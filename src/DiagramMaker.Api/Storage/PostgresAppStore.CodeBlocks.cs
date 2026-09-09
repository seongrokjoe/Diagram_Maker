using System.Text.Json;
using DiagramMaker.Domain;
using Npgsql;
using NpgsqlTypes;

namespace DiagramMaker.Storage;

public sealed partial class PostgresAppStore
{
    private const string ActiveCodeStates = "('Queued','Indexing','NeedsClarification','Generating')";
    private async Task InitializeCodeBlocksAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS code_block_workspaces (
                id uuid PRIMARY KEY, owner_user_id text NOT NULL, revision integer NOT NULL,
                payload jsonb NOT NULL, updated_at timestamptz NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_code_workspaces_owner ON code_block_workspaces(owner_user_id,updated_at DESC);
            CREATE TABLE IF NOT EXISTS code_block_runs (
                id uuid PRIMARY KEY, workspace_id uuid NOT NULL REFERENCES code_block_workspaces(id) ON DELETE CASCADE,
                owner_user_id text NOT NULL, revision integer NOT NULL, state text NOT NULL, payload jsonb NOT NULL,
                created_at timestamptz NOT NULL, lease_until timestamptz NULL, lease_id uuid NULL);
            CREATE INDEX IF NOT EXISTS ix_code_runs_workspace ON code_block_runs(workspace_id,created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_code_runs_queue ON code_block_runs(state,created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_code_runs_one_active ON code_block_runs(workspace_id)
                WHERE state IN ('Queued','Indexing','NeedsClarification','Generating');
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T?> ReadCodeRecordAsync<T>(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize<T>(json) : default;
    }
    private static async Task<IReadOnlyList<T>> ReadCodeRecordsAsync<T>(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var values = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(Deserialize<T>(reader.GetString(0)));
        return values;
    }
    private static void JsonParameter<T>(NpgsqlCommand command, T value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(value, JsonOptions));

    public async Task<CodeBlockWorkspace?> GetCodeBlockWorkspaceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM code_block_workspaces WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await ReadCodeRecordAsync<CodeBlockWorkspace>(command, cancellationToken);
    }
    public async Task<IReadOnlyList<CodeBlockWorkspace>> ListCodeBlockWorkspacesAsync(string ownerUserId, int limit, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM code_block_workspaces WHERE owner_user_id=$1 ORDER BY updated_at DESC LIMIT $2");
        command.Parameters.AddWithValue(ownerUserId); command.Parameters.AddWithValue(limit);
        return await ReadCodeRecordsAsync<CodeBlockWorkspace>(command, cancellationToken);
    }
    public async Task<bool> SaveCodeBlockWorkspaceAsync(CodeBlockWorkspace workspace, int expectedRevision, CancellationToken cancellationToken)
    {
        if (workspace.Revision != expectedRevision + 1) return false;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(expectedRevision == 0
            ? "INSERT INTO code_block_workspaces(id,owner_user_id,revision,payload,updated_at) VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING"
            : "UPDATE code_block_workspaces SET revision=$3,payload=$4,updated_at=$5 WHERE id=$1 AND owner_user_id=$2 AND revision=$6", connection, transaction);
        command.Parameters.AddWithValue(workspace.Id); command.Parameters.AddWithValue(workspace.OwnerUserId);
        command.Parameters.AddWithValue(workspace.Revision); JsonParameter(command, workspace); command.Parameters.AddWithValue(workspace.UpdatedAt);
        if (expectedRevision != 0) command.Parameters.AddWithValue(expectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await using var select = new NpgsqlCommand($"SELECT payload::text FROM code_block_runs WHERE workspace_id=$1 AND state IN {ActiveCodeStates} FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue(workspace.Id);
        foreach (var run in await ReadCodeRecordsAsync<CodeBlockRun>(select, cancellationToken))
            await WriteCodeRunAsync(connection, transaction, CodeBlockStoreRules.Cancel(run), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    public async Task<bool> DeleteCodeBlockWorkspaceAsync(Guid id, string ownerUserId, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var locked = new NpgsqlCommand("SELECT payload::text FROM code_block_workspaces WHERE id=$1 AND owner_user_id=$2 AND revision=$3 FOR UPDATE", connection, transaction);
        locked.Parameters.AddWithValue(id); locked.Parameters.AddWithValue(ownerUserId); locked.Parameters.AddWithValue(expectedRevision);
        if (await locked.ExecuteScalarAsync(cancellationToken) is null) return false;
        await using var revisions = new NpgsqlCommand("DELETE FROM diagram_revisions WHERE payload->>'sourceKind'='code-block' AND payload->>'sourceId' IN (SELECT id::text FROM code_block_runs WHERE workspace_id=$1)", connection, transaction);
        revisions.Parameters.AddWithValue(id); await revisions.ExecuteNonQueryAsync(cancellationToken);
        await using var remove = new NpgsqlCommand("DELETE FROM code_block_workspaces WHERE id=$1", connection, transaction);
        remove.Parameters.AddWithValue(id); await remove.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    public async Task<CodeBlockRun?> GetCodeBlockRunAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM code_block_runs WHERE id=$1");
        command.Parameters.AddWithValue(id);
        return await ReadCodeRecordAsync<CodeBlockRun>(command, cancellationToken);
    }
    public async Task<IReadOnlyList<CodeBlockRun>> ListCodeBlockRunsAsync(Guid workspaceId, int limit, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("SELECT payload::text FROM code_block_runs WHERE workspace_id=$1 ORDER BY created_at DESC LIMIT $2");
        command.Parameters.AddWithValue(workspaceId); command.Parameters.AddWithValue(limit);
        return await ReadCodeRecordsAsync<CodeBlockRun>(command, cancellationToken);
    }
    public async Task<bool> CreateCodeBlockRunAsync(CodeBlockRun run, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var locked = new NpgsqlCommand("SELECT revision FROM code_block_workspaces WHERE id=$1 AND owner_user_id=$2 FOR UPDATE", connection, transaction);
        locked.Parameters.AddWithValue(run.WorkspaceId); locked.Parameters.AddWithValue(run.OwnerUserId);
        if (await locked.ExecuteScalarAsync(cancellationToken) is not int version || version != run.InputRevision) return false;
        await using var command = new NpgsqlCommand("INSERT INTO code_block_runs(id,workspace_id,owner_user_id,revision,state,payload,created_at) VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING", connection, transaction);
        command.Parameters.AddWithValue(run.Id); command.Parameters.AddWithValue(run.WorkspaceId); command.Parameters.AddWithValue(run.OwnerUserId);
        command.Parameters.AddWithValue(run.Revision); command.Parameters.AddWithValue(run.State.ToString()); JsonParameter(command, run);
        command.Parameters.AddWithValue(run.CreatedAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    public async Task<bool> SaveCodeBlockRunAsync(CodeBlockRun run, int expectedRevision, CancellationToken cancellationToken)
    {
        if (run.Revision != expectedRevision + 1) return false;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Always lock workspace before run, matching update/delete/enqueue lock order.
        await using var workspace = new NpgsqlCommand("SELECT revision FROM code_block_workspaces WHERE id=$1 AND owner_user_id=$2 FOR SHARE", connection, transaction);
        workspace.Parameters.AddWithValue(run.WorkspaceId); workspace.Parameters.AddWithValue(run.OwnerUserId);
        if (await workspace.ExecuteScalarAsync(cancellationToken) is not int version || version != run.InputRevision) return false;
        await using var locked = new NpgsqlCommand("SELECT payload::text FROM code_block_runs WHERE id=$1 FOR UPDATE", connection, transaction);
        locked.Parameters.AddWithValue(run.Id);
        var current = await ReadCodeRecordAsync<CodeBlockRun>(locked, cancellationToken);
        if (current is null || current.IsTerminal || current.Revision != expectedRevision || current.LeaseId != run.LeaseId ||
            current.WorkspaceId != run.WorkspaceId || current.OwnerUserId != run.OwnerUserId) return false;
        var finished = run.IsTerminal || run.State == CodeBlockRunState.NeedsClarification;
        await WriteCodeRunAsync(connection, transaction, run with { LeaseUntil = finished ? null : current.LeaseUntil,
            LeaseId = finished ? null : current.LeaseId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    public async Task<CodeBlockRun?> TryLeaseCodeBlockRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand("SELECT payload::text FROM code_block_runs WHERE state='Queued' OR (state IN ('Indexing','Generating') AND (lease_until IS NULL OR lease_until < now())) ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED", connection, transaction);
        var run = await ReadCodeRecordAsync<CodeBlockRun>(select, cancellationToken);
        if (run is null) return null;
        var leased = CodeBlockStoreRules.Lease(run, leaseDuration);
        await WriteCodeRunAsync(connection, transaction, leased, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return leased;
    }
    public async Task<bool> RenewCodeBlockLeaseAsync(Guid runId, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var select = new NpgsqlCommand("SELECT payload::text FROM code_block_runs WHERE id=$1 AND lease_id=$2 AND state IN ('Indexing','Generating') FOR UPDATE", connection, transaction);
        select.Parameters.AddWithValue(runId); select.Parameters.AddWithValue(leaseId);
        var run = await ReadCodeRecordAsync<CodeBlockRun>(select, cancellationToken);
        if (run is null) return false;
        await WriteCodeRunAsync(connection, transaction, run with { LeaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration) }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
    private static async Task WriteCodeRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CodeBlockRun run, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("UPDATE code_block_runs SET revision=$2,state=$3,payload=$4,lease_until=$5,lease_id=$6 WHERE id=$1", connection, transaction);
        command.Parameters.AddWithValue(run.Id); command.Parameters.AddWithValue(run.Revision); command.Parameters.AddWithValue(run.State.ToString());
        JsonParameter(command, run);
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)run.LeaseUntil ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)run.LeaseId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
