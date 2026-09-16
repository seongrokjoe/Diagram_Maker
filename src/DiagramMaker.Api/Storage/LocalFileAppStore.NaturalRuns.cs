using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class LocalFileAppStore
{
    private readonly SemaphoreSlim _naturalRunFileGate = new(1, 1);

    private Task InitializeNaturalRunsAsync(CancellationToken cancellationToken) =>
        LoadRecordsAsync(_naturalRunDirectory, async json =>
        {
            var run = Deserialize<NaturalDiagramRun>(json);
            _inner.RestoreNaturalRun(run);
            await Task.CompletedTask;
        }, cancellationToken);

    public async Task<bool> CreateNaturalDiagramRunAsync(NaturalDiagramRun run, CancellationToken cancellationToken)
    {
        await _naturalRunFileGate.WaitAsync(cancellationToken);
        try
        {
            if (!await _inner.CreateNaturalDiagramRunAsync(run, cancellationToken)) return false;
            await PersistRecordAsync(_naturalRunDirectory, run.Id, run, cancellationToken);
            return true;
        }
        finally { _naturalRunFileGate.Release(); }
    }

    public Task<NaturalDiagramRun?> GetNaturalDiagramRunAsync(Guid id, CancellationToken cancellationToken) =>
        _inner.GetNaturalDiagramRunAsync(id, cancellationToken);

    public Task<IReadOnlyList<NaturalDiagramRun>> ListNaturalDiagramRunsAsync(string ownerUserId, int limit, CancellationToken cancellationToken) =>
        _inner.ListNaturalDiagramRunsAsync(ownerUserId, limit, cancellationToken);

    public async Task<bool> UpdateNaturalDiagramRunAsync(NaturalDiagramRun run, int expectedRevision, Guid? expectedLeaseId, CancellationToken cancellationToken)
    {
        await _naturalRunFileGate.WaitAsync(cancellationToken);
        try
        {
            if (!await _inner.UpdateNaturalDiagramRunAsync(run, expectedRevision, expectedLeaseId, cancellationToken)) return false;
            await PersistRecordAsync(_naturalRunDirectory, run.Id,
                (await _inner.GetNaturalDiagramRunAsync(run.Id, cancellationToken))!, cancellationToken);
            return true;
        }
        finally { _naturalRunFileGate.Release(); }
    }

    public async Task<NaturalDiagramRun?> TryLeaseNaturalDiagramRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await _naturalRunFileGate.WaitAsync(cancellationToken);
        try
        {
            var run = await _inner.TryLeaseNaturalDiagramRunAsync(leaseDuration, cancellationToken);
            if (run is not null) await PersistRecordAsync(_naturalRunDirectory, run.Id, run, cancellationToken);
            return run;
        }
        finally { _naturalRunFileGate.Release(); }
    }

    public async Task<bool> RenewNaturalDiagramRunLeaseAsync(Guid id, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await _naturalRunFileGate.WaitAsync(cancellationToken);
        try
        {
            if (!await _inner.RenewNaturalDiagramRunLeaseAsync(id, leaseId, leaseDuration, cancellationToken)) return false;
            await PersistRecordAsync(_naturalRunDirectory, id,
                (await _inner.GetNaturalDiagramRunAsync(id, cancellationToken))!, cancellationToken);
            return true;
        }
        finally { _naturalRunFileGate.Release(); }
    }
}
