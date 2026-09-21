using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class LocalFileAppStore
{
    private readonly SemaphoreSlim _naturalRunFileGate = new(1, 1);

    private Task InitializeNaturalRunsAsync(CancellationToken cancellationToken) =>
        LoadRecordsAsync(_naturalRunDirectory, json =>
        {
            _inner.RestoreNaturalRun(Deserialize<NaturalDiagramRun>(json));
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<bool> CreateNaturalDiagramRunAsync(NaturalDiagramRun run, CancellationToken ct) =>
        MutateNaturalAsync(() => _inner.CreateNaturalDiagramRunAsync(run, ct), ct);

    public async Task<NaturalDiagramRun?> GetNaturalDiagramRunAsync(Guid id, CancellationToken ct)
    {
        await _naturalRunFileGate.WaitAsync(ct);
        try { return await _inner.GetNaturalDiagramRunAsync(id, ct); }
        finally { _naturalRunFileGate.Release(); }
    }

    public async Task<IReadOnlyList<NaturalDiagramRun>> ListNaturalDiagramRunsAsync(string ownerUserId, int limit, CancellationToken ct)
    {
        await _naturalRunFileGate.WaitAsync(ct);
        try { return await _inner.ListNaturalDiagramRunsAsync(ownerUserId, limit, ct); }
        finally { _naturalRunFileGate.Release(); }
    }

    public Task<bool> UpdateNaturalDiagramRunAsync(NaturalDiagramRun run, int expectedRevision, Guid? expectedLeaseId, CancellationToken ct) =>
        MutateNaturalAsync(() => _inner.UpdateNaturalDiagramRunAsync(run, expectedRevision, expectedLeaseId, ct), ct);

    public Task<NaturalDiagramRun?> TryLeaseNaturalDiagramRunAsync(TimeSpan leaseDuration, CancellationToken ct) =>
        MutateNaturalAsync(() => _inner.TryLeaseNaturalDiagramRunAsync(leaseDuration, ct), ct);

    public Task<bool> RenewNaturalDiagramRunLeaseAsync(Guid id, Guid leaseId, TimeSpan leaseDuration, CancellationToken ct) =>
        MutateNaturalAsync(() => _inner.RenewNaturalDiagramRunLeaseAsync(id, leaseId, leaseDuration, ct), ct);

    private async Task<T> MutateNaturalAsync<T>(Func<Task<T>> mutation, CancellationToken ct)
    {
        await _naturalRunFileGate.WaitAsync(ct);
        var snapshot = _inner.SnapshotNaturalRuns();
        try
        {
            var result = await mutation();
            // Natural run operations change at most one file. Publish in-memory state only after atomic persistence.
            foreach (var run in _inner.SnapshotNaturalRuns())
                if (!snapshot.Any(old => old == run)) await PersistRecordAsync(_naturalRunDirectory, run.Id, run, ct);
            return result;
        }
        catch
        {
            _inner.RestoreNaturalRuns(snapshot);
            throw;
        }
        finally { _naturalRunFileGate.Release(); }
    }
}
