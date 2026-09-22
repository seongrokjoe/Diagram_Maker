using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
using DiagramMaker.Storage;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiagramMaker.Tests;

public sealed class SharedSemanticTests(ITestOutputHelper output)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    [Fact]
    public void ComparisonsAndGenericsSurviveProjectionAndOnlyAffectedPagesChange()
    {
        var prepared = new SharedSemanticProjection(true, [new("f1", "symbol", "First", [], [], null), new("f2", "symbol", "Second", [], [], null)]);
        foreach (var id in new[] { "1", "2" })
        {
            var diagram = new DiagramIr("code-relation", "근거 " + id,
                [new("n" + id, "함수", "method", null, "unchanged", Confidence.Exact, [], SourceFactIds: ["f" + id])], [], [], []);
            prepared.Add(new("page" + id, diagram, new("view", "code-relation", "balanced")));
        }
        var annotations = prepared.Items.Keys.Select(id => new SharedSemanticAnnotation(id, "List<T> 값과 x < 0 조건 검사", "원본 조건을 검사합니다")).ToArray();
        var response = new SharedSemanticResponse("조건 검사", "code-relation", annotations);
        var first = prepared.Apply(response);
        Assert.All(first.Pages.Values, page => Assert.Equal("Semantic", page.Status));
        var same = prepared.Apply(response);
        Assert.Empty(same.ChangedPageKeys!);
        Assert.Same(first.Pages["page2"], same.Pages["page2"]);
        var key = prepared.Diagrams[0].NodeItems["n1"];
        var changed = prepared.Apply(response with { Items = annotations.Select(item => item.Id == key ? item with { Description = "해당 함수의 조건을 검사합니다" } : item).ToArray() });
        Assert.Equal(new[] { "page1" }, changed.ChangedPageKeys);
        Assert.Same(first.Pages["page2"], changed.Pages["page2"]);
        Assert.NotSame(first.Pages["page1"], changed.Pages["page1"]);
    }

    [Fact]
    public void ApprovedAnnotationsRemainVisibleWhenAnotherItemFailsReview()
    {
        var facts = new[] { new SourceFact("f1", "symbol", "First", [], ["e1"], null),
            new SourceFact("f2", "symbol", "Second", [], ["e2"], null) };
        var projection = new SharedSemanticProjection(true, facts);
        var diagram = new DiagramIr("flowchart", "부분 의미",
            [new("n1", "First()", "method", null, "unchanged", Confidence.Exact, ["e1"], SourceFactIds: ["f1"]),
             new("n2", "Second()", "method", null, "unchanged", Confidence.Exact, ["e2"], SourceFactIds: ["f2"])], [], [], []);
        projection.Add(new("view/overview", diagram, new("view", "flowchart", "balanced")));
        var approvedKey = projection.Diagrams[0].NodeItems["n1"];
        var failedKey = projection.Diagrams[0].NodeItems["n2"];
        var response = new SharedSemanticResponse("부분 검토", "flowchart",
            [new(approvedKey, "검토된 첫 동작", "첫 함수의 원본 동작을 검토했습니다.")],
            [new([failedKey], "semantic-review", "LLM_SEMANTIC_REVIEW", Fields: ["items.description"],
                IssueCodes: ["reversed_condition"], CorrectionInstructions: ["조건 방향을 원본과 맞춥니다."])]);

        var result = projection.Apply(response).Pages["view/overview"];

        Assert.Equal("Incomplete", result.Status);
        Assert.Equal("검토된 첫 동작", result.Diagram.Nodes.Single(node => node.Id == "n1").Label);
        Assert.Equal("Second()", result.Diagram.Nodes.Single(node => node.Id == "n2").Label);
        Assert.Equal(new SemanticCoverage(2, 1, 1, 1), result.Explanation!.Coverage);
        var failure = Assert.Single(result.Explanation.Failures!);
        Assert.Equal(new[] { failedKey }, failure.ItemIds);
        Assert.Equal(new[] { "f2" }, failure.FactIds);
        Assert.Equal(new[] { "items.description" }, failure.Fields);
        Assert.Equal(new[] { "reversed_condition" }, failure.IssueCodes);
    }
    private static InternalLlmClient Client(CodeBlockPipelineTests.CodeTransport transport) => new(
        Options.Create(new LlmOptions { Enabled = true }), new(), new(), transport, new(transport));

    [Fact]
    public async Task InterruptingPageAssemblyTwicePreservesSavedPagesAndReusesAllMeaning()
    {
        await using var store = new InMemoryAppStore();
        var proxy = DispatchProxy.Create<IAppStore, ObservedStore>();
        var observed = (ObservedStore)proxy;
        observed.Inner = store;
        var options = SemanticExecutionTests.OptionsForTest();
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var input = new CodeBlockWorkspaceInput("페이지 중단", [new("code", "csharp", "코드",
            "class Work { int A(){return 1;} int B(){return 2;} int C(){return 3;} }")],
            [new("g", "그룹", ["code"], [new("flow", "flowchart", "balanced")])]);
        var workspace = await service.CreateAsync(input, "owner", Ct);
        var current = await service.StartAsync(workspace.Id, new(1), "owner", Ct);
        var handler = new SemanticExecutionTests.PipelineHandler();
        using var transport = new VllmClient(options, handler: handler);
        var requests = 0;
        string[] savedPages = [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var interruption = new CancellationTokenSource();
            observed.AfterSave = run => {
                var pages = run.Results?.SelectMany(g => g.Views).SelectMany(v => v.Pages).ToArray() ?? [];
                if (attempt < 2 && run.StageMessage == "검증된 페이지를 저장하며 다음 단위를 생성합니다." && pages.Length >= 2)
                    interruption.Cancel();
            };
            await SemanticExecutionTests.Processor(proxy, options, transport).ProcessAsync(
                (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, interruption.Token);
            current = (await store.GetCodeBlockRunAsync(current.Id, Ct))!;
            Assert.All(savedPages, id => Assert.Contains(current.Results!.SelectMany(g => g.Views).SelectMany(v => v.Pages), p => p.Id == id));
            if (attempt == 0) requests = handler.Completed.Count;
            else Assert.Equal(requests, handler.Completed.Count);
            savedPages = current.Results!.SelectMany(g => g.Views).SelectMany(v => v.Pages).Select(p => p.Id).ToArray();
            if (attempt < 2)
            {
                current = await service.CancelAsync(current.Id, "owner", Ct);
                current = await service.ResumeAsync(current.Id, new(current.Revision), "owner", Ct);
            }
            else Assert.Equal(CodeBlockRunState.Completed, current.State);
        }
        Assert.Equal(4, savedPages.Length);
    }

    public class ObservedStore : DispatchProxy
    {
        public IAppStore Inner { get; set; } = null!;
        public Action<CodeBlockRun>? AfterSave { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            return method.Name == nameof(IAppStore.SaveCodeBlockRunAsync) ? Observe((Task<bool>)result!, (CodeBlockRun)args![0]!) : result;
        }
        private async Task<bool> Observe(Task<bool> pending, CodeBlockRun run)
        {
            var result = await pending;
            if (result) AfterSave?.Invoke(run);
            return result;
        }
    }

    [Fact]
    public async Task GitResumesTwiceWithImmutableRevisionsAndCumulativeRequests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "shared-git-resume-" + Guid.NewGuid().ToString("N"), "store.json");
        var options = SemanticExecutionTests.OptionsForTest(); options.SemanticJobBudgetSeconds = 5; options.DiagramOutputTokens = 800;
        string Source(bool after) => "class Work {" + string.Join("\n", Enumerable.Range(0, 4).Select(i =>
            $"int Task{i}(int n) {{ if(n<0) return {(after ? -1 : 0)}; return n+{i}; }}")) + "}";
        var comparison = new GitComparison(new string('a', 40), new string('b', 40),
            [new ChangedFile("Work.cs", null, ChangeKind.Modified, "before", "after", [], Source(false), Source(true))]);
        var repositoryId = Guid.NewGuid(); var jobId = Guid.NewGuid();
        var completedRequests = new HashSet<string>();
        var requestCount = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var store = new LocalFileAppStore(path); await store.InitializeAsync(Ct);
            if (attempt == 0)
            {
                var now = DateTimeOffset.UtcNow;
                await store.SaveRepositoryAsync(new(repositoryId, "Synthetic", Path.GetTempPath(), "main", ["Reviewer"], now), Ct);
                var graph = new SourceGraphAnalyzer().Analyze(repositoryId, comparison);
                var group = new AnalysisGroupSelection("g", "변경", graph.Changes.Select(c => c.Id).ToArray(), "flowchart", "balanced",
                    Views: [new("flow", "flowchart", "balanced"), new("class", "class", "balanced")]);
                await store.SaveAnalysisAsync(new(jobId, new(repositoryId, comparison.BaseSha, comparison.TargetSha, Groups: [group]),
                    AnalysisState.Queued, comparison.BaseSha, comparison.TargetSha, 0, "Queued", null, null, null, now, now, null,
                    GenerationVersion: SharedSemanticProjection.Version), Ct);
            }
            else
            {
                var saved = (await store.GetAnalysisAsync(jobId, Ct))!;
                Assert.Equal(requestCount, saved.Execution!.Requests);
                Assert.True(await store.UpdateAnalysisAsync(saved with { State = AnalysisState.Queued, Revision = saved.Revision + 1,
                    LeaseUntil = null, StopReason = null }, saved.Revision, Ct));
                Assert.False(await store.UpdateAnalysisAsync(saved with { State = AnalysisState.Queued }, saved.Revision, Ct));
            }
            var handler = new SemanticExecutionTests.PipelineHandler { StopAfter = attempt < 2 ? 1 : null };
            using var transport = new VllmClient(options, handler: handler);
            var llm = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
            var processor = new AnalysisJobProcessor(store, new FixedGit(comparison), new(), llm, new(new()), new(), new(),
                NullLogger<AnalysisJobProcessor>.Instance, Options.Create(options));
            await processor.ProcessAsync((await store.TryLeaseAnalysisAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
            var current = (await store.GetAnalysisAsync(jobId, Ct))!;
            foreach (var request in handler.Completed) Assert.True(completedRequests.Add(request), "Completed Git request was repeated.");
            requestCount += handler.Completed.Count + (attempt < 2 ? 1 : 0);
            Assert.Equal(requestCount, current.Execution!.TransportRequests);
            Assert.Equal(attempt + 1, current.Execution.AttemptNumber);
            Assert.Equal(comparison.BaseSha, current.BaseSha); Assert.Equal(comparison.TargetSha, current.TargetSha);
            if (attempt < 2) Assert.Equal("budget", current.StopReason);
            else
            {
                Assert.Null(current.StopReason);
                Assert.True(current.Execution.ReusedUnits >= 2);
                Assert.All(current.Result!.DiagramGroups!.SelectMany(g => g.Views!).SelectMany(v => v.Document!.Pages),
                    p => Assert.Equal("Semantic", p.Diagram.Explanation!.Status));
            }
        }
    }

    private sealed class FixedGit(GitComparison comparison) : IGitWorkerClient
    {
        public Task<GitComparison> CompareAsync(RepositoryDefinition repository, AnalyzeRequest request, CancellationToken ct)
        {
            Assert.Equal(comparison.BaseSha, request.BaseRevision); Assert.Equal(comparison.TargetSha, request.TargetRevision);
            return Task.FromResult(comparison);
        }
        public Task<GitRepositoryInspection> InspectAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitCommitSummary>> ListCommitsAsync(RepositoryDefinition r, string? q, int s, int l, CancellationToken ct) => throw new NotSupportedException();
        public Task<GitCommitSummary> GetCommitAsync(RepositoryDefinition r, string revision, CancellationToken ct) => throw new NotSupportedException();
        public Task<PreparedRepositoryAnalysis> PrepareAsync(RepositoryDefinition r, string b, string t, CancellationToken ct) => throw new NotSupportedException();
        public Task<EvidenceSnippet> ReadEvidenceAsync(RepositoryDefinition r, string revision, string file, int start, int end, CancellationToken ct) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("invalid")]
    [InlineData("reject")]
    public async Task SplitAndPermanentFailuresRemainBoundedAcrossResumes(string mode)
    {
        var input = new CodeBlockWorkspaceInput("실패 회귀", [new("code", "csharp", "코드",
            "class Work {" + string.Join("\n", Enumerable.Range(0, 20).Select(i => $"int Task{i}(int n) {{ if(n<0) return 0; return n+{i}; }}")) + "}")]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var transport = new FaultTransport(mode);
        var options = new LlmOptions { Enabled = true, DiagramOutputTokens = mode == "truncate" ? 8000 : 800 };
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        IReadOnlyList<SemanticCheckpoint>? saved = null;
        var originalRequests = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var execution = new SemanticExecution(options, saved, Ct);
            var result = await client.PlanCodeBlockGroupAsync(input, graph, new("g", "그룹", ["code"]), [new("v", "flowchart", "balanced")], Ct);
            saved = execution.Checkpoints;
            if (attempt == 0) originalRequests = transport.Requests;
            else Assert.Equal(originalRequests, transport.Requests);
            if (mode == "truncate")
            {
                Assert.All(result!.Pages.Values, p => Assert.Equal("Semantic", p.Status));
                Assert.Contains(saved, c => c.WasSplit);
            }
            else
            {
                Assert.All(result!.Pages.Values, p => Assert.NotEqual("Semantic", p.Status));
                // Every source unit is attempted even after other content failures.
                // Identical invalid contracts and unchanged meanings stop early.
                Assert.Equal(mode == "invalid" ? 30 * 2 : 30 * 3, transport.Requests);
            }
        }
    }

    private sealed class FaultTransport(string mode) : ILlmCompletionTransport
    {
        private readonly CodeBlockPipelineTests.CodeTransport fallback = new() { WrongFact = mode == "invalid", RejectReview = mode == "reject" };
        public bool IsEnabled => true;
        public int Requests { get; private set; }
        public async Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            Requests++;
            var result = await fallback.CompleteAsync(request, ct);
            using var document = JsonDocument.Parse(request.UserPrompt);
            return mode == "truncate" && document.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 4
                ? result with { FinishReason = "length" } : result;
        }
    }

    [Fact]
    public async Task AllFiveFormatsRetainControlCallStateAndUserEvidence()
    {
        var blocks = new[] { new CodeBlockInput("code", "csharp", "동작",
            "class Machine { int state; int Run(int n) { int x=1; int y=2; if(n<0) return -1; " +
            "while(n>0) { n=Save(n); if(n==2) break; } if(state==0) state=1; Save(n); return n; } int Save(int n){return n-1;} }"),
            new CodeBlockInput("other", "csharp", "별도", "int Other(){return 1;}") };
        var input = new CodeBlockWorkspaceInput("근거", blocks);
        var analyzer = new CodeBlockAnalyzer(new(), Options.Create(new GitWorkerOptions()), Options.Create(new CodeBlockOptions()), null!);
        var graph = await analyzer.AnalyzeAsync(Guid.NewGuid(), blocks, Ct);
        var caller = graph.Symbols.First(s => s.Name.EndsWith("Run"));
        var other = graph.Symbols.First(s => s.BlockId == "other");
        graph = graph with { Relations = graph.Relations.Append(new CodeBlockRelation("manual", "code", "other", "dataflow", "user",
            "사용자 제공 경로", caller.Id, other.Id)).ToArray() };
        var group = new CodeBlockGroupSelection("g", "그룹", ["code", "other"]);
        var projection = new CodeBlockProjectionService(new());
        var selections = projection.Availability(graph, group).Where(a => a.Available).Select(a => new DiagramViewSelection(a.Type, a.Type, "balanced")).ToArray();
        Assert.Equal(5, selections.Length);
        var result = await Client(new()).PlanCodeBlockGroupAsync(input, graph, group, selections, Ct);
        foreach (var selection in selections)
        foreach (var original in projection.Build(graph, graph.Relations, group, selection))
        {
            var actual = result!.Pages[selection.Id + "/" + original.Id];
            Assert.Equal("Semantic", actual.Status);
            var mapped = original.Diagram.Nodes.ToDictionary(n => n.Id, n => actual.Diagram.Nodes.First(a =>
                n.Id == a.Id || (n.SourceFactIds ?? []).Count > 0 && n.SourceFactIds!.All(f => (a.SourceFactIds ?? []).Contains(f))).Id);
            foreach (var edge in original.Diagram.Edges)
                if (mapped[edge.SourceId] != mapped[edge.TargetId] || edge.SourceId == edge.TargetId)
                    Assert.Contains(actual.Diagram.Edges, e => e.Id == edge.Id && e.SourceId == mapped[edge.SourceId] && e.TargetId == mapped[edge.TargetId] &&
                        e.Type == edge.Type && e.RelationOrigin == edge.RelationOrigin && e.EvidenceIds.SequenceEqual(edge.EvidenceIds));
            Assert.All(original.Diagram.Nodes, n => Assert.All(n.EvidenceIds, id => Assert.Contains(actual.Diagram.Nodes, a => a.EvidenceIds.Contains(id))));
            Assert.All(actual.Diagram.Nodes.Where(n => n.DetailPageId is not null), n => Assert.Contains(selection.Id + "/" + n.DetailPageId, result.Pages.Keys));
            if (selection.DiagramType == "sequence")
                Assert.Equal(JsonSerializer.Serialize(original.Diagram.SequenceBlocks?.Select(Shape)), JsonSerializer.Serialize(actual.Diagram.SequenceBlocks?.Select(Shape)));
            if (selection.DiagramType == "state") Assert.Equal(actual.Diagram.Nodes.Count, actual.Diagram.Nodes.Select(n => n.Label).Distinct().Count());
        }
        static object Shape(SequenceBlock block) => new { block.Id, block.Kind, block.EdgeId, children = block.Children.Select(Shape) };
    }

    [Fact]
    public async Task TwoLocalFileRestartsResumeBetweenGenerationAndReviewWithoutRepeatingCompletedTransmissions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "shared-resume-" + Guid.NewGuid().ToString("N"), "store.json");
        var options = SemanticExecutionTests.OptionsForTest();
        options.SemanticJobBudgetSeconds = 5; options.DiagramOutputTokens = 800;
        Guid runId = default;
        var completed = new HashSet<string>();
        var requests = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var store = new LocalFileAppStore(path);
            await store.InitializeAsync(Ct);
            var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
            if (attempt == 0)
            {
                var input = new CodeBlockWorkspaceInput("재개", [new("code", "csharp", "함수", "class Work {" +
                    string.Join("\n", Enumerable.Range(0, 12).Select(i => $"int Work{i}(int n) {{ if(n<0) return -1; return n+{i}; }}")) + "}")],
                    [new("g", "그룹", ["code"], [new("flow", "flowchart", "balanced"), new("relation", "code-relation", "balanced")])]);
                var workspace = await service.CreateAsync(input, "owner", Ct);
                runId = (await service.StartAsync(workspace.Id, new(1), "owner", Ct)).Id;
            }
            else
            {
                var prior = (await store.GetCodeBlockRunAsync(runId, Ct))!;
                Assert.Equal(requests, prior.Execution!.Requests);
                Assert.Equal(completed.Count, prior.Execution.CompletedUnits);
                await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResumeAsync(runId, new(prior.Revision), "other", Ct));
                var next = await service.ResumeAsync(runId, new(prior.Revision), "owner", Ct);
                Assert.Equal(prior.Execution, next.Execution);
                await Assert.ThrowsAsync<CodeBlockConflictException>(() => service.ResumeAsync(runId, new(prior.Revision), "owner", Ct));
                runId = next.Id;
            }
            var handler = new SemanticExecutionTests.PipelineHandler { StopAfter = attempt < 2 ? 1 : null };
            using var transport = new VllmClient(options, handler: handler);
            await SemanticExecutionTests.Processor(store, options, transport).ProcessAsync(
                (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
            var current = (await store.GetCodeBlockRunAsync(runId, Ct))!;
            foreach (var request in handler.Completed) Assert.True(completed.Add(request), "Completed HTTP request was repeated.");
            requests += handler.Completed.Count + (attempt < 2 ? 1 : 0);
            Assert.Equal(requests, current.Execution!.Requests);
            Assert.Equal(requests, current.Execution.TransportRequests);
            Assert.Equal(attempt + 1, current.Execution.AttemptNumber);
            Assert.Equal(completed.Count, current.Execution.CompletedUnits);
            if (attempt < 2)
            {
                Assert.Equal(CodeBlockRunState.Partial, current.State);
                Assert.Equal("budget", current.StopReason);
            }
            else
            {
                Assert.Equal(CodeBlockRunState.Completed, current.State);
                Assert.True(current.Execution.ReusedUnits >= 2);
                Assert.All(current.Results!.SelectMany(g => g.Views).SelectMany(v => v.Pages), p => Assert.Equal("Semantic", p.Diagram.Explanation!.Status));
            }
        }
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("cpp")]
    public async Task TwelveHundredLinesAndFortyFunctionsShareGenerationAndBoundReviewOutput(string language)
    {
        var source = string.Join("\n", Enumerable.Range(0, 4).Select(c =>
            $"class Example{c} {{\n" + (language == "cpp" ? "public:\n" : "// methods\n") +
            string.Join("\n", Enumerable.Range(0, 10).Select(m => $"  int Task{m}(int input) {{\n" +
                string.Join("\n", Enumerable.Range(1, 27).Select(i => $"    input += {i};")) + "\n    return input;\n  }")) +
            (language == "cpp" ? "\n};" : "\n}")));
        Assert.Equal(1212, source.Split('\n').Length);
        var input = new CodeBlockWorkspaceInput("성능 기준", [new("code", language, "코드", source)]);
        var analyzer = new CodeBlockAnalyzer(new(), Options.Create(new GitWorkerOptions
            { ScriptPath = Path.Combine(Root(), "tools/git-worker/index.mjs") }), Options.Create(new CodeBlockOptions()), new TestEnvironment());
        var graph = await analyzer.AnalyzeAsync(Guid.NewGuid(), input.Blocks, Ct);
        Assert.Equal(40, graph.Symbols.Count(s => s.Steps.Count > 0));
        var group = new CodeBlockGroupSelection("g", "전체", ["code"]);
        var projection = new CodeBlockProjectionService(new());
        var selections = projection.Availability(graph, group).Where(a => a.Available)
            .Select(a => new DiagramViewSelection(a.Type, a.Type, "balanced")).ToArray();
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var result = await Client(transport).PlanCodeBlockGroupAsync(input, graph, group, selections, Ct);
        output.WriteLine($"{language}: {transport.Requests.Count} requests, {result!.Pages.Count} pages, {selections.Length} types");
        Assert.DoesNotContain(transport.Requests, r => r.Purpose is "execution-plan" or "execution-review");
        Assert.InRange(transport.Requests.Count, 1, 20);
        var generations = transport.Requests.Where(r => r.Purpose is not ("review" or "execution-plan" or "execution-review")).ToArray();
        var reviews = transport.Requests.Where(r => r.Purpose == "review").ToArray();
        foreach (var request in generations)
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            output.WriteLine($"Batch: {request.UserPrompt.Length} chars, {json.RootElement.GetProperty("items").GetArrayLength()} annotations");
        }
        Assert.InRange(generations.Length, 1, 10);
        Assert.All(generations, request =>
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            Assert.InRange(json.RootElement.GetProperty("items").GetArrayLength(), 1, 26);
        });
        Assert.InRange(reviews.Length, generations.Length, 24);
        Assert.All(reviews, request =>
        {
            Assert.Equal(2000, request.MaxOutputTokens);
            using var json = JsonDocument.Parse(request.UserPrompt);
            Assert.InRange(json.RootElement.GetProperty("context").GetProperty("items").GetArrayLength(), 1, 26);
        });
        Assert.True(result.Pages.Count > 40);
        Assert.All(result.Pages.Values, page => Assert.Equal("Semantic", page.Status));
        foreach (var selection in selections)
        foreach (var candidate in projection.Build(graph, graph.Relations, group, selection))
        {
            var actual = result.Pages[selection.Id + "/" + candidate.Id];
            Assert.True(candidate.Diagram.Nodes.SelectMany(n => n.SourceFactIds ?? []).ToHashSet()
                .SetEquals(actual.Diagram.Nodes.SelectMany(n => n.SourceFactIds ?? [])));
            Assert.All(actual.Diagram.Nodes, n => Assert.NotEmpty(n.EvidenceIds));
        }
    }

    [Theory]
    [InlineData("static_cast<int>(Read())")]
    [InlineData("(static_cast<int>(Read()))")]
    [InlineData("(int)(Read())")]
    public async Task CppCallAssignmentMergesConversionAndKeepsOriginalEvidence(string expression)
    {
        var analyzer = new CodeBlockAnalyzer(new(), Options.Create(new GitWorkerOptions
            { ScriptPath = Path.Combine(Root(), "tools/git-worker/index.mjs") }), Options.Create(new CodeBlockOptions()), new TestEnvironment());
        var graph = await analyzer.AnalyzeAsync(Guid.NewGuid(),
            [new("code", "cpp", "code", $"int Read(){{return 1;}} int Run(){{int result={expression}; return result;}}")], Ct);
        var diagram = ExecutionSequenceProjection.Build(graph.Symbols.Single(s => s.Name == "Run"), graph, "TB");
        var response = Assert.Single(diagram.Edges, e => e.Type == "response");
        Assert.Equal("result", response.Call!.AssignedTo);
        Assert.Contains("형 변환", response.Label);
        Assert.True(response.SourceFactIds!.Count >= 2);
        Assert.True(response.EvidenceIds.Count >= 2);
        Assert.DoesNotContain(SequenceStructure.AnnotatedBlocks(diagram.SequenceBlocks ?? []), b => b.Kind == "note");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DiagramMaker.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root is required for the C++ worker test.");
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = Root();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Root();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public async Task SharedGitAnnotationsPreserveBothRevisionEvidenceAcrossFormats()
    {
        var comparison = new GitComparison(new string('a', 40), new string('b', 40),
            [new ChangedFile("Work.cs", null, ChangeKind.Modified, "old", "new", [],
                "class Work { int Run(int n) { if(n<0) return 0; return Save(n); } int Save(int n) { return n; } }",
                "class Work { int Run(int n) { if(n<0) return -1; return Save(n+1); } int Save(int n) { return n; } }")]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var bundle = DiagramEvidenceBuilder.Build(graph, comparison, graph.Changes.Select(c => c.Id).ToArray());
        var inputs = new List<SharedDiagramInput>();
        var projection = new DiagramProjectionService();
        foreach (var type in new[] { "flowchart", "sequence", "class", "code-relation" })
        {
            var preset = new DiagramPresetCatalog().Resolve(type, "balanced");
            var selection = new DiagramViewSelection(type, type, preset.Id);
            var built = projection.Build("Work", graph, comparison, [type], preset.CallerDepth, preset.CalleeDepth,
                false, graph.Changes.Select(c => c.Id).ToHashSet(), preset, preserveDetails: true);
            foreach (var artifact in built.Artifacts)
                foreach (var page in DiagramDocumentBuilder.Build(artifact, bundle).Pages)
                    inputs.Add(new(type + "/" + page.Id, page.Diagram.Ir, selection));
        }
        Assert.True(inputs.Count > 1);
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var result = await Client(transport).PlanGitGroupAsync(bundle, inputs, false, Ct);
        output.WriteLine($"Git: {transport.Requests.Count} requests, {result!.Pages.Count} pages");
        Assert.InRange(transport.Requests.Count, 2, 12);
        Assert.All(result.Pages.Values, page => Assert.Equal("Semantic", page.Status));
        foreach (var page in result.Pages.Values)
        foreach (var change in page.Explanation!.Changes)
        {
            var evidence = bundle.Facts.Where(f => change.FactIds.Contains(f.Id) && f.Kind == "source").ToArray();
            Assert.Contains(evidence, f => f.Span!.RevisionSha == comparison.BaseSha);
            Assert.Contains(evidence, f => f.Span!.RevisionSha == comparison.TargetSha);
        }
    }

    [Fact]
    public async Task RejectedSharedMeaningNeverBecomesSemanticAndDoesNotRepeatAfterResume()
    {
        var input = new CodeBlockWorkspaceInput("검토", [new("code", "csharp", "코드", "class Work { int Run(){return 1;} }")]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var transport = new CodeBlockPipelineTests.CodeTransport { RejectReview = true };
        var client = Client(transport);
        IReadOnlyList<SemanticCheckpoint>? saved = null;
        var firstRequests = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var execution = new SemanticExecution(new LlmOptions { Enabled = true }, saved, Ct);
            var result = await client.PlanCodeBlockGroupAsync(input, graph, new("g", "그룹", ["code"]),
                [new("flow", "flowchart", "balanced")], Ct);
            Assert.All(result!.Pages.Values, page => Assert.NotEqual("Semantic", page.Status));
            saved = execution.Checkpoints;
            if (attempt == 0) firstRequests = transport.Requests.Count;
            else Assert.Equal(firstRequests, transport.Requests.Count);
        }
    }
}
