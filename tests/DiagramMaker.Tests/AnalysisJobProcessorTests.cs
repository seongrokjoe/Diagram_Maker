using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiagramMaker.Tests;

public sealed class AnalysisJobProcessorTests
{
    [Fact]
    public async Task ProcessAsync_UnexpectedViewFailure_IsolatedAsPartialResult()
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
            new DiagramViewSelection("valid", "flowchart", "flow-vertical-overview"),
            new DiagramViewSelection("broken", "historical-unsupported", "missing-historical-preset")
        };
        var group = new AnalysisGroupSelection("group", "변경 그룹", ["change"], "flowchart", "flow-vertical-overview", Views: views);
        await store.SaveAnalysisPlanAsync(new AnalysisPlan(
            planId, "reviewer", new AnalysisPlanRequest(repositoryId, targetSha, baseSha, false), AnalysisPlanState.Ready,
            baseSha, targetSha, 100, "Ready", comparison, graph, [], [], [group], [], null, null, 1,
            now, now, now.AddDays(1), null, SourceGraphAnalyzer.IndexVersion), CancellationToken.None);
        var job = new AnalysisJob(Guid.NewGuid(), new AnalyzeRequest(repositoryId, baseSha, targetSha,
            IncludeLlmSummary: false, AnalysisPlanId: planId, Groups: [group]), AnalysisState.Queued,
            null, null, 0, "Queued", null, null, null, now, now, null);
        await store.SaveAnalysisAsync(job, CancellationToken.None);

        await CreateProcessor(store).ProcessAsync(job, CancellationToken.None);

        var completed = (await store.GetAnalysisAsync(job.Id, CancellationToken.None))!;
        Assert.Equal(AnalysisState.Partial, completed.State);
        Assert.Single(completed.Result!.Diagrams);
        var failedView = Assert.Single(completed.Result.DiagramGroups!.Single().Views!, view => view.ViewId == "broken");
        Assert.Equal("DIAGRAM_RENDER_FAILED", failedView.ErrorCode);
    }

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
            [], [], [group], [], null, null, 1, now, now, now.AddDays(1), null, SourceGraphAnalyzer.IndexVersion);
        await store.SaveAnalysisPlanAsync(plan, CancellationToken.None);
        var first = new AnalysisJob(
            Guid.NewGuid(), new AnalyzeRequest(repositoryId, baseSha, targetSha, IncludeLlmSummary: false,
                AnalysisPlanId: planId, Groups: [group]), AnalysisState.Queued, null, null, 0, "Queued",
            null, null, null, now, now, null);
        await store.SaveAnalysisAsync(first, CancellationToken.None);
        var processor = CreateProcessor(store);

        await processor.ProcessAsync(first, CancellationToken.None);
        var storedFirst = (await store.GetAnalysisAsync(first.Id, CancellationToken.None))!;
        var firstResult = storedFirst.Result!;
        var firstViews = firstResult.DiagramGroups!.Single().Views!;
        var duplicateFailure = firstViews[0] with
        {
            Diagram = null,
            State = "Failed",
            ErrorCode = "OLD_DUPLICATE",
            ErrorMessage = "historical duplicate"
        };
        await store.SaveAnalysisAsync(storedFirst with
        {
            Result = firstResult with
            {
                DiagramGroups = [firstResult.DiagramGroups!.Single() with { Views = [.. firstViews, duplicateFailure] }]
            }
        }, CancellationToken.None);
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

    [Fact]
    public async Task ProcessAsync_GitView_StoresDeterministicGenerationMetadata()
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
        var graph = CreateComparisonGraph(repositoryId, baseSha, targetSha);
        var comparison = new GitComparison(baseSha, targetSha, []);
        var view = new DiagramViewSelection("sequence-view", "sequence", "sequence-focused",
            FocusOnChanges: true, RefinementInstruction: "저장 호출을 중심으로 설명");
        var group = new AnalysisGroupSelection("group", "비교 그룹", ["change"],
            "sequence", "sequence-focused", Views: [view]);
        await store.SaveAnalysisPlanAsync(new AnalysisPlan(
            planId, "reviewer", new AnalysisPlanRequest(repositoryId, targetSha, baseSha, false), AnalysisPlanState.Ready,
            baseSha, targetSha, 100, "Ready", comparison, graph, [], [], [group], [], null, null, 1,
            now, now, now.AddDays(1), null, SourceGraphAnalyzer.IndexVersion), CancellationToken.None);
        var job = new AnalysisJob(Guid.NewGuid(), new AnalyzeRequest(repositoryId, baseSha, targetSha,
            IncludeLlmSummary: false, AnalysisPlanId: planId, Groups: [group]), AnalysisState.Queued,
            null, null, 0, "Queued", null, null, null, now, now, null);
        await store.SaveAnalysisAsync(job, CancellationToken.None);

        await CreateProcessor(store).ProcessAsync(job, CancellationToken.None);

        var completed = (await store.GetAnalysisAsync(job.Id, CancellationToken.None))!;
        Assert.Equal(AnalysisState.Completed, completed.State);
        Assert.Single(completed.Result!.Diagrams);
        var resultView = Assert.Single(completed.Result.DiagramGroups!.Single().Views!);
        Assert.NotNull(resultView.Diagram);
        Assert.Equal("Disabled", resultView.GenerationMetadata!.LlmStatus);
        Assert.Equal("저장 호출을 중심으로 설명", resultView.GenerationMetadata.RefinementInstruction);
        Assert.Contains("change", resultView.GenerationMetadata.ChangeIds);
    }

    [Fact]
    public async Task ProcessAsync_LegacyPlan_ReindexesBeforeRegeneration()
    {
        await using var store = new InMemoryAppStore();
        var repositoryId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var baseSha = new string('a', 40);
        var targetSha = new string('b', 40);
        const string before = "namespace Sample; class Service { void Run() { } }";
        const string after = "namespace Sample; class Service { void Run() { int value = 1; } }";
        var comparison = new GitComparison(baseSha, targetSha,
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new", [], before, after)]);
        var analyzer = new SourceGraphAnalyzer();
        var refreshedGraph = analyzer.Analyze(repositoryId, comparison);
        var changeId = Assert.Single(refreshedGraph.Changes, change =>
            refreshedGraph.Versions.FirstOrDefault(version => version.Id == change.AfterSymbolVersionId)?.QualifiedName == "Sample.Service.Run").Id;
        var group = new AnalysisGroupSelection("group", "legacy", [changeId], "flowchart", "flow-vertical-overview",
            Views: [new DiagramViewSelection("flow-view", "flowchart", "flow-vertical-overview")]);
        await store.SaveRepositoryAsync(new RepositoryDefinition(repositoryId, "Sample", Path.GetTempPath(), "main", ["Reviewer"], now), CancellationToken.None);
        await store.SaveAnalysisPlanAsync(new AnalysisPlan(
            planId, "reviewer", new AnalysisPlanRequest(repositoryId, targetSha, baseSha, false), AnalysisPlanState.Ready,
            baseSha, targetSha, 100, "Ready", comparison with { Files = [] }, new VersionedGraph([], [], [], [], []),
            [], [], [group], [], null, null, 1, now, now, now.AddDays(1), null, "source-graph-v5:legacy"), CancellationToken.None);
        var job = new AnalysisJob(Guid.NewGuid(), new AnalyzeRequest(repositoryId, baseSha, targetSha, IncludeLlmSummary: false,
            AnalysisPlanId: planId, Groups: [group]), AnalysisState.Queued, null, null, 0, "Queued", null, null, null, now, now, null);
        await store.SaveAnalysisAsync(job, CancellationToken.None);
        var cppIndex = new CppSourceIndex("test", [], [], [], [], 0, 0, 0, false, []);
        var git = new PreparedGitWorker(new PreparedRepositoryAnalysis(comparison, cppIndex));
        var processor = new AnalysisJobProcessor(store, git, analyzer, new DisabledLlmClient(),
            new MermaidCompiler(new DiagramValidator()), new DiagramProjectionService(), new DiagramPresetCatalog(),
            NullLogger<AnalysisJobProcessor>.Instance);

        await processor.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(1, git.PrepareCalls);
        var completed = (await store.GetAnalysisAsync(job.Id, CancellationToken.None))!;
        Assert.Equal(AnalysisState.Completed, completed.State);
        Assert.Single(completed.Result!.Diagrams);
    }

    private static AnalysisJobProcessor CreateProcessor(InMemoryAppStore store) => new(
        store, new FailingGitWorker(), new SourceGraphAnalyzer(), new DisabledLlmClient(),
        new MermaidCompiler(new DiagramValidator()), new DiagramProjectionService(), new DiagramPresetCatalog(),
        NullLogger<AnalysisJobProcessor>.Instance);

    [Fact]
    public async Task Redraw_RehydratesBothSourcesWithSummaryDisabled_AndReusesOnlySharedUnderstanding()
    {
        await using var store = new InMemoryAppStore();
        var repositoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var baseSha = new string('a', 40);
        var targetSha = new string('b', 40);
        var comparison = new GitComparison(baseSha, targetSha, [new ChangedFile("S.cs", null, ChangeKind.Modified, "old", "new", [],
            "class S { int Run() => 1; }", "class S { int Run() => 2; }")]);
        var graph = new SourceGraphAnalyzer().Analyze(repositoryId, comparison);
        var version = graph.Versions.Single(item => item.QualifiedName == "S.Run" && item.RevisionSha == targetSha);
        var changeId = graph.Changes.Single(item => item.AfterSymbolVersionId == version.Id).Id;
        var selection = new DiagramViewSelection("view", "flowchart", "flow-vertical-overview");
        var group = new AnalysisGroupSelection("group", "처리 변경", [changeId], selection.DiagramType, selection.PresetId, Views: [selection]);
        var planId = Guid.NewGuid();
        await store.SaveRepositoryAsync(new RepositoryDefinition(repositoryId, "test", Path.GetTempPath(), "main", ["Reviewer"], now), CancellationToken.None);
        await store.SaveAnalysisPlanAsync(new AnalysisPlan(planId, "reviewer", new AnalysisPlanRequest(repositoryId, targetSha, baseSha, false),
            AnalysisPlanState.Ready, baseSha, targetSha, 100, "Ready", comparison with
            { Files = comparison.Files.Select(file => file with { BeforeContent = null, AfterContent = null }).ToArray() }, graph,
            [], [], [group], [], null, null, 1, now, now, now.AddDays(1), null, SourceGraphAnalyzer.IndexVersion), CancellationToken.None);
        var llm = new RecordingPlanningLlm();
        var git = new PreparedGitWorker(new PreparedRepositoryAnalysis(comparison, new CppSourceIndex("test", [], [], [], [], 0, 0, 0, false, [])));
        var processor = new AnalysisJobProcessor(store, git, new SourceGraphAnalyzer(), llm, new MermaidCompiler(new DiagramValidator()),
            new DiagramProjectionService(), new DiagramPresetCatalog(), NullLogger<AnalysisJobProcessor>.Instance);
        var first = new AnalysisJob(Guid.NewGuid(), new AnalyzeRequest(repositoryId, baseSha, targetSha, IncludeLlmSummary: false,
            AnalysisPlanId: planId, Groups: [group]), AnalysisState.Queued, null, null, 0, "Queued", null, null, null, now, now, null);
        await store.SaveAnalysisAsync(first, CancellationToken.None);
        await processor.ProcessAsync(first, CancellationToken.None);
        var storedFirst = (await store.GetAnalysisAsync(first.Id, CancellationToken.None))!;
        Assert.NotNull(storedFirst.Result);
        var changed = selection with { RefinementInstruction = "반환 동작을 강조", Overrides = new DiagramStyleOverrides(Direction: "LR") };
        var second = first with { Id = Guid.NewGuid(), Request = first.Request with { SourceAnalysisId = first.Id,
            RequestedViewIds = ["view"], Groups = [group with { Views = [changed] }] } };
        await store.SaveAnalysisAsync(second, CancellationToken.None);
        await processor.ProcessAsync(second, CancellationToken.None);
        var result = (await store.GetAnalysisAsync(second.Id, CancellationToken.None))!;
        Assert.NotNull(result.Result);
        Assert.Equal(2, git.CompareCalls);
        Assert.Equal(1, llm.UnderstandingCalls);
        Assert.Equal(0, llm.ReviewCalls);
        Assert.True(llm.Plans.Count >= 2);
        var redraw = llm.Plans.Last();
        Assert.Equal("반환 동작을 강조", redraw.Selection.RefinementInstruction);
        Assert.Equal("LR", redraw.Selection.Overrides!.Direction);
        Assert.NotNull(redraw.Previous);
        Assert.Contains(redraw.Bundle.Facts, fact => fact.Content?.Contains("=> 1") == true && fact.Span?.RevisionSha == baseSha);
        Assert.Contains(redraw.Bundle.Facts, fact => fact.Content?.Contains("=> 2") == true && fact.Span?.RevisionSha == targetSha);
        Assert.Equal(llm.Plans[0].Bundle.Hash, redraw.Bundle.Hash);
        Assert.NotEqual(storedFirst.Result!.Diagrams[0].Id, result.Result!.Diagrams[0].Id);
    }

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
            [], [new SymbolChange("change", SymbolChangeKind.ModifyBody, "vma", "vma", Confidence.Exact, [])],
            [new MethodControlFlow("method-a", [new ControlFlowNode("start", "entry", "시작", 2, 2, []),
                new ControlFlowNode("call", "call", "Save()", 3, 3, []), new ControlFlowNode("end", "exit", "종료", 5, 5, [])],
                [new ControlFlowEdge("start", "call", "control", ""), new ControlFlowEdge("call", "end", "control", "")], sha, "A.cpp")]);
    }

    private static VersionedGraph CreateComparisonGraph(Guid repositoryId, string baseSha, string targetSha)
    {
        var identities = new[]
        {
            new SymbolIdentity("source", repositoryId, "cpp", "method", "function:A::Run()"),
            new SymbolIdentity("target", repositoryId, "cpp", "method", "function:B::Save()")
        };
        var versions = new[]
        {
            new SymbolVersion("source-base", "source", baseSha, "A::RunOld", "void Run()", "A.cpp", 1, 5, "old"),
            new SymbolVersion("source-target", "source", targetSha, "A::RunNew", "void Run()", "A.cpp", 1, 5, "new"),
            new SymbolVersion("target-base", "target", baseSha, "B::Save", "void Save()", "B.cpp", 1, 3, "same"),
            new SymbolVersion("target-target", "target", targetSha, "B::Save", "void Save()", "B.cpp", 1, 3, "same")
        };
        var edges = new[]
        {
            new GraphEdge("base-call", "source", "target", "calls", "Save", Confidence.Exact, [], RevisionSha: baseSha),
            new GraphEdge("target-call", "source", "target", "calls", "Save", Confidence.Exact, [], RevisionSha: targetSha)
        };
        return new VersionedGraph(identities, versions, edges, [],
            [new SymbolChange("change", SymbolChangeKind.ModifyBody, "source-base", "source-target", Confidence.Exact, [])]);
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

    private sealed class PreparedGitWorker(PreparedRepositoryAnalysis prepared) : IGitWorkerClient
    {
        public int PrepareCalls { get; private set; }
        public int CompareCalls { get; private set; }
        public Task<PreparedRepositoryAnalysis> PrepareAsync(RepositoryDefinition repository, string baseRevision, string targetRevision, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            return Task.FromResult(prepared);
        }
        public Task<GitRepositoryInspection> InspectAsync(string localPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitComparison> CompareAsync(RepositoryDefinition repository, AnalyzeRequest request, CancellationToken cancellationToken)
        {
            CompareCalls++;
            Assert.Equal(prepared.Comparison.BaseSha, request.BaseRevision);
            Assert.Equal(prepared.Comparison.TargetSha, request.TargetRevision);
            return Task.FromResult(prepared.Comparison);
        }
        public Task<IReadOnlyList<GitCommitSummary>> ListCommitsAsync(RepositoryDefinition repository, string? query, int skip, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitCommitSummary> GetCommitAsync(RepositoryDefinition repository, string revision, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EvidenceSnippet> ReadEvidenceAsync(RepositoryDefinition repository, string revisionSha, string filePath, int startLine, int endLine, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingPlanningLlm : DisabledLlmClient, IInternalLlmClient
    {
        public override bool IsEnabled => true;
        public int UnderstandingCalls { get; private set; }
        public int ReviewCalls { get; private set; }
        public List<(EvidenceBundle Bundle, DiagramViewSelection Selection, DiagramIr? Previous)> Plans { get; } = [];
        public Task<ChangeUnderstanding?> UnderstandChangesAsync(EvidenceBundle bundle, bool enableThinking, CancellationToken cancellationToken)
        {
            UnderstandingCalls++;
            return Task.FromResult<ChangeUnderstanding?>(new ChangeUnderstanding("공통 이해", bundle.ChangeIds.Select(id =>
                new ChangeExplanation(id, "반환값 변경", bundle.Facts.Where(fact => fact.ChangeIds.Contains(id)).Select(fact => fact.Id).ToArray())).ToArray()));
        }
        public Task<SemanticGeneration?> PlanDiagramAsync(DiagramIr candidate, EvidenceBundle bundle, ChangeUnderstanding? understanding,
            DiagramViewSelection selection, DiagramIr? previous, bool enableThinking, CancellationToken cancellationToken)
        {
            Plans.Add((bundle, selection, previous));
            return Task.FromResult<SemanticGeneration?>(new SemanticGeneration(candidate, "Semantic", [], [], 2));
        }
        Task<ReviewNarrative?> IInternalLlmClient.GenerateReviewAsync(VersionedGraph graph, IReadOnlyList<ChangedFile> files,
            bool enableThinking, CancellationToken cancellationToken)
        {
            ReviewCalls++;
            return Task.FromResult<ReviewNarrative?>(null);
        }
    }

    private class DisabledLlmClient : IInternalLlmClient
    {
        public virtual bool IsEnabled => false;

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

        public Task<DiagramIr?> RefineAnalysisDiagramAsync(
            DiagramIr candidate,
            VersionedGraph graph,
            GitComparison comparison,
            IReadOnlyList<string> changeIds,
            DiagramViewSelection selection,
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
