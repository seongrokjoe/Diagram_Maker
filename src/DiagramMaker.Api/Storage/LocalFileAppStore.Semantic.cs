using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class LocalFileAppStore
{
    private readonly SemaphoreSlim _analysisFileGate = new(1, 1);
    public async Task<bool> UpdateAnalysisAsync(AnalysisJob job, int expectedRevision, CancellationToken ct)
    {
        await _analysisFileGate.WaitAsync(ct);
        try
        {
            if (!await _inner.UpdateAnalysisAsync(job, expectedRevision, ct)) return false;
            await PersistRecordAsync(_analysisDirectory, job.Id, (await _inner.GetAnalysisAsync(job.Id, ct))!, ct);
            return true;
        }
        finally { _analysisFileGate.Release(); }
    }
    public async Task<bool> RenewAnalysisLeaseAsync(Guid id, Guid leaseId, TimeSpan duration, CancellationToken ct)
    {
        await _analysisFileGate.WaitAsync(ct);
        try
        {
            if (!await _inner.RenewAnalysisLeaseAsync(id, leaseId, duration, ct)) return false;
            await PersistRecordAsync(_analysisDirectory, id, (await _inner.GetAnalysisAsync(id, ct))!, ct);
            return true;
        }
        finally { _analysisFileGate.Release(); }
    }
}
