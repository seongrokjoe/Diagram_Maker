using System.Diagnostics;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalTestExecutionTests
{
    [Fact]
    public async Task SelectedCaseAndTypeUseOnlyTheRequestedScenarioAndSkipCrossViewReview()
    {
        var model = new Model();
        var events = new List<NaturalDiagramTestEvent>();
        var result = await Test(model, new() { Enabled = true }).RunAsync(CancellationToken.None, "table-interlock", "state", events.Add);
        Assert.True(result.Success, result.Report);
        Assert.Equal("table-interlock", Assert.Single(result.Cases).Id);
        Assert.Equal(new[] { "requirements", "requirements-review", "scenario-design", "scenario-review" }, model.Purposes);
        Assert.Contains(events, e => e.Type == "case-completed");
        Assert.DoesNotContain(NaturalDiagramSelfTest.TablePrompt, JsonSerializer.Serialize(events));
    }

    [Fact]
    public async Task DeadlineIncludesBothCasesAndLateCompletionCannotPublishSuccess()
    {
        var model = new Model { DelayFirst = 400, DelayAfterFirst = 2200 };
        var watch = Stopwatch.StartNew();
        var result = await Test(model, new() { Enabled = true, SemanticJobBudgetSeconds = 1 }).RunAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("Completed", result.Cases[0].State);
        Assert.Equal("NATURAL_EXECUTION_BUDGET", result.Cases[1].ErrorCode);
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 6);
        Assert.Equal(1, result.Execution.BudgetSeconds);
        Assert.DoesNotContain(model.Purposes.Skip(4), purpose => purpose == "scenario-review");
        var before = JsonSerializer.Serialize(result);
        await Task.Delay(2300);
        Assert.Equal(before, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task CancellationPreservesCompletedCaseAndMarksUnstartedCaseSkipped()
    {
        using var stop = new CancellationTokenSource();
        var result = await Test(new(), new() { Enabled = true }).RunAsync(stop.Token, reportProgress: e => {
            if (e.Type == "case-completed") stop.Cancel();
        });
        Assert.Equal("Completed", result.Cases[0].State);
        Assert.Equal("Skipped", result.Cases[1].State);
        Assert.Equal("NATURAL_CANCELLED", result.Cases[1].ErrorCode);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task TimeoutPreservesAlreadyReviewedPagesInTheActiveCase()
    {
        var model = new Model { DelayAfterFirst = 2200 };
        var result = await Test(model, new() { Enabled = true, SemanticJobBudgetSeconds = 1 })
            .RunAsync(CancellationToken.None, "table-interlock");
        Assert.Equal("NATURAL_EXECUTION_BUDGET", result.Cases[0].ErrorCode);
        Assert.Equal(1, result.Cases[0].ReviewedPages);
        Assert.Equal(1, result.Cases[0].Pages);
    }

    [Fact]
    public async Task CrossViewFindingRegeneratesOnlyItsTargetPageAndKeepsTheOtherArtifact()
    {
        var model = new Model { RejectCrossViewOnce = true };
        var options = new LlmOptions { Enabled = true };
        await using var store = new InMemoryAppStore();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(Client(model, options), new(new()), store, cache, new(), Options.Create(options), new Environment());
        var snapshots = new List<NaturalGenerationProgress>();
        var result = await service.GenerateAsync(new("요청한다.", Views: [new("flow", "flowchart", "balanced"), new("sequence", "sequence", "balanced")]),
            "owner", CancellationToken.None, (value, _) => { snapshots.Add(value); return Task.CompletedTask; });
        Assert.All(result.Views!, view => Assert.Equal("Completed", view.State));
        Assert.Equal(new[] { "flowchart", "sequence", "flowchart" }, model.DesignedTypes);
        var originalSequence = snapshots.SelectMany(s => s.Views).First(v => v.ViewId == "sequence").Diagram!.Id;
        Assert.Equal(originalSequence, result.Views![1].Diagram!.Id);
        Assert.Contains(result.Views[0].Diagram!.Ir.Nodes, n => n.Label.Contains("수정"));
        Assert.Equal(2, model.CrossReviews);
    }

    [Fact]
    public void InvalidSelectionsAreRejected() => Assert.False(NaturalDiagramSelfTest.ValidSelection("short-approval", "class"));

    private static InternalLlmClient Client(Model model, LlmOptions options) => new(Options.Create(options), new(), new(), model, new(model));
    private static NaturalDiagramSelfTest Test(Model model, LlmOptions options) => new(Client(model, options), new(new()), new(), Options.Create(options), new Environment());

    private sealed class Model : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public int DelayFirst, DelayAfterFirst;
        public bool RejectCrossViewOnce;
        public int CrossReviews;
        public List<string> Purposes { get; } = [];
        public List<string> DesignedTypes { get; } = [];
        public async Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            Purposes.Add(request.Purpose!);
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var root = doc.RootElement;
            object result;
            if (request.Purpose == "requirements")
            {
                var ranges = root.GetProperty("sourceRanges").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();
                result = NaturalExtraction.FromRequirements(new("합성 요청", ["장비"],
                    [new("r1", "요청을 처리한다", "behavior", "explicit", "", SourceRangeIds: ranges)],
                    Scenarios: [new("s1", "요청 처리", ["r1"], ranges)], Questions: []));
            }
            else if (request.Purpose == "requirements-review")
                result = new NaturalSourceReview(root.GetProperty("sourceRanges").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray(), []);
            else if (request.Purpose == "natural-final-review")
            {
                CrossReviews++;
                var page = root.GetProperty("views")[0].GetProperty("pages")[0];
                result = new NaturalScenarioReview(["r1"], RejectCrossViewOnce && CrossReviews == 1 ?
                    [new(page.GetProperty("id").GetString()!, "label", "NaturalConditionChanged", "조건을 수정하세요", ["r1"])] : []);
            }
            else if (request.Purpose == "scenario-review") result = new NaturalScenarioReview(["r1"], []);
            else
            {
                var type = root.GetProperty("type").GetString()!;
                DesignedTypes.Add(type);
                var delay = DesignedTypes.Count == 1 ? DelayFirst : DelayAfterFirst;
                // Deliberately ignore cancellation to verify the completion boundary rejects late data.
                if (delay > 0) await Task.Delay(delay, CancellationToken.None);
                var design = NaturalDesignTests.Design(type);
                if (root.GetProperty("issues").GetArrayLength() > 0)
                    design = design with { Nodes = design.Nodes.Select(n => n with { Label = n.Label + " 수정" }).ToArray() };
                result = design;
            }
            var value = JsonSerializer.SerializeToNode(result, PromptJson.Options)!;
            ScenarioPipelineTests.ScenarioModel.Project(value, request.StructuredSchema!.Value);
            return new(value.ToJsonString(), "stop", 1, true, false, 0, request.MaxOutputTokens, 10, 10, 20);
        }
    }

    private sealed class Environment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
