using System.Text.Json;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Configuration;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class ExecutionMeaningTests
{
    private static (ExecutionMeaningInput Input, ExecutionMeaningPlan Plan) Example()
    {
        const string code = "bool Run(){bool result=false; if(Check()!=true)return result; Save(); Save(); result=true; return result;}";
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [new("a", "csharp", "함수", code)]);
        var symbol = Assert.Single(graph.Symbols);
        var input = new ExecutionMeaningInput(symbol.Id, symbol.Name, new("code-block", "", "a", 1, 1, 0, code.Length), code, symbol.Execution!);
        var plan = new ExecutionMeaningPlan("조건을 확인하고 저장 호출 뒤 결과를 반환합니다", "implementation", ExecutionMeaningValidation.Steps(input.Events),
            ExecutionSequenceProjection.Flatten(input.Events).Select(e => new ExecutionMeaningUnit("원본 동작을 처리합니다", "외부 호출 구현은 미확인입니다", [e.Id])).ToArray());
        Assert.Null(ExecutionMeaningValidation.Check(input, plan));
        return (input, plan);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("reorder")]
    [InlineData("polarity")]
    [InlineData("return")]
    [InlineData("termination")]
    [InlineData("state")]
    public void RejectsChangesToObservedExecution(string fault)
    {
        var (input, plan) = Example();
        var steps = plan.Steps.ToList();
        var call = steps.FindIndex(s => s.Kind == "call");
        var branch = steps.FindIndex(s => s.Kind == "branch");
        var returned = steps.FindIndex(s => s.Kind == "return");
        switch (fault)
        {
            case "missing": steps.RemoveAt(call); break;
            case "duplicate": steps.Insert(call, steps[call]); break;
            case "reorder": (steps[call], steps[returned]) = (steps[returned], steps[call]); break;
            case "polarity": steps[branch] = steps[branch] with { Expression = "Check()==true" }; break;
            case "return": steps[returned] = steps[returned] with { Value = "true" }; break;
            case "termination": steps[returned] = steps[returned] with { TerminationTarget = "loop" }; break;
            case "state": steps[branch] = steps[branch] with { Kind = "state" }; break;
        }
        Assert.NotNull(ExecutionMeaningValidation.Check(input, plan with { Steps = steps }));
    }

    [Fact]
    public void UnitsCannotLoseRepeatCallsOrCombineControlBoundaries()
    {
        var (input, plan) = Example();
        Assert.NotNull(ExecutionMeaningValidation.Check(input, plan with { Units = plan.Units.Skip(1).ToArray() }));
        Assert.Equal("ExecutionPlanCrossesControlBoundary", ExecutionMeaningValidation.Check(input, plan with
        { Units = [new("처리를 합칩니다", "조건과 저장을 한 단계로 합칩니다", plan.Steps.Select(s => s.Id).ToArray())] }));
    }

    [Fact]
    public void ExecutionContractsRetainUnknownValuesAndReadLegacyGraphs()
    {
        var (input, _) = Example();
        var serialized = JsonSerializer.Serialize(input);
        Assert.Equal(serialized, JsonSerializer.Serialize(JsonSerializer.Deserialize<ExecutionMeaningInput>(serialized)));
        Assert.Contains(input.Events.SelectMany(e => ExecutionSequenceProjection.Flatten([e])), e => e.Kind == "call" && e.Value == "unknown");
    }

    [Fact]
    public async Task UnsupportedControlNeverClaimsCompleteWholeFunctionMeaning()
    {
        const string code = "void Run(){ try { Save(); } catch { Recover(); } }";
        var input = new CodeBlockWorkspaceInput("함수", [new("a", "csharp", "함수", code)]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var client = new InternalLlmClient(Options.Create(new LlmOptions { Enabled = true }), new(), new(), transport, new(transport));
        var result = await client.PlanCodeBlockGroupAsync(input, graph, new("g", "함수", ["a"]), [new("s", "sequence", "balanced")], CancellationToken.None);
        var page = Assert.Single(result!.Pages.Values);
        Assert.Equal("Incomplete", page.Status);
        Assert.Equal("Incomplete", page.Explanation?.Status);
        Assert.Equal(new[] { "Save()", "Recover()" }, page.Diagram.Edges.Where(e => e.Type == "message").Select(e => e.OriginalExpression));
    }

    [Fact]
    public async Task LongFunctionPartitionsCompleteRegionsAndReviewsTheReassembledFunction()
    {
        var code = "int Run(int value){" + string.Concat(Enumerable.Range(0, 75).Select(i => $"value += {i};")) +
            "if(value<0){Save();return -1;}return value;}";
        var input = new CodeBlockWorkspaceInput("긴 함수", [new("a", "csharp", "함수", code)]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var observed = ExecutionMeaningValidation.Steps(Assert.Single(graph.Symbols).Execution!);
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var client = new InternalLlmClient(Options.Create(new LlmOptions { Enabled = true }), new(), new(), transport, new(transport));
        var result = await client.PlanCodeBlockGroupAsync(input, graph, new("g", "함수", ["a"]), [new("s", "sequence", "balanced")], CancellationToken.None);
        var parts = transport.Requests.Where(r => r.Purpose == "execution-plan").ToArray();
        Assert.True(parts.Length > 1);
        var suppliedSteps = new List<(string Kind, string Expression)>();
        foreach (var request in parts)
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            Assert.Equal(code, json.RootElement.GetProperty("source").GetString());
            var steps = json.RootElement.GetProperty("steps").Deserialize<ExecutionMeaningStep[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var ids = steps.Select(s => s.Id).ToHashSet();
            Assert.All(steps, s => Assert.All(s.ChildIds.Concat(s.AlternativeIds).Concat(s.EvaluationIds), id => Assert.Contains(id, ids)));
            suppliedSteps.AddRange(steps.Select(s => (s.Kind, s.Expression)));
        }
        Assert.Equal(observed.Select(s => (s.Kind, s.Expression)), suppliedSteps);
        using var review = JsonDocument.Parse(Assert.Single(transport.Requests, r => r.Purpose == "execution-review").UserPrompt);
        Assert.Equal(code, review.RootElement.GetProperty("source").GetString());
        Assert.Equal(observed.Select(s => (s.Kind, s.Expression)), review.RootElement.GetProperty("plan").GetProperty("steps").EnumerateArray()
            .Select(s => (s.GetProperty("kind").GetString()!, s.GetProperty("expression").GetString()!)));
        Assert.All(result!.Pages.Values, page => Assert.Equal("Semantic", page.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedRegionStaysIntactAndCannotClaimReviewedMeaning(bool inputBudget)
    {
        var code = "void Run(int value){if(value>0){" + string.Concat(Enumerable.Range(0, inputBudget ? 10 : 60).Select(i => $"Save({i});")) + "}}";
        var input = new CodeBlockWorkspaceInput("제어 영역", [new("a", "csharp", "함수", code)]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var options = new LlmOptions { Enabled = true, MaxInputCharacters = inputBudget ? 4000 : 100000 };
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        var result = await client.PlanCodeBlockGroupAsync(input, graph, new("g", "함수", ["a"]), [new("s", "sequence", "balanced")], CancellationToken.None);
        Assert.DoesNotContain(transport.Requests, r => r.Purpose is "execution-plan" or "execution-review");
        Assert.All(result!.Pages.Values, page => {
            Assert.Equal("Incomplete", page.Status);
            Assert.Equal("execution-plan", page.FailureStage);
            Assert.DoesNotContain("whole-function-reviewed", page.Diagram.Provenance);
        });
        Assert.Equal(inputBudget ? 10 : 60, Assert.Single(result.Pages.Values).Diagram.Edges.Count(e => e.Type == "message"));
    }
}
