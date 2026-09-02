using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiagramMaker.Tests;

public sealed class AnalysisJobProcessorTests
{
    [Fact]
    public async Task ProcessAsync_MapsStructuredGitFailureToActionableAnalysisError()
    {
        await using var store = new InMemoryAppStore();
        var repositoryId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.SaveRepositoryAsync(
            new RepositoryDefinition(repositoryId, "LargeRepo", Path.GetTempPath(), "main", ["Reviewer"], now),
            CancellationToken.None);
        var job = new AnalysisJob(
            analysisId,
            new AnalyzeRequest(repositoryId, "base", "target"),
            AnalysisState.Resolving,
            null,
            null,
            5,
            "Resolving immutable revisions",
            null,
            null,
            null,
            now,
            now,
            now.AddMinutes(1));
        await store.SaveAnalysisAsync(job, CancellationToken.None);

        var processor = new AnalysisJobProcessor(
            store,
            new FailingGitWorker(),
            new SourceGraphAnalyzer(),
            new DisabledLlmClient(),
            new MermaidCompiler(new DiagramValidator()),
            new DiagramProjectionService(),
            new DiagramPresetCatalog(),
            NullLogger<AnalysisJobProcessor>.Instance);

        await processor.ProcessAsync(job, CancellationToken.None);

        var failed = await store.GetAnalysisAsync(analysisId, CancellationToken.None);
        Assert.NotNull(failed);
        Assert.Equal(AnalysisState.Failed, failed.State);
        Assert.Equal("GIT_PACK_UNREADABLE", failed.ErrorCode);
        Assert.Contains("Git pack", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(failed.LeaseUntil);
    }

    private sealed class FailingGitWorker : IGitWorkerClient
    {
        public Task<GitRepositoryInspection> InspectAsync(string localPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitComparison> CompareAsync(
            RepositoryDefinition repository,
            AnalyzeRequest request,
            CancellationToken cancellationToken) =>
            throw new GitWorkerException(
                "GIT_PACK_UNREADABLE",
                "Git worker failed (isomorphic): Could not read packfile at an internal path.",
                "isomorphic");

        public Task<IReadOnlyList<GitCommitSummary>> ListCommitsAsync(
            RepositoryDefinition repository, string? query, int skip, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GitCommitSummary> GetCommitAsync(
            RepositoryDefinition repository, string revision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PreparedRepositoryAnalysis> PrepareAsync(
            RepositoryDefinition repository, string baseRevision, string targetRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EvidenceSnippet> ReadEvidenceAsync(
            RepositoryDefinition repository, string revisionSha, string filePath,
            int startLine, int endLine, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DisabledLlmClient : IInternalLlmClient
    {
        public bool IsEnabled => false;

        public Task<DiagramIr?> GenerateNaturalDiagramAsync(
            string prompt,
            string requestedType,
            bool enableThinking,
            DiagramPreset preset,
            DiagramStyleOverrides? style,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ReviewNarrative?> GenerateReviewAsync(
            VersionedGraph graph,
            IReadOnlyList<ChangedFile> files,
            bool enableThinking,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<AnalysisGroupDraft>?> RegroupChangesAsync(
            IReadOnlyList<ChangeCandidate> candidates,
            IReadOnlyList<AnalysisGroupDraft> staticGroups,
            VersionedGraph graph,
            IReadOnlyList<ChangedFile> files,
            bool enableThinking,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LlmConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LlmContractTestResult> TestDiagramContractAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LlmThinkingContractTestResult> TestThinkingContractAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
