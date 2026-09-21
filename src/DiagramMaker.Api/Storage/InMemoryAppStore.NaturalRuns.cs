using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class InMemoryAppStore
{
    private readonly object _naturalRunGate = new();
    private readonly Dictionary<Guid, NaturalDiagramRun> _naturalRuns = [];

    public Task<bool> CreateNaturalDiagramRunAsync(NaturalDiagramRun run, CancellationToken cancellationToken)
    {
        lock (_naturalRunGate)
        {
            if (_naturalRuns.ContainsKey(run.Id) || run.Revision != 1 || run.State != NaturalDiagramRunState.Queued)
                return Task.FromResult(false);
            _naturalRuns.Add(run.Id, run);
            return Task.FromResult(true);
        }
    }

    public Task<NaturalDiagramRun?> GetNaturalDiagramRunAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_naturalRunGate) return Task.FromResult(_naturalRuns.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<NaturalDiagramRun>> ListNaturalDiagramRunsAsync(string ownerUserId, int limit, CancellationToken cancellationToken)
    {
        lock (_naturalRunGate) return Task.FromResult<IReadOnlyList<NaturalDiagramRun>>(_naturalRuns.Values
            .Where(run => run.OwnerUserId.Equals(ownerUserId, StringComparison.Ordinal))
            .OrderByDescending(run => run.CreatedAt).Take(limit).ToArray());
    }

    public Task<bool> UpdateNaturalDiagramRunAsync(NaturalDiagramRun run, int expectedRevision, Guid? expectedLeaseId, CancellationToken cancellationToken)
    {
        lock (_naturalRunGate)
        {
            if (!_naturalRuns.TryGetValue(run.Id, out var current) || current.Revision != expectedRevision ||
                run.Revision != expectedRevision + 1 || current.OwnerUserId != run.OwnerUserId ||
                expectedLeaseId is not null && (current.IsTerminal || current.LeaseId != expectedLeaseId))
                return Task.FromResult(false);
            _naturalRuns[run.Id] = run with
            {
                LeaseId = run.State != NaturalDiagramRunState.Generating ? null : current.LeaseId,
                LeaseUntil = run.State != NaturalDiagramRunState.Generating ? null : current.LeaseUntil
            };
            return Task.FromResult(true);
        }
    }

    public Task<NaturalDiagramRun?> TryLeaseNaturalDiagramRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        lock (_naturalRunGate)
        {
            var now = DateTimeOffset.UtcNow;
            var run = _naturalRuns.Values.Where(candidate => candidate.State == NaturalDiagramRunState.Queued ||
                    candidate.State == NaturalDiagramRunState.Generating && (candidate.LeaseUntil is null || candidate.LeaseUntil < now))
                .OrderBy(candidate => candidate.CreatedAt).FirstOrDefault();
            if (run is null) return Task.FromResult<NaturalDiagramRun?>(null);
            var leased = run with { State = NaturalDiagramRunState.Generating, Progress = Math.Max(5, run.Progress),
                StageMessage = "요구사항과 시나리오 준비", Revision = run.Revision + 1,
                UpdatedAt = now, LeaseId = Guid.NewGuid(), LeaseUntil = now.Add(leaseDuration) };
            _naturalRuns[run.Id] = leased;
            return Task.FromResult<NaturalDiagramRun?>(leased);
        }
    }

    public Task<bool> RenewNaturalDiagramRunLeaseAsync(Guid id, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        lock (_naturalRunGate)
        {
            if (!_naturalRuns.TryGetValue(id, out var run) || run.LeaseId != leaseId || run.State != NaturalDiagramRunState.Generating)
                return Task.FromResult(false);
            _naturalRuns[id] = run with { LeaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration) };
            return Task.FromResult(true);
        }
    }

    internal NaturalDiagramRun[] SnapshotNaturalRuns()
    {
        lock (_naturalRunGate) return _naturalRuns.Values.ToArray();
    }

    internal void RestoreNaturalRun(NaturalDiagramRun run)
    {
        lock (_naturalRunGate) _naturalRuns[run.Id] = run;
    }

    internal void RestoreNaturalRuns(IReadOnlyList<NaturalDiagramRun> runs)
    {
        lock (_naturalRunGate)
        {
            _naturalRuns.Clear();
            foreach (var run in runs) _naturalRuns.Add(run.Id, run);
        }
    }
}
