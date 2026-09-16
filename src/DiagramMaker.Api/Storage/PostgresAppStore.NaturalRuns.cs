using System.Text.Json;
using DiagramMaker.Domain;
using Npgsql;
using NpgsqlTypes;

namespace DiagramMaker.Storage;

public sealed partial class PostgresAppStore
{
    private async Task InitializeNaturalRunsAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS natural_diagram_runs (
                id uuid PRIMARY KEY,
                owner_user_id text NOT NULL,
                revision integer NOT NULL,
                state text NOT NULL,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                lease_until timestamptz NULL,
                lease_id uuid NULL
            );
            CREATE INDEX IF NOT EXISTS ix_natural_runs_owner ON natural_diagram_runs(owner_user_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_natural_runs_queue ON natural_diagram_runs(state, created_at);
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CreateNaturalDiagramRunAsync(NaturalDiagramRun run, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO natural_diagram_runs(id,owner_user_id,revision,state,payload,created_at,updated_at,lease_until,lease_id)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT DO NOTHING
            """);
        AddNaturalRunParameters(command, run);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<NaturalDiagramRun?> GetNaturalDiagramRunAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM natural_diagram_runs WHERE id=$1");
        command.Parameters.AddWithValue(id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize<NaturalDiagramRun>(json) : null;
    }

    public async Task<IReadOnlyList<NaturalDiagramRun>> ListNaturalDiagramRunsAsync(string ownerUserId, int limit, CancellationToken cancellationToken)
    {
        var result = new List<NaturalDiagramRun>();
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM natural_diagram_runs WHERE owner_user_id=$1 ORDER BY created_at DESC LIMIT $2");
        command.Parameters.AddWithValue(ownerUserId);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<NaturalDiagramRun>(reader.GetString(0)));
        return result;
    }

    public async Task<bool> UpdateNaturalDiagramRunAsync(NaturalDiagramRun run, int expectedRevision, Guid? expectedLeaseId, CancellationToken cancellationToken)
    {
        if (run.Revision != expectedRevision + 1) return false;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand("SELECT payload::text FROM natural_diagram_runs WHERE id=$1 FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue(run.Id);
        var value = await select.ExecuteScalarAsync(cancellationToken);
        var current = value is string json ? Deserialize<NaturalDiagramRun>(json) : null;
        if (current is null || current.Revision != expectedRevision || current.OwnerUserId != run.OwnerUserId ||
            expectedLeaseId is not null && (current.IsTerminal || current.LeaseId != expectedLeaseId)) return false;
        var stored = run with
        {
            LeaseId = run.IsTerminal || run.State == NaturalDiagramRunState.Queued ? null : current.LeaseId,
            LeaseUntil = run.IsTerminal || run.State == NaturalDiagramRunState.Queued ? null : current.LeaseUntil
        };
        await WriteNaturalRunAsync(connection, transaction, stored, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<NaturalDiagramRun?> TryLeaseNaturalDiagramRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand("SELECT payload::text FROM natural_diagram_runs WHERE state='Queued' OR (state='Generating' AND (lease_until IS NULL OR lease_until < now())) ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED", connection, transaction);
        var value = await select.ExecuteScalarAsync(cancellationToken);
        if (value is not string json) return null;
        var run = Deserialize<NaturalDiagramRun>(json);
        var now = DateTimeOffset.UtcNow;
        var leased = run with { State = NaturalDiagramRunState.Generating, Progress = Math.Max(5, run.Progress),
            StageMessage = "요구사항과 시나리오 준비", Revision = run.Revision + 1, UpdatedAt = now,
            LeaseId = Guid.NewGuid(), LeaseUntil = now.Add(leaseDuration) };
        await WriteNaturalRunAsync(connection, transaction, leased, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task<bool> RenewNaturalDiagramRunLeaseAsync(Guid id, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand("SELECT payload::text FROM natural_diagram_runs WHERE id=$1 AND lease_id=$2 AND state='Generating' FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue(id);
        select.Parameters.AddWithValue(leaseId);
        var value = await select.ExecuteScalarAsync(cancellationToken);
        if (value is not string json) return false;
        var run = Deserialize<NaturalDiagramRun>(json) with { LeaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration) };
        await WriteNaturalRunAsync(connection, transaction, run, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task WriteNaturalRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        NaturalDiagramRun run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE natural_diagram_runs SET revision=$3,state=$4,payload=$5,created_at=$6,updated_at=$7,lease_until=$8,lease_id=$9 WHERE id=$1 AND owner_user_id=$2", connection, transaction);
        AddNaturalRunParameters(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNaturalRunParameters(NpgsqlCommand command, NaturalDiagramRun run)
    {
        command.Parameters.AddWithValue(run.Id);
        command.Parameters.AddWithValue(run.OwnerUserId);
        command.Parameters.AddWithValue(run.Revision);
        command.Parameters.AddWithValue(run.State.ToString());
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(run, JsonOptions));
        command.Parameters.AddWithValue(run.CreatedAt);
        command.Parameters.AddWithValue(run.UpdatedAt);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)run.LeaseUntil ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)run.LeaseId ?? DBNull.Value);
    }
}
