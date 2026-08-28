using System.Collections.Concurrent;
using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed class InMemoryAppStore : IAppStore
{
    private readonly ConcurrentDictionary<Guid, RepositoryDefinition> _repositories = new();
    private readonly ConcurrentDictionary<Guid, AnalysisJob> _analyses = new();
    private readonly ConcurrentDictionary<Guid, NaturalDiagramRecord> _diagrams = new();
    private readonly ConcurrentQueue<AuditEvent> _audit = new();
    private readonly SemaphoreSlim _leaseLock = new(1, 1);

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<RepositoryDefinition>> ListRepositoriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RepositoryDefinition>>(
            _repositories.Values.OrderBy(static repository => repository.Name).ToArray());

    public Task<RepositoryDefinition?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken)
    {
        _repositories.TryGetValue(id, out var repository);
        return Task.FromResult(repository);
    }

    public Task SaveRepositoryAsync(RepositoryDefinition repository, CancellationToken cancellationToken)
    {
        _repositories[repository.Id] = repository;
        return Task.CompletedTask;
    }

    public Task SaveAnalysisAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        _analyses[job.Id] = job with { UpdatedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    public Task<AnalysisJob?> GetAnalysisAsync(Guid id, CancellationToken cancellationToken)
    {
        _analyses.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }

    public async Task<AnalysisJob?> TryLeaseAnalysisAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await _leaseLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var job = _analyses.Values
                .Where(candidate => candidate.State == AnalysisState.Queued ||
                                    (candidate.LeaseUntil < now && candidate.State is not AnalysisState.Completed and not AnalysisState.Partial and not AnalysisState.Failed))
                .OrderBy(static candidate => candidate.CreatedAt)
                .FirstOrDefault();

            if (job is null)
            {
                return null;
            }

            var leased = job with
            {
                State = AnalysisState.Resolving,
                Progress = 5,
                StageMessage = "Resolving immutable revisions",
                LeaseUntil = now.Add(leaseDuration),
                UpdatedAt = now
            };
            _analyses[leased.Id] = leased;
            return leased;
        }
        finally
        {
            _leaseLock.Release();
        }
    }

    public Task SaveNaturalDiagramAsync(NaturalDiagramRecord record, CancellationToken cancellationToken)
    {
        _diagrams[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<NaturalDiagramRecord?> GetNaturalDiagramAsync(Guid id, CancellationToken cancellationToken)
    {
        _diagrams.TryGetValue(id, out var record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramsAsync(string ownerUserId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NaturalDiagramRecord>>(_diagrams.Values
            .Where(record => record.OwnerUserId.Equals(ownerUserId, StringComparison.Ordinal) && record.ParentDiagramId is null)
            .OrderByDescending(record => record.CreatedAt)
            .Take(limit)
            .ToArray());

    public Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramRevisionsAsync(Guid rootDiagramId, string ownerUserId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NaturalDiagramRecord>>(_diagrams.Values
            .Where(record => record.OwnerUserId.Equals(ownerUserId, StringComparison.Ordinal) && (record.RootDiagramId ?? record.Id) == rootDiagramId)
            .OrderBy(record => record.Diagram.Version)
            .ToArray());

    public Task<IReadOnlyList<NaturalDiagramRecord>> ListAllNaturalDiagramsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NaturalDiagramRecord>>(_diagrams.Values.OrderBy(record => record.CreatedAt).ToArray());

    public Task SaveAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        _audit.Enqueue(auditEvent);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _leaseLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
