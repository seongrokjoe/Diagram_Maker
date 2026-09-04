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
    Task<IReadOnlyList<AnalysisJob>> ListAnalysesByPlanAsync(Guid planId, int limit, CancellationToken cancellationToken);
    Task<AnalysisJob?> TryLeaseAnalysisAsync(TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task SaveAnalysisPlanAsync(AnalysisPlan plan, CancellationToken cancellationToken);
    Task<AnalysisPlan?> GetAnalysisPlanAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<AnalysisPlan>> ListAnalysisPlansAsync(string ownerUserId, int limit, CancellationToken cancellationToken);
    Task<AnalysisPlan?> TryLeaseAnalysisPlanAsync(TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task SaveNaturalDiagramAsync(NaturalDiagramRecord record, CancellationToken cancellationToken);
    Task<NaturalDiagramRecord?> GetNaturalDiagramAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramsAsync(string ownerUserId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<NaturalDiagramRecord>> ListNaturalDiagramRevisionsAsync(Guid rootDiagramId, string ownerUserId, CancellationToken cancellationToken);
    Task SaveDiagramRevisionAsync(DiagramRevisionRecord record, CancellationToken cancellationToken);
    Task<DiagramRevisionRecord?> GetDiagramRevisionAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiagramRevisionRecord>> ListDiagramRevisionsAsync(Guid rootArtifactId, string ownerUserId, CancellationToken cancellationToken);
    Task SaveAuditAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
