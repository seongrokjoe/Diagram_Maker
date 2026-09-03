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

    [Fact]
    public async Task ProcessAsync_RegeneratesRequestedGroupViewAndReusesUnchangedView()
    {
        await using var store = new InMemoryAppStore();
        var repositoryId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var baseSha = new string('a', 40);
        var targetSha = new string('b', 40);
        await store.SaveRepositoryAsync(
            new RepositoryDefinition(repositoryId, "Sample", Path.GetTempPath(), "main", ["Reviewer"], now),
            CancellationToken.None);
        var graph = CreateGraph(repositoryId, targetSha);
        var comparison = new GitComparison(baseSha, targetSha, []);
        var views = new[]
        {
            new DiagramViewSelection("flow-view", "flowchart", "flow-vertical-overview"),
            new DiagramViewSelection("class-view", "class", "class-related")
        };
        var group = new AnalysisGroupSelection("group", "변경 그룹", ["change"],
            "flowchart", "flow-vertical-overview", Views: views);
        var plan = new AnalysisPlan(
            planId, "reviewer", new AnalysisPlanRequest(repositoryId, targetSha, baseSha, false),
            AnalysisPlanState.Ready, baseSha, targetSha, 100, "Ready", comparison, graph,
            [], [], [group], [], null, null, 1, now, now, now.AddDays(1), null);
        await store.SaveAnalysisPlanAsync(plan, CancellationToken.None);
        var first = new AnalysisJob(
            Guid.NewGuid(), new AnalyzeRequest(repositoryId, baseSha, targetSha, IncludeLlmSummary: false,
                AnalysisPlanId: planId, Groups: [group]), AnalysisState.Queued, null, null, 0, "Queued",
            null, null, null, now, now, null);
        await store.SaveAnalysisAsync(first, CancellationToken.None);
        var processor = CreateProcessor(store);

        await processor.ProcessAsync(first, CancellationToken.None);
        var firstResult = (await store.GetAnalysisAsync(first.Id, CancellationToken.None))!.Result!;
        var firstViews = firstResult.DiagramGroups!.Single().Views!;
        var second = new AnalysisJob(
            Guid.NewGuid(), first.Request with { SourceAnalysisId = first.Id, RequestedViewIds = ["class-view"] },
            AnalysisState.Queued, null, null, 0, "Queued", null, null, null, now, now, null);
        await store.SaveAnalysisAsync(second, CancellationToken.None);

        await processor.ProcessAsync(second, CancellationToken.None);

        var secondResult = (await store.GetAnalysisAsync(second.Id, CancellationToken.None))!.Result!;
        var secondViews = secondResult.DiagramGroups!.Single().Views!;
        Assert.Equal(firstViews.Single(view => view.ViewId == "flow-view").Diagram!.Id,
            secondViews.Single(view => view.ViewId == "flow-view").Diagram!.Id);
        Assert.True(secondViews.Single(view => view.ViewId == "flow-view").Reused);
        Assert.NotEqual(firstViews.Single(view => view.ViewId == "class-view").Diagram!.Id,
            secondViews.Single(view => view.ViewId == "class-view").Diagram!.Id);
        Assert.False(secondViews.Single(view => view.ViewId == "class-view").Reused);
    }

    private static AnalysisJobProcessor CreateProcessor(InMemoryAppStore store) => new(
        store, new FailingGitWorker(), new SourceGraphAnalyzer(), new DisabledLlmClient(),
        new MermaidCompiler(new DiagramValidator()), new DiagramProjectionService(), new DiagramPresetCatalog(),
        NullLogger<AnalysisJobProcessor>.Instance);

    private static VersionedGraph CreateGraph(Guid repositoryId, string sha)
    {
        var identities = new[]
        {
            new SymbolIdentity("type-a", repositoryId, "cpp", "class", "type:A"),
            new SymbolIdentity("method-a", repositoryId, "cpp", "method", "function:A::Run()"),
            new SymbolIdentity("type-b", repositoryId, "cpp", "class", "type:B"),
            new SymbolIdentity("method-b", repositoryId, "cpp", "method", "function:B::Save()")
        };
        var versions = new[]
        {
            new SymbolVersion("vta", "type-a", sha, "A", "class A", "A.cpp", 1, 10, "ta"),
            new SymbolVersion("vma", "method-a", sha, "A::Run", "void Run()", "A.cpp", 2, 5, "ma"),
            new SymbolVersion("vtb", "type-b", sha, "B", "class B", "B.cpp", 1, 10, "tb"),
            new SymbolVersion("vmb", "method-b", sha, "B::Save", "void Save()", "B.cpp", 2, 5, "mb")
        };
        return new VersionedGraph(
            identities, versions,
            [new GraphEdge("call", "method-a", "method-b", "calls", "Save", Confidence.Exact, [])],
            [], [new SymbolChange("change", SymbolChangeKind.ModifyBody, "vma", "vma", Confidence.Exact, [])]);
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
