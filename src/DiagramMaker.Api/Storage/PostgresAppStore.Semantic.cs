using System.Text.Json;
using DiagramMaker.Domain;
using NpgsqlTypes;

namespace DiagramMaker.Storage;

public sealed partial class PostgresAppStore
{
    public async Task<bool> UpdateAnalysisAsync(AnalysisJob job, int expectedRevision, CancellationToken ct)
    {
        if (job.Revision != expectedRevision + 1) return false;
        await using var command = _dataSource.CreateCommand("""
            UPDATE analysis_jobs SET state=$2, payload=$3, updated_at=$4,
                lease_until=CASE WHEN $2 IN ('Completed','Partial','Failed','Cancelled') THEN NULL ELSE lease_until END
            WHERE id=$1 AND COALESCE((payload->>'revision')::int,1)=$5
                AND (payload->>'leaseId') IS NOT DISTINCT FROM $6
                AND (state NOT IN ('Completed','Partial','Failed','Cancelled') OR
                    ($2='Queued' AND state IN ('Partial','Failed','Cancelled')))
            """);
        command.Parameters.AddWithValue(job.Id);
        command.Parameters.AddWithValue(job.State.ToString());
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(job, JsonOptions));
        command.Parameters.AddWithValue(job.UpdatedAt);
        command.Parameters.AddWithValue(expectedRevision);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)job.LeaseId?.ToString() ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> RenewAnalysisLeaseAsync(Guid id, Guid leaseId, TimeSpan duration, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE analysis_jobs SET lease_until=$3, payload=jsonb_set(payload,'{leaseUntil}',to_jsonb($3::timestamptz))
            WHERE id=$1 AND payload->>'leaseId'=$2 AND state NOT IN ('Completed','Partial','Failed','Cancelled')
            """);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(leaseId.ToString());
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow.Add(duration));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
