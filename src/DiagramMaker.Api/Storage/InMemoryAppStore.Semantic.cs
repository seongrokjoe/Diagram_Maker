using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class InMemoryAppStore
{
    public async Task<bool> UpdateAnalysisAsync(AnalysisJob job, int expectedRevision, CancellationToken cancellationToken)
    {
        await _leaseLock.WaitAsync(cancellationToken);
        try
        {
            if (!_analyses.TryGetValue(job.Id, out var current) || current.Revision != expectedRevision ||
                current.LeaseId != job.LeaseId || job.Revision != expectedRevision + 1 ||
                IsAnalysisTerminal(current.State) && (current.State == AnalysisState.Completed || job.State != AnalysisState.Queued)) return false;
            _analyses[job.Id] = job with { LeaseUntil = IsAnalysisTerminal(job.State) ? null : current.LeaseUntil };
            return true;
        }
        finally { _leaseLock.Release(); }
    }

    public async Task<bool> RenewAnalysisLeaseAsync(Guid id, Guid leaseId, TimeSpan duration, CancellationToken cancellationToken)
    {
        await _leaseLock.WaitAsync(cancellationToken);
        try
        {
            if (!_analyses.TryGetValue(id, out var job) || job.LeaseId != leaseId || IsAnalysisTerminal(job.State)) return false;
            _analyses[id] = job with { LeaseUntil = DateTimeOffset.UtcNow.Add(duration) };
            return true;
        }
        finally { _leaseLock.Release(); }
    }

    internal static bool IsAnalysisTerminal(AnalysisState state) => state is AnalysisState.Completed or AnalysisState.Partial or AnalysisState.Failed or AnalysisState.Cancelled;
}
