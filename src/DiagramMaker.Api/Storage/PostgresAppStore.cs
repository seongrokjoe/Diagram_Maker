using System.Text.Json;
using DiagramMaker.Domain;
using Npgsql;
using NpgsqlTypes;

namespace DiagramMaker.Storage;

public sealed class PostgresAppStore : IAppStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAppStore(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS repositories (
                id uuid PRIMARY KEY,
                definition jsonb NOT NULL,
                created_at timestamptz NOT NULL
            );
            CREATE TABLE IF NOT EXISTS analysis_jobs (
                id uuid PRIMARY KEY,
                state text NOT NULL,
                payload jsonb NOT NULL,
                analysis_plan_id uuid NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                lease_until timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analysis_jobs_queue
                ON analysis_jobs (state, created_at);
            ALTER TABLE analysis_jobs ADD COLUMN IF NOT EXISTS analysis_plan_id uuid NULL;
            UPDATE analysis_jobs
            SET analysis_plan_id = (payload #>> '{request,analysisPlanId}')::uuid
            WHERE analysis_plan_id IS NULL
              AND COALESCE(payload #>> '{request,analysisPlanId}', '') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$';
            CREATE INDEX IF NOT EXISTS ix_analysis_jobs_plan_created
                ON analysis_jobs (analysis_plan_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS analysis_plans (
                id uuid PRIMARY KEY,
                state text NOT NULL,
                payload jsonb NOT NULL,
                owner_user_id text NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                expires_at timestamptz NOT NULL,
                lease_until timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_analysis_plans_queue ON analysis_plans (state, created_at);
            CREATE INDEX IF NOT EXISTS ix_analysis_plans_owner ON analysis_plans (owner_user_id, created_at DESC);
            CREATE TABLE IF NOT EXISTS natural_diagrams (
                id uuid PRIMARY KEY,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                owner_user_id text NULL,
                root_id uuid NULL,
                parent_id uuid NULL,
                revision integer NOT NULL DEFAULT 1
            );
            ALTER TABLE natural_diagrams ADD COLUMN IF NOT EXISTS owner_user_id text NULL;
            ALTER TABLE natural_diagrams ADD COLUMN IF NOT EXISTS root_id uuid NULL;
            ALTER TABLE natural_diagrams ADD COLUMN IF NOT EXISTS parent_id uuid NULL;
            ALTER TABLE natural_diagrams ADD COLUMN IF NOT EXISTS revision integer NOT NULL DEFAULT 1;
            CREATE INDEX IF NOT EXISTS ix_natural_diagrams_owner_created ON natural_diagrams (owner_user_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_natural_diagrams_root_revision ON natural_diagrams (root_id, revision);
            CREATE TABLE IF NOT EXISTS diagram_revisions (
                id uuid PRIMARY KEY,
                root_artifact_id uuid NOT NULL,
                owner_user_id text NOT NULL,
                revision integer NOT NULL,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_diagram_revisions_root_revision
                ON diagram_revisions (root_artifact_id, owner_user_id, revision);
            CREATE TABLE IF NOT EXISTS audit_events (
                id uuid PRIMARY KEY,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL
            );
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RepositoryDefinition>> ListRepositoriesAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT definition::text FROM repositories ORDER BY definition->>'name'";
        var values = new List<RepositoryDefinition>();
        await using var command = _dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(Deserialize<RepositoryDefinition>(reader.GetString(0)));
        }

        return values;
    }

    public Task<RepositoryDefinition?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken) =>
        GetByIdAsync<RepositoryDefinition>("repositories", "definition", id, cancellationToken);

    public async Task SaveRepositoryAsync(RepositoryDefinition repository, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO repositories (id, definition, created_at)
            VALUES ($1, $2, $3)
            ON CONFLICT (id) DO UPDATE SET definition = EXCLUDED.definition
            """;
        await ExecuteJsonUpsertAsync(sql, repository.Id, repository, repository.CreatedAt, cancellationToken);
    }

    public async Task SaveAnalysisAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO analysis_jobs (id, state, payload, analysis_plan_id, created_at, updated_at, lease_until)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (id) DO UPDATE SET
                state = EXCLUDED.state,
                payload = EXCLUDED.payload,
                analysis_plan_id = EXCLUDED.analysis_plan_id,
                updated_at = EXCLUDED.updated_at,
                lease_until = EXCLUDED.lease_until
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(job.Id);
        command.Parameters.AddWithValue(job.State.ToString());
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(job, JsonOptions));
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)job.Request.AnalysisPlanId ?? DBNull.Value);
        command.Parameters.AddWithValue(job.CreatedAt);
        command.Parameters.AddWithValue(job.UpdatedAt);
        command.Parameters.AddWithValue((object?)job.LeaseUntil ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<AnalysisJob?> GetAnalysisAsync(Guid id, CancellationToken cancellationToken) =>
        GetByIdAsync<AnalysisJob>("analysis_jobs", "payload", id, cancellationToken);

    public async Task<IReadOnlyList<AnalysisJob>> ListAnalysesByPlanAsync(Guid planId, int limit, CancellationToken cancellationToken)
    {
        const string sql = "SELECT payload::text FROM analysis_jobs WHERE analysis_plan_id=$1 ORDER BY created_at DESC LIMIT $2";
        var result = new List<AnalysisJob>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(planId);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<AnalysisJob>(reader.GetString(0)));
        return result;
    }

    public async Task<AnalysisJob?> TryLeaseAnalysisAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string selectSql = """
            SELECT id, payload::text
            FROM analysis_jobs
            WHERE state = 'Queued'
               OR (lease_until < now() AND state NOT IN ('Completed', 'Partial', 'Failed'))
            ORDER BY created_at
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """;
        await using var select = new NpgsqlCommand(selectSql, connection, transaction);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var id = reader.GetGuid(0);
        var job = Deserialize<AnalysisJob>(reader.GetString(1));
        await reader.CloseAsync();

        var now = DateTimeOffset.UtcNow;
        var leased = job with
        {
            State = AnalysisState.Resolving,
            Progress = 5,
            StageMessage = "Resolving immutable revisions",
            LeaseUntil = now.Add(leaseDuration),
            UpdatedAt = now
        };

        const string updateSql = "UPDATE analysis_jobs SET state=$2, payload=$3, updated_at=$4, lease_until=$5 WHERE id=$1";
        await using var update = new NpgsqlCommand(updateSql, connection, transaction);
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(leased.State.ToString());
        update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(leased, JsonOptions));
        update.Parameters.AddWithValue(now);
        update.Parameters.AddWithValue(leased.LeaseUntil!.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task SaveAnalysisPlanAsync(AnalysisPlan plan, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO analysis_plans (id, state, payload, owner_user_id, created_at, updated_at, expires_at, lease_until)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (id) DO UPDATE SET state=EXCLUDED.state, payload=EXCLUDED.payload,
                owner_user_id=EXCLUDED.owner_user_id, updated_at=EXCLUDED.updated_at,
                expires_at=EXCLUDED.expires_at, lease_until=EXCLUDED.lease_until
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(plan.Id);
        command.Parameters.AddWithValue(plan.State.ToString());
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(plan, JsonOptions));
        command.Parameters.AddWithValue(plan.OwnerUserId);
        command.Parameters.AddWithValue(plan.CreatedAt);
        command.Parameters.AddWithValue(plan.UpdatedAt);
        command.Parameters.AddWithValue(plan.ExpiresAt);
        command.Parameters.AddWithValue((object?)plan.LeaseUntil ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<AnalysisPlan?> GetAnalysisPlanAsync(Guid id, CancellationToken cancellationToken) =>
        GetByIdAsync<AnalysisPlan>("analysis_plans", "payload", id, cancellationToken);

    public async Task<IReadOnlyList<AnalysisPlan>> ListAnalysisPlansAsync(string ownerUserId, int limit, CancellationToken cancellationToken)
    {
        const string sql = "SELECT payload::text FROM analysis_plans WHERE owner_user_id=$1 AND expires_at > now() ORDER BY created_at DESC LIMIT $2";
        var values = new List<AnalysisPlan>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(ownerUserId);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) values.Add(Deserialize<AnalysisPlan>(reader.GetString(0)));
        return values;
    }

    public async Task<AnalysisPlan?> TryLeaseAnalysisPlanAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string selectSql = """
            SELECT id, payload::text FROM analysis_plans
            WHERE expires_at > now() AND (state='Queued' OR (lease_until < now() AND state IN ('Indexing','Grouping')))
            ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """;
        await using var select = new NpgsqlCommand(selectSql, connection, transaction);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var id = reader.GetGuid(0);
        var plan = Deserialize<AnalysisPlan>(reader.GetString(1));
        await reader.CloseAsync();
        var now = DateTimeOffset.UtcNow;
        var leased = plan with { State = AnalysisPlanState.Indexing, Progress = 5, StageMessage = "Resolving immutable revisions", LeaseUntil = now.Add(leaseDuration), UpdatedAt = now };
        const string updateSql = "UPDATE analysis_plans SET state=$2, payload=$3, updated_at=$4, lease_until=$5 WHERE id=$1";
        await using var update = new NpgsqlCommand(updateSql, connection, transaction);
        update.Parameters.AddWithValue(id);
        update.Parameters.AddWithValue(leased.State.ToString());
        update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(leased, JsonOptions));
        update.Parameters.AddWithValue(now);
        update.Parameters.AddWithValue(leased.LeaseUntil!.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task SaveNaturalDiagramAsync(NaturalDiagramRecord record, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO natural_diagrams (id, payload, created_at, owner_user_id, root_id, parent_id, revision)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (id) DO UPDATE SET payload = EXCLUDED.payload, owner_user_id = EXCLUDED.owner_user_id,
                root_id = EXCLUDED.root_id, parent_id = EXCLUDED.parent_id, revision = EXCLUDED.revision
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(record.Id);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(record, JsonOptions));
        command.Parameters.AddWithValue(record.CreatedAt);
        command.Parameters.AddWithValue(record.OwnerUserId);
        command.Parameters.AddWithValue(record.RootDiagramId ?? record.Id);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)record.ParentDiagramId ?? DBNull.Value);
        command.Parameters.AddWithValue(record.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<NaturalDiagramRecord?> GetNaturalDiagramAsync(Guid id, CancellationToken cancellationToken) =>
        GetByIdAsync<NaturalDiagramRecord>("natural_diagrams", "payload", id, cancellationToken);

    public async Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramsAsync(string ownerUserId, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text FROM (
                SELECT DISTINCT ON (COALESCE(root_id, id)) payload, created_at, revision
                FROM natural_diagrams WHERE owner_user_id=$1
                ORDER BY COALESCE(root_id, id), revision DESC
            ) latest ORDER BY created_at DESC LIMIT $2
            """;
        var result = new List<NaturalDiagramRecord>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(ownerUserId);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<NaturalDiagramRecord>(reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramRevisionsAsync(Guid rootDiagramId, string ownerUserId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT payload::text FROM natural_diagrams WHERE root_id=$1 AND owner_user_id=$2 ORDER BY revision";
        var result = new List<NaturalDiagramRecord>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(rootDiagramId);
        command.Parameters.AddWithValue(ownerUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<NaturalDiagramRecord>(reader.GetString(0)));
        return result;
    }

    public async Task SaveDiagramRevisionAsync(DiagramRevisionRecord record, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO diagram_revisions (id, root_artifact_id, owner_user_id, revision, payload, created_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (id) DO UPDATE SET payload=EXCLUDED.payload, revision=EXCLUDED.revision
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(record.Id);
        command.Parameters.AddWithValue(record.RootArtifactId);
        command.Parameters.AddWithValue(record.OwnerUserId);
        command.Parameters.AddWithValue(record.Version);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(record, JsonOptions));
        command.Parameters.AddWithValue(record.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<DiagramRevisionRecord?> GetDiagramRevisionAsync(Guid id, CancellationToken cancellationToken) =>
        GetByIdAsync<DiagramRevisionRecord>("diagram_revisions", "payload", id, cancellationToken);

    public async Task<IReadOnlyList<DiagramRevisionRecord>> ListDiagramRevisionsAsync(Guid rootArtifactId, string ownerUserId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT payload::text FROM diagram_revisions WHERE root_artifact_id=$1 AND owner_user_id=$2 ORDER BY revision";
        var result = new List<DiagramRevisionRecord>();
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(rootArtifactId);
        command.Parameters.AddWithValue(ownerUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize<DiagramRevisionRecord>(reader.GetString(0)));
        return result;
    }

    public async Task SaveAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        const string sql = "INSERT INTO audit_events (id, payload, created_at) VALUES ($1, $2, $3)";
        await ExecuteJsonUpsertAsync(sql, auditEvent.Id, auditEvent, auditEvent.CreatedAt, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    private async Task<T?> GetByIdAsync<T>(string table, string column, Guid id, CancellationToken cancellationToken)
    {
        var allowed = table switch
        {
            "repositories" when column == "definition" => true,
            "analysis_jobs" when column == "payload" => true,
            "analysis_plans" when column == "payload" => true,
            "natural_diagrams" when column == "payload" => true,
            "diagram_revisions" when column == "payload" => true,
            _ => false
        };
        if (!allowed)
        {
            throw new InvalidOperationException("Invalid storage query target.");
        }

        await using var command = _dataSource.CreateCommand($"SELECT {column}::text FROM {table} WHERE id=$1");
        command.Parameters.AddWithValue(id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize<T>(json) : default;
    }

    private async Task ExecuteJsonUpsertAsync<T>(string sql, Guid id, T value, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(value, JsonOptions));
        command.Parameters.AddWithValue(createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new InvalidOperationException($"Stored {typeof(T).Name} payload is invalid.");
}
