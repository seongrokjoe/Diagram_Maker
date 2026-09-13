using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class CodeBlockPipelineTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CodeBlockInput Block(string id, string code) => new(id, "csharp", id, code);
    private static CodeBlockRun Run(CodeBlockWorkspaceInput input) => new(Guid.NewGuid(), Guid.NewGuid(), "owner", 1, 1, input,
        CodeBlockRunState.Indexing, 0, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static CodeBlockGraph Graph(CodeBlockWorkspaceInput input) => new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
    private static CodeBlockGroupingService Grouping() => new(Options.Create(new CodeBlockOptions()));

    [Fact]
    public void UnrelatedBlocksInOneUserGroupHaveNoInventedEdges()
    {
        var input = new CodeBlockWorkspaceInput("independent", [Block("a", "void One(){}"), Block("b", "void Two(){}")], [new("g", "both", ["a", "b"])]);
        var graph = Graph(input);
        var grouped = Grouping().Prepare(Run(input), graph);
        Assert.Single(grouped.Groups); Assert.Empty(grouped.Relations); Assert.Empty(grouped.Questions);
        var map = new CodeBlockProjectionService(new()).Build(graph, grouped.Relations, grouped.Groups[0], new("v", "code-relation", "balanced"));
        Assert.Empty(map[0].Diagram.Edges);
        var flow = new CodeBlockProjectionService(new()).Build(graph, [], grouped.Groups[0], new("f", "flowchart", "balanced"))[0].Diagram;
        var nodes = flow.Nodes.ToDictionary(n => n.Id);
        Assert.All(flow.Edges, e => Assert.Equal(nodes[e.SourceId].Group, nodes[e.TargetId].Group));
    }
    [Fact]
    public void AnswersRemainUserRelationsAndMergeOnlyWhenRequested()
    {
        var input = new CodeBlockWorkspaceInput("ambiguous", [Block("a", "void Run(){Save();}"), Block("b", "void Save(){}")],
            [new("first", "First", ["a"], EnableThinking: true, EnableUserRelations: false), new("second", "Second", ["b"], EnableThinking: false)]);
        var graph = Graph(input);
        var initial = Grouping().Prepare(Run(input), graph);
        var question = Assert.Single(initial.Questions);
        Assert.Equal(2, initial.Groups.Count);
        var run = Run(input) with { Groups = initial.Groups, Questions = initial.Questions, QuestionsResolved = true,
            Answers = [new(question.Id, question.Options[0].Id)] };
        var answered = Grouping().Prepare(run, graph);
        Assert.Equal("user", Assert.Single(answered.Relations).Origin);
        Assert.Null(answered.Relations[0].CallSiteId);
        Assert.Equal(2, answered.Groups.Count);
        var merged = Grouping().Prepare(run with { Answers = [new(question.Id, question.Options[0].Id, true)] }, graph);
        Assert.Single(merged.Groups);
        Assert.True(merged.Groups[0].EnableThinking);
        Assert.False(merged.Groups[0].EnableUserRelations);
        var projection = new CodeBlockProjectionService(new());
        Assert.True(projection.Availability(graph, merged.Groups[0]).Single(a => a.Type == "sequence").Available);
        var sequence = projection.Build(graph, merged.Relations, merged.Groups[0], new("sequence", "sequence", "balanced"))[0].Diagram;
        Assert.DoesNotContain(sequence.Edges, edge => edge.RelationOrigin == "user");
        Assert.Contains(sequence.Nodes, node => node.Label.Contains("구현 미확인"));
        var map = projection.Build(graph, merged.Relations, merged.Groups[0], new("v", "code-relation", "balanced"));
        Assert.StartsWith("사용자 제공:", Assert.Single(map[0].Diagram.Edges).Label);
    }
    [Fact]
    public void QuestionsAreCappedAndUnknownAnswersDoNotRepeat()
    {
        var input = new CodeBlockWorkspaceInput("many", [Block("a", "void Run(){Save();Save();Save();Save();Save();Save();}"), Block("b", "void Save(){}")]);
        var graph = Graph(input); var grouped = Grouping().Prepare(Run(input), graph);
        Assert.Equal(5, grouped.Questions.Count);
        var resolved = Grouping().Prepare(Run(input) with { Questions = grouped.Questions, QuestionsResolved = true,
            Answers = grouped.Questions.Select(q => new CodeBlockAnswer(q.Id, "unknown")).ToArray() }, graph);
        Assert.Empty(resolved.Relations);
        Assert.Contains(resolved.Warnings, w => w.Contains("미확인"));
    }
    [Fact]
    public async Task ProcessorPersistsTerminalPartialWhenLlmIsDisabled()
    {
        await using var store = new InMemoryAppStore();
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var workspace = await service.CreateAsync(new("code", [Block("a", "void Run(){int value=1; if(value>0) return;}")]), "owner", Ct);
        var queued = await service.StartAsync(workspace.Id, new(1), "owner", Ct);
        var leased = (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!;
        var transport = new CodeTransport { Enabled = false };
        var options = new LlmOptions();
        var processor = new CodeBlockRunProcessor(store, new(new(), Options.Create(new GitWorkerOptions()), Options.Create(new CodeBlockOptions()), null!),
            Grouping(), new(new()), Client(options, transport), new(new()), new(), Options.Create(options));
        await processor.ProcessAsync(leased, Ct);
        var result = (await store.GetCodeBlockRunAsync(queued.Id, Ct))!;
        Assert.Equal(CodeBlockRunState.Partial, result.State); Assert.Null(result.LeaseId);
        Assert.NotEmpty(result.Results![0].Views[0].Pages);
        Assert.Empty(transport.Requests);
        Assert.Null(await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct));
    }
    [Fact]
    public async Task CodePlanningUsesSeparatePromptsAndRetainsFactsAndBranches()
    {
        var (input, graph, candidate) = Candidate();
        var transport = new CodeTransport();
        var result = await Client(new() { Enabled = true }, transport).PlanCodeBlockDiagramAsync(candidate, input, graph, null, new("v", "flowchart", "balanced"), Ct);
        Assert.Equal("Semantic", result!.Status); Assert.NotEmpty(result.Explanation!.Behaviors!);
        Assert.Equal(candidate.Edges.Select(e => (e.Id, e.SourceId, e.TargetId)), result.Diagram.Edges.Select(e => (e.Id, e.SourceId, e.TargetId)));
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, r => { Assert.Contains("pasted code behavior", r.SystemPrompt); Assert.DoesNotContain("Compare old and new", r.SystemPrompt); });
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidOrRejectedCodePlansStopAfterOneRepair(bool wrongFact, bool rejectReview)
    {
        var (input, graph, candidate) = Candidate();
        var transport = new CodeTransport { WrongFact = wrongFact, RejectReview = rejectReview };
        var result = await Client(new() { Enabled = true }, transport).PlanCodeBlockDiagramAsync(candidate, input, graph, null, new("v", "flowchart", "balanced"), Ct);
        Assert.Equal("Incomplete", result!.Status); Assert.Null(result.Explanation);
        Assert.Equal(2, result.Attempts); Assert.Same(candidate, result.Diagram);
        Assert.Equal(wrongFact ? 2 : 4, transport.Requests.Count);
    }
    [Fact]
    public async Task ManualRelationshipChangesLoseCodeProvenance()
    {
        var (input, _, candidate) = Candidate();
        await using var store = new InMemoryAppStore();
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var workspace = await service.CreateAsync(input, "owner", Ct);
        var run = await service.StartAsync(workspace.Id, new(1), "owner", Ct);
        var source = new DiagramArtifact(Guid.NewGuid(), candidate.Type, 1, candidate, new MermaidCompiler(new()).Compile(candidate), DateTimeOffset.UtcNow);
        var document = new DiagramEditDocument(candidate.Title, candidate.Direction,
            candidate.Nodes.Select(n => new EditableDiagramNode(n.Id, n.Label)).ToArray(),
            candidate.Edges.Select((e, i) => new EditableDiagramEdge(e.Id, e.SourceId, e.TargetId, i == 0 ? "사용자 설명" : e.Label, e.Type)).ToArray());
        var saved = await new DiagramRevisionService(store, new(), new(new())).SaveAsync(source, new(source.Id, null, 1, document), "owner", "code-block", run.Id, "g", "v", Ct);
        Assert.Equal("user", saved.Diagram.Ir.Edges[0].RelationOrigin);
        Assert.Empty(saved.Diagram.Ir.Edges[0].EvidenceIds);
        Assert.Empty(saved.Diagram.Ir.Edges[0].SourceFactIds!);
        Assert.StartsWith("사용자 제공:", saved.Diagram.Ir.Edges[0].Label);
    }
    [Fact]
    public async Task SelectedRegenerationRetainsOtherViewAndSemanticFailureKeepsPreviousSuccess()
    {
        await using var store = new InMemoryAppStore();
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var input = new CodeBlockWorkspaceInput("source", [Block("a", "void Run(){int value=1; if(value>0) return;}")],
            [new("g", "group", ["a"], [new("flow", "flowchart", "balanced"), new("map", "code-relation", "balanced")])]);
        var workspace = await service.CreateAsync(input, "owner", Ct);
        var transport = new CodeTransport(); var options = new LlmOptions { Enabled = true };
        var processor = new CodeBlockRunProcessor(store, new(new(), Options.Create(new GitWorkerOptions()), Options.Create(new CodeBlockOptions()), null!),
            Grouping(), new(new()), Client(options, transport), new(new()), new(), Options.Create(options));
        var first = await service.StartAsync(workspace.Id, new(1), "owner", Ct);
        await processor.ProcessAsync((await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
        var original = (await store.GetCodeBlockRunAsync(first.Id, Ct))!;
        Assert.Equal(CodeBlockRunState.Completed, original.State);
        transport.RejectReview = true;
        var next = await service.StartAsync(workspace.Id, new(1, ["flow"]), "owner", Ct);
        await processor.ProcessAsync((await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
        var updated = (await store.GetCodeBlockRunAsync(next.Id, Ct))!;
        Assert.Equal(CodeBlockRunState.Partial, updated.State);
        Assert.All(updated.Results![0].Views, v => Assert.Equal(original.Results![0].Views.Single(o => o.ViewId == v.ViewId).Pages[0].Diagram.Id, v.Pages[0].Diagram.Id));
        Assert.Equal("Completed", updated.Results[0].Views.Single(v => v.ViewId == "map").State);
        Assert.Contains("이전 성공", updated.Results[0].Views.Single(v => v.ViewId == "flow").ErrorMessage!);
    }
    [Fact]
    public void DefaultGroupDoesNotSplitUnrelatedBlocksAndManualRelationsFollowGroupOptions()
    {
        var input = new CodeBlockWorkspaceInput("unrelated", [Block("a", "void One(){}"), Block("b", "void Two(){}")],
            Relations: [new("manual", "a", "b", "uses", "user", "사용자 관계")]);
        var graph = Graph(input);
        var prepared = Grouping().Prepare(Run(input), graph);
        Assert.Single(prepared.Groups);
        Assert.Single(prepared.Relations);
        var disabled = input with { Groups = [prepared.Groups[0] with { EnableUserRelations = false }] };
        Assert.Empty(Grouping().Prepare(Run(disabled), graph).Relations);
        Assert.Single(disabled.Relations!);
        var moved = input with { Groups = [new("a", "First", ["a"], EnableUserRelations: true), new("b", "Second", ["b"], EnableUserRelations: true)] };
        Assert.Empty(Grouping().Prepare(Run(moved), graph).Relations);
        Assert.Single(Grouping().Prepare(Run(input), graph).Relations);
    }

    [Fact]
    public async Task ThinkingReachesEveryGroupLlmStageAndSurvivesSelectedRegenerationAndCacheChanges()
    {
        await using var store = new InMemoryAppStore();
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var input = new CodeBlockWorkspaceInput("two groups", [Block("a", "void First(){int x=1; if(x>0) return;}"), Block("b", "void Second(){int y=2; if(y>0) return;}")],
            [new("ga", "Group A", ["a"], [new("va", "flowchart", "balanced")], EnableThinking: true, EnableUserRelations: false),
             new("gb", "Group B", ["b"], [new("vb", "flowchart", "balanced")], EnableThinking: false, EnableUserRelations: false),
             new("empty", "Empty", [], EnableThinking: true)]);
        var workspace = await service.CreateAsync(input, "owner", Ct);
        var transport = new CodeTransport(); var options = new LlmOptions { Enabled = true };
        var processor = new CodeBlockRunProcessor(store, new(new(), Options.Create(new GitWorkerOptions()), Options.Create(new CodeBlockOptions()), null!),
            Grouping(), new(new()), Client(options, transport), new(new()), new(), Options.Create(options));
        async Task<CodeBlockRun> Generate(IReadOnlyList<string>? views = null)
        {
            var queued = await service.StartAsync(workspace.Id, new(workspace.Revision, views), "owner", Ct);
            await processor.ProcessAsync((await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
            var result = (await store.GetCodeBlockRunAsync(queued.Id, Ct))!;
            Assert.Equal(CodeBlockRunState.Completed, result.State);
            Assert.Equal(2, result.Results!.Count);
            return result;
        }
        var first = await Generate();
        // Each group plans/reviews its function, then generates/reviews shared labels.
        Assert.Equal(8, transport.Requests.Count);
        Assert.All(transport.Requests.Take(4), r => Assert.True(r.EnableThinking));
        Assert.All(transport.Requests.Skip(4), r => Assert.False(r.EnableThinking));
        transport.Requests.Clear();
        var regenerated = await Generate(["va"]);
        Assert.True(regenerated.Groups!.Single(g => g.Id == "ga").EnableThinking);
        Assert.False(regenerated.Groups!.Single(g => g.Id == "gb").EnableThinking);
        Assert.Equal(4, transport.Requests.Count);
        Assert.All(transport.Requests, r => Assert.True(r.EnableThinking));
        Assert.Equal(first.Results![1].Views[0].Pages[0].Diagram.Id, regenerated.Results![1].Views[0].Pages[0].Diagram.Id);
        workspace = await service.SaveAsync(workspace.Id, new(workspace.Revision, input with {
            Groups = input.Groups!.Select(g => g.Id == "ga" ? g with { EnableThinking = false } : g).ToArray() }), "owner", Ct);
        transport.Requests.Clear();
        var changed = await Generate();
        Assert.All(transport.Requests, r => Assert.False(r.EnableThinking));
        Assert.NotEqual(first.Results[0].Views[0].CacheKey, changed.Results![0].Views[0].CacheKey);
        Assert.Equal(first.Results[1].Views[0].Pages[0].Diagram.Id, changed.Results[1].Views[0].Pages[0].Diagram.Id);
    }

    private static (CodeBlockWorkspaceInput, CodeBlockGraph, DiagramIr) Candidate()
    {
        var input = new CodeBlockWorkspaceInput("source", [Block("a", "void Run(){int value=1; if(value>0) return;}")]);
        var graph = Graph(input);
        return (input, graph, new CodeBlockProjectionService(new()).Build(graph, [], new("g", "source", ["a"]), new("v", "flowchart", "balanced"))[0].Diagram);
    }
    private static InternalLlmClient Client(LlmOptions options, ILlmCompletionTransport transport) => new(Options.Create(options), new(), new(), transport, new(transport));
    internal sealed class CodeTransport : ILlmCompletionTransport
    {
        public bool Enabled { get; init; } = true;
        public bool IsEnabled => Enabled;
        public bool WrongFact { get; init; }
        public bool RejectReview { get; set; }
        public List<VllmCompletionRequest> Requests { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            using var json = JsonDocument.Parse(request.UserPrompt);
            var properties = request.StructuredSchema!.Value.GetProperty("properties");
            var response = properties.TryGetProperty("steps", out _)
                ? JsonSerializer.Serialize(WrongFact ? ExecutionPlan(json.RootElement) with { Steps = [] } : ExecutionPlan(json.RootElement), Json)
                : properties.TryGetProperty("items", out var items) && items.GetProperty("items").GetProperty("properties").TryGetProperty("issues", out _)
                ? JsonSerializer.Serialize(new SharedSemanticReview(json.RootElement.GetProperty("context").GetProperty("items").EnumerateArray()
                    .Select(i => new SharedItemReview(i.GetProperty("id").GetString()!, RejectReview ? ["unsupported_role"] : [])).ToArray()), Json)
                : properties.TryGetProperty("items", out _)
                ? JsonSerializer.Serialize(SharedPlan(json.RootElement, WrongFact), Json)
                : properties.TryGetProperty("recommendedType", out _)
                ? json.RootElement.TryGetProperty("symbol", out var symbol)
                    ? JsonSerializer.Serialize(new CodeBlockUnderstanding("값을 준비하고 조건에 따라 반환", "", [new("behavior", "값을 준비하고 조건에 따라 반환", [symbol.GetProperty("id").GetString()!], [], [])]), Json)
                    : JsonSerializer.Serialize(new CodeBlockUnderstanding("입력 코드 동작", "flowchart", []), Json)
                : properties.TryGetProperty("accepted", out _)
                ? JsonSerializer.Serialize(new DiagramPlanReview(!RejectReview, RejectReview ? ["unsupported behavior"] : []), Json)
                : JsonSerializer.Serialize(Plan(json.RootElement.GetProperty("context").GetProperty("candidate").Deserialize<DiagramIr>(Json)!, WrongFact), Json);
            return Task.FromResult(new VllmCompletionResult(response, "stop", 1, true, false, 0, 1000, 10, 10, 20));
        }
        internal static SharedSemanticResponse SharedPlan(JsonElement root, bool wrong)
        {
            if (root.TryGetProperty("context", out var context)) root = context;
            return new("입력 코드의 동작과 확인된 관계", root.GetProperty("available")[0].GetString()!,
                root.GetProperty("items").EnumerateArray().Select(item => new SharedSemanticAnnotation(
                    wrong ? "invented" : item.GetProperty("id").GetString()!,
                    item.GetProperty("kind").GetString() is "condition" or "loop" or "control" ? "값이 양수인가요?" : "조건에 따라 값을 처리합니다",
                    "원본 코드의 값과 조건을 확인하고 처리합니다")).ToArray());
        }
        internal static ExecutionMeaningPlan ExecutionPlan(JsonElement root)
        {
            var steps = root.GetProperty("steps").Deserialize<ExecutionMeaningStep[]>(Json)!;
            var units = new List<ExecutionMeaningUnit>();
            var owned = new HashSet<string>();
            var referenced = steps.SelectMany(s => s.ChildIds.Concat(s.AlternativeIds).Concat(s.EvaluationIds)).ToHashSet();
            var regions = steps.SelectMany(s => new[] { s.ChildIds, s.AlternativeIds, s.EvaluationIds })
                .Prepend(steps.Where(s => !referenced.Contains(s.Id)).Select(s => s.Id).ToArray());
            foreach (var region in regions)
            {
                var chain = new List<string>();
                void Flush() { if (chain.Count > 0) { units.Add(new("지역 값을 준비합니다", "원문에 명시된 순서로 지역 값을 설정합니다", chain.ToArray())); chain.Clear(); } }
                foreach (var id in region)
                {
                    if (!owned.Add(id)) continue;
                    var step = steps.Single(s => s.Id == id);
                    if (step.Kind is "declare" or "assign") chain.Add(id);
                    else { Flush(); units.Add(new("조건에 따라 처리합니다", "원본의 실행 사실을 보존하며 외부 구현은 미확인입니다", [id])); }
                }
                Flush();
            }
            return new("입력 조건을 확인하고 명시된 처리 결과를 반환합니다", "implementation", steps, units);
        }
        public static CodeBlockSemanticPlan Plan(DiagramIr candidate, bool wrongFact) => new("코드 동작 설명",
            candidate.Nodes.Select((n, i) => new CodeBlockSemanticElement("element" + i, n.Kind is "entry" or "exit" ? n.Label : "조건에 따라 값을 처리합니다", [n.Id], n.SourceFactIds!,
                n.Kind is "condition" or "loop" ? "값이 양수인가요?" : "", "값을 확인하고 처리를 마칩니다")).ToArray(),
            candidate.Edges.Select(e => new SemanticMessage(e.Id, "코드 흐름")).ToArray(),
            candidate.Nodes.Select(n => new CodeBlockBehavior(n.Id, "실제 코드의 처리 동작", wrongFact ? ["invented"] : n.SourceFactIds!, [n.Id], [])).ToArray());
    }
}
