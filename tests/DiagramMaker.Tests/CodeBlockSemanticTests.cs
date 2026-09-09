using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class CodeBlockSemanticTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const string GuardSource = """
        public async Task GuardAsync(HttpContext context, RequestDelegate next, bool sampleMode)
        {
            if (IsCodeBlockPath(context.Request.Path))
            {
                if (sampleMode)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = "샘플 모드" });
                    return;
                }
                var origin = context.Request.Headers.Origin.ToString();
                var development = context.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment();
                if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}" &&
                    !(development && origin == "http://localhost:5173"))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = "허용되지 않은 출처" });
                    return;
                }
                var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 2_000_000;
            }
            await next(context);
        }
        """;

    [Fact]
    public void GuardGroupsResponseAndReturnAndKeepsEveryDecisionAndSourceReference()
    {
        var (_, graph, ir) = Candidate(GuardSource);
        var plan = Plan(ir, mergeResponses: true);
        Assert.Null(InternalLlmClient.ValidateCodePlan(ir, plan));
        Assert.Equal(2, plan.Elements.Count(e => e.NodeIds.Count == 3));
        var result = InternalLlmClient.ApplyCodePlan(ir, plan);
        Assert.Equal(ir.Nodes.Count - 4, result.Nodes.Count);
        Assert.Equal(ir.Nodes.Count(n => n.Kind == "condition"), result.Nodes.Count(n => n.Kind == "condition"));
        Assert.Equal(2, result.Nodes.Count(n => n.Label == "403 오류 응답을 보내고 종료" && n.Kind == "return"));
        Assert.DoesNotContain(result.Nodes, n => n.Label.Contains("return;") || n.Label.Contains("&&") || n.Label == "데이터 처리");
        Assert.All(result.Nodes, n => Assert.All(n.EvidenceIds, id => Assert.Contains(graph.Evidence, e => e.Id == id)));
        Assert.True(ir.Nodes.SelectMany(n => n.SourceFactIds!).ToHashSet().SetEquals(result.Nodes.SelectMany(n => n.SourceFactIds!)));
        Assert.Contains(result.Nodes, n => n.Label.Contains("2,000,000"));
        var request = Assert.Single(result.Nodes, n => n.Label == "코드 블럭 요청인가요?");
        Assert.Equal(2, result.Edges.Count(e => e.SourceId == request.Id));
    }

    [Theory]
    [InlineData("void Run(){ if (ready) { Send(); return; } Next(); }")]
    [InlineData("class Code { void One(){ Send(); } void Two(){ Next(); } }")]
    [InlineData("void Run(){ while(ready) { Send(); } Next(); }")]
    public void RejectsAbstractionAcrossBranchFunctionOrLoop(string code)
    {
        var (_, _, ir) = Candidate(code);
        var plan = Plan(ir);
        var actions = ir.Nodes.Where(n => n.Kind is "call" or "operation" or "return").ToArray();
        var merged = new CodeBlockSemanticElement("unsafe", "응답을 보내고 다음 처리를 진행", actions.Select(n => n.Id).ToArray(),
            actions.SelectMany(n => n.SourceFactIds!).Distinct().ToArray(), "", "처리를 완료합니다");
        plan = plan with { Elements = plan.Elements.Where(e => !e.NodeIds.Any(id => actions.Any(n => n.Id == id))).Append(merged).ToArray() };
        Assert.NotNull(InternalLlmClient.ValidateCodePlan(ir, plan));
    }

    [Fact]
    public void SingleFunctionHasOnePageAndCallsWithoutCandidatesDoNotWarnAboutQuestionLimit()
    {
        var (input, graph, _) = Candidate(GuardSource);
        var pages = new CodeBlockProjectionService(new()).Build(graph, [], new("g", "guard", ["a"]), new("v", "flowchart", "balanced"));
        Assert.Single(pages); Assert.All(pages[0].Diagram.Nodes, n => Assert.Null(n.DetailPageId));
        var run = new CodeBlockRun(Guid.NewGuid(), Guid.NewGuid(), "owner", 1, 1, input, CodeBlockRunState.Indexing, 0, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var grouping = new CodeBlockGroupingService(Options.Create(new CodeBlockOptions())).Prepare(run, graph);
        Assert.Empty(grouping.Questions); Assert.DoesNotContain(grouping.Warnings, w => w.Contains("질문"));
    }

    [Theory]
    [InlineData("missing-evidence", "plan-validation", 2)]
    [InlineData("inverted-branch", "semantic-review", 4)]
    [InlineData("invented-relationship", "plan-validation", 2)]
    [InlineData("malformed", "plan-validation", 2)]
    [InlineData("null-element", "plan-validation", 2)]
    [InlineData("timeout", "llm-request", 2)]
    public async Task InvalidSyntheticResponsesAreIncompleteAfterAtMostOneRepair(string mode, string stage, int requests)
    {
        var (input, graph, ir) = Candidate(GuardSource);
        var transport = new SyntheticTransport(mode);
        var client = new InternalLlmClient(Options.Create(new LlmOptions { Enabled = true }), new(), new(), transport, new(transport));
        var result = await client.PlanCodeBlockDiagramAsync(ir, input, graph, null, new("v", "flowchart", "balanced"), CancellationToken.None);
        Assert.Equal("Incomplete", result!.Status); Assert.Same(ir, result.Diagram);
        Assert.Equal(stage, result.FailureStage); Assert.Equal(2, result.Attempts); Assert.Equal(requests, transport.Requests.Count);
    }

    [Fact]
    public async Task ValidSyntheticPlanReceivesFullFunctionContextAndReturnsMergedMeaning()
    {
        var (input, graph, ir) = Candidate(GuardSource);
        var transport = new SyntheticTransport("valid");
        var client = new InternalLlmClient(Options.Create(new LlmOptions { Enabled = true }), new(), new(), transport, new(transport));
        var result = await client.PlanCodeBlockDiagramAsync(ir, input, graph, null, new("v", "flowchart", "balanced"), CancellationToken.None);
        Assert.Equal("Semantic", result!.Status); Assert.Equal(2, transport.Requests.Count);
        using var prompt = JsonDocument.Parse(transport.Requests[0].UserPrompt);
        Assert.Equal(GuardSource, prompt.RootElement.GetProperty("context").GetProperty("functions")[0].GetProperty("code").GetString());
        Assert.All(result.Explanation!.Behaviors!, b => Assert.All(b.NodeIds, id => Assert.Contains(result.Diagram.Nodes, n => n.Id == id)));
    }

    private static (CodeBlockWorkspaceInput, CodeBlockGraph, DiagramIr) Candidate(string code)
    {
        var input = new CodeBlockWorkspaceInput("접근 검사", [new("a", "csharp", "접근 검사", code, "요청의 접근 허용 여부 검사")]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        return (input, graph, new CodeBlockProjectionService(new()).Build(graph, [], new("g", "접근 검사", ["a"]), new("v", "flowchart", "balanced"))[0].Diagram);
    }
    private static CodeBlockSemanticPlan Plan(DiagramIr ir, bool mergeResponses = false)
    {
        var covered = new HashSet<string>();
        var elements = new List<CodeBlockSemanticElement>();
        foreach (var node in ir.Nodes)
        {
            if (!covered.Add(node.Id)) continue;
            var members = new List<DiagramNode> { node };
            if (mergeResponses && node.Kind == "operation" && node.Context?.Statement.Contains("StatusCode = 403") == true)
            {
                var next = ir.Nodes.First(n => n.Id == ir.Edges.Single(e => e.SourceId == node.Id).TargetId);
                var last = ir.Nodes.First(n => n.Id == ir.Edges.Single(e => e.SourceId == next.Id).TargetId);
                members.Add(next); members.Add(last); covered.Add(next.Id); covered.Add(last.Id);
            }
            var source = node.Kind is "condition" or "loop" ? node.Label : node.Context?.Statement ?? node.Label;
            var label = members.Count > 1 ? "403 오류 응답을 보내고 종료" : node.Kind == "condition"
                ? source.Contains("IsCodeBlockPath") ? "코드 블럭 요청인가요?" : source.Contains("sampleMode") ? "샘플 모드인가요?" : source.Contains("origin.Length") ? "허용되지 않은 출처인가요?" : "본문 제한을 변경할 수 있나요?"
                : source.Contains("MaxRequestBodySize =") ? "요청 본문을 최대 2,000,000바이트로 제한" : source.Contains("GetRequiredService") ? "등록된 환경 정보를 조회" : source.Contains("next(context)") ? "다음 요청 처리기로 전달" : "요청 정보를 확인하고 처리";
            elements.Add(new(node.Id, label, members.Select(n => n.Id).ToArray(), members.SelectMany(n => n.SourceFactIds!).Distinct().ToArray(),
                node.Kind is "condition" or "loop" ? label : "", label));
        }
        return new("요청 출처와 실행 환경을 검사하고 허용된 요청을 전달합니다", elements,
            ir.Edges.Select(e => new SemanticMessage(e.Id, "확인된 처리 흐름")).ToArray(),
            ir.Nodes.Select(n => new CodeBlockBehavior(n.Id, n.Kind is "condition" or "loop" ? n.Label : n.Context?.Statement ?? "요청의 시작과 종료", n.SourceFactIds!, [n.Id], [])).ToArray());
    }
    private sealed class SyntheticTransport(string mode) : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public List<VllmCompletionRequest> Requests { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (mode == "timeout") throw new LlmClientException("LLM_TIMEOUT", "Synthetic timeout");
            using var prompt = JsonDocument.Parse(request.UserPrompt);
            string response;
            if (request.StructuredSchema!.Value.GetProperty("properties").TryGetProperty("accepted", out _))
                response = JsonSerializer.Serialize(new DiagramPlanReview(mode != "inverted-branch", mode == "inverted-branch" ? ["Predicate meaning was reversed"] : []), Json);
            else
            {
                var ir = prompt.RootElement.GetProperty("context").GetProperty("candidate").Deserialize<DiagramIr>(Json)!;
                var plan = Plan(ir, true);
                if (mode == "missing-evidence") plan = plan with { Elements = plan.Elements.Select((e, i) => i == 0 ? e with { FactIds = [] } : e).ToArray() };
                if (mode == "inverted-branch") plan = plan with { Elements = plan.Elements.Select(e => e.Condition == "샘플 모드인가요?" ? e with { Condition = "운영 모드인가요?" } : e).ToArray() };
                if (mode == "invented-relationship") plan = plan with { Messages = plan.Messages.Append(new("fake-edge", "새 호출을 실행")).ToArray() };
                if (mode == "null-element") plan = plan with { Elements = [null!] };
                response = mode == "malformed" ? "{not-json}" : JsonSerializer.Serialize(plan, Json);
            }
            return Task.FromResult(new VllmCompletionResult(response, "stop", 1, true, false, 0, 1000, 10, 10, 20));
        }
    }
}
