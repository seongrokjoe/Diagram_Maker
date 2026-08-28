using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public interface IAppStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RepositoryDefinition>> ListRepositoriesAsync(CancellationToken cancellationToken);
    Task<RepositoryDefinition?> GetRepositoryAsync(Guid id, CancellationToken cancellationToken);
    Task SaveRepositoryAsync(RepositoryDefinition repository, CancellationToken cancellationToken);
    Task SaveAnalysisAsync(AnalysisJob job, CancellationToken cancellationToken);
    Task<AnalysisJob?> GetAnalysisAsync(Guid id, CancellationToken cancellationToken);
    Task<AnalysisJob?> TryLeaseAnalysisAsync(TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task SaveNaturalDiagramAsync(NaturalDiagramRecord record, CancellationToken cancellationToken);
    Task<NaturalDiagramRecord?> GetNaturalDiagramAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramsAsync(string ownerUserId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramRevisionsAsync(Guid rootDiagramId, string ownerUserId, CancellationToken cancellationToken);
    Task SaveAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
