using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalStagedGenerationTests
{
    private static readonly NaturalRequirements Requirements = new("처리 흐름", ["작업자", "장비"],
        [new("r1", "요청한다.", "behavior", "explicit", "요청한다."),
         new("r2", "결과를 확인한다.", "behavior", "explicit", "결과를 확인한다.")]);

    private static NaturalStagedPlan Plan()
    {
        var design = NaturalDesignTests.Design("sequence");
        return new(design.Title, design.Nodes, [], [], [
            new("request", "요청", ["r1"], design.Nodes.Select(n => n.Id).ToArray(), [new("outer", "alt", "문 상태", "닫힘")]),
            new("result", "결과", ["r2"], design.Nodes.Select(n => n.Id).ToArray(), [new("outer", "alt", "문 상태", "열림")])]);
    }

    [Fact]
    public void AssemblyKeepsSharedBranchesAndNamespacesNestedControls()
    {
        var plan = Plan();
        Assert.Null(NaturalStagedDesign.Check(plan, "sequence", Requirements));
        var parts = plan.Blocks.Select(block => (block, new NaturalDesign("부분", [],
            [NaturalDesignTests.Design("sequence").Edges[0] with { RequirementIds = block.RequirementIds,
                ControlPath = [new("nested", "loop", "응답 대기", "")] }], []))).ToArray();
        var design = NaturalStagedDesign.Assemble(plan, parts, "sequence");
        Assert.Equal(2, design.Edges.Count);
        Assert.Equal("닫힘", design.Edges[0].ControlPath[0].Branch);
        Assert.Equal("열림", design.Edges[1].ControlPath[0].Branch);
        Assert.Equal(design.Edges[0].ControlPath[0].Id, design.Edges[1].ControlPath[0].Id);
        Assert.NotEqual(design.Edges[0].ControlPath[1].Id, design.Edges[1].ControlPath[1].Id);
        Assert.Equal(2, design.Edges.Select(e => e.Id).Distinct().Count());
        Assert.Null(NaturalDesignValidation.Design(design, "sequence", Requirements));
        var dsl = new MermaidCompiler(new()).Compile(NaturalDesignValidation.Normalize(design, "sequence"));
        Assert.Contains("alt", dsl);
        Assert.Contains("else", dsl);
        Assert.Contains("loop", dsl);
    }

    [Fact]
    public void PlanAndBlocksRejectMissingCoverageUnknownParticipantsAndMalformedCollections()
    {
        var plan = Plan();
        Assert.Equal("NaturalRequirementCoverageMissing", NaturalStagedDesign.Check(plan with { Blocks = [plan.Blocks[0]] }, "sequence", Requirements));
        Assert.Equal("NaturalDesignFieldsInvalid", NaturalStagedDesign.Check(plan with { Nodes = null! }, "sequence", Requirements));
        Assert.Equal("NaturalDesignFieldsInvalid", NaturalStagedDesign.Check(plan with { Blocks = [plan.Blocks[0] with { ControlPath = [null!] }] }, "sequence", Requirements));
        var invalid = new NaturalDesign("부분", [], [NaturalDesignTests.Design("sequence").Edges[0] with { TargetId = "invented" }], []);
        Assert.Equal("NaturalIntegrationScopeInvalid", NaturalStagedDesign.CheckBlock(invalid, plan, plan.Blocks[0], "sequence"));
        Assert.Equal("NaturalDesignFieldsInvalid", NaturalStagedDesign.CheckBlock(invalid with { Edges = [null!] }, plan, plan.Blocks[0], "sequence"));
    }

    [Fact]
    public void ClassMembersCannotChangeTheSharedSkeleton()
    {
        var design = NaturalDesignTests.Design("class");
        var block = new NaturalStageBlock("members", "장비", ["r1"], [design.Nodes[0].Id], []);
        var plan = new NaturalStagedPlan(design.Title, [design.Nodes[0] with { Members = [] }], [], [], [block]);
        Assert.Null(NaturalStagedDesign.CheckBlock(design, plan, block, "class"));
        Assert.Equal("NaturalIntegrationScopeInvalid", NaturalStagedDesign.CheckBlock(design with {
            Nodes = [design.Nodes[0] with { Label = "다른 장비" }] }, plan, block, "class"));
        var result = NaturalStagedDesign.Assemble(plan, [(block, design)], "class");
        Assert.Equal(2, result.Nodes[0].Members.Count);
        Assert.Equal(plan.Nodes[0].Id, result.Nodes[0].Id);
    }

    [Fact]
    public void PatchChangesOnlyReportedFieldsAndRejectsStaleOrUnrelatedMutation()
    {
        var before = NaturalDesignTests.Design("flowchart");
        var target = before.Nodes[0];
        var issues = new[] { new NaturalIssue(target.Id, "label", "NaturalEvidenceMismatch", "문구를 고친다") };
        var patch = new NaturalDesignPatch(NaturalDesignPatching.Hash(before), [target with { Label = "문이 닫혔는가?" }], [], [], [], []);
        var after = NaturalDesignPatching.Apply(before, patch, "flowchart", issues);
        Assert.Equal(patch.Nodes[0].Label, after.Nodes[0].Label);
        Assert.Equal(before.Nodes.Skip(1), after.Nodes.Skip(1));
        Assert.Equal(before.Edges, after.Edges);
        Assert.Throws<ArgumentException>(() => NaturalDesignPatching.Apply(before, patch with { BaseHash = "stale" }, "flowchart", issues));
        Assert.Throws<ArgumentException>(() => NaturalDesignPatching.Apply(before, patch with { Nodes = [target with { Kind = "terminal" }] }, "flowchart", issues));
        Assert.Throws<ArgumentException>(() => NaturalDesignPatching.Apply(before, patch with { Nodes = [before.Nodes[1] with { Label = "무관한 변경" }] }, "flowchart", issues));
        Assert.Throws<ArgumentException>(() => NaturalDesignPatching.Apply(before, patch with { RemovedNodeIds = [target.Id] }, "flowchart", issues));
    }

    [Fact]
    public async Task TruncatedBlockSplitsLocallyAndReusesCompletedPlanAfterRestart()
    {
        var model = new StageModel();
        var options = new LlmOptions { Enabled = true };
        var client = new InternalLlmClient(Options.Create(options), new(), new(), model, new(model));
        IReadOnlyList<SemanticCheckpoint> saved;
        using (var first = new SemanticExecution(options, null, CancellationToken.None))
        {
            model.InterruptResult = true;
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.GenerateDesignedNaturalAsync("요청한다. 결과를 확인한다.",
                "sequence", false, new DiagramPresetCatalog().Resolve("sequence", "balanced"), null, Requirements, CancellationToken.None));
            saved = first.Checkpoints;
            Assert.Contains(saved, c => c.Stage == "natural-stage-plan" && c.State == "Completed");
            Assert.Contains(saved, c => c.Stage == "natural-stage-block" && c.State == "Completed");
        }
        var start = model.Purposes.Count;
        model.InterruptResult = false;
        using var resumed = new SemanticExecution(options, saved, CancellationToken.None);
        var result = await client.GenerateDesignedNaturalAsync("요청한다. 결과를 확인한다.", "sequence", false,
            new DiagramPresetCatalog().Resolve("sequence", "balanced"), null, Requirements, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(3, result.Diagram.Edges.Count);
        Assert.DoesNotContain("scenario-plan", model.Purposes.Skip(start));
        Assert.Equal(1, model.Purposes.Skip(start).Count(p => p == "scenario-block"));
        Assert.Equal(1, model.Purposes.Count(p => p == "scenario-plan"));
        Assert.Equal(1, model.Purposes.Count(p => p == "scenario-block-split"));
    }

    [Fact]
    public async Task InternalExceptionHasScopedTerminalCauseWithoutExceptionText()
    {
        var model = new StageModel { ThrowInternal = true };
        var options = new LlmOptions { Enabled = true };
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        using var scope = new NaturalDiagnosticScope("sequence", "s1", "sequence-s1");
        var client = new InternalLlmClient(Options.Create(options), new(), new(), model, new(model));
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateDesignedNaturalAsync("요청한다.", "sequence", false,
            new DiagramPresetCatalog().Resolve("sequence", "balanced"), null, Requirements, CancellationToken.None));
        Assert.Equal("NATURAL_INTERNAL_ERROR", error.Code);
        var terminal = Assert.Single(execution.Diagnostics, d => d.Kind == "Terminal");
        Assert.Equal("sequence-s1", terminal.PageId);
        Assert.Equal("MissingKey", terminal.ExceptionKind);
        Assert.DoesNotContain("private-exception-text", JsonSerializer.Serialize(execution.Diagnostics));
        Assert.DoesNotContain("private-exception-text", error.Message);
    }

    [Fact]
    public void TerminalSummaryDoesNotDoubleCountItsRootOrRecoveredEvents()
    {
        var root = new LlmDiagnostic("root", "design", "unit", "Failed", DateTimeOffset.UtcNow,
            ErrorCode: "NATURAL_DESIGN_INVALID", ValidationCode: "NaturalNodeRequirementIdsMissing", Kind: "Validation", PageId: "page", RecoveryState: "Exhausted");
        var terminal = root with { Id = "terminal", Kind = "Terminal", RootDiagnosticId = "root" };
        Assert.Equal(1, NaturalRunDiagnostics.Unresolved([root, terminal, root with { Id = "recovered", RecoveryState = "Recovered" }]));
        Assert.Equal(0, NaturalRunDiagnostics.Unresolved([root with { RecoveryState = "Recovered" }]));
    }

    private sealed class StageModel : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public bool InterruptResult, ThrowInternal;
        public List<string> Purposes { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
        {
            Purposes.Add(request.Purpose!);
            if (ThrowInternal) throw new KeyNotFoundException("private-exception-text");
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var context = doc.RootElement;
            object value;
            if (request.Purpose == "scenario-plan") value = Plan();
            else if (request.Purpose == "scenario-block-split")
            {
                var block = context.GetProperty("block").Deserialize<NaturalStageBlock>(PromptJson.Options)!;
                value = new NaturalBlockDivision([block with { Id = "a", Title = "요청 준비" }, block with { Id = "b", Title = "요청 전달" }]);
            }
            else if (request.Purpose == "scenario-review") value = new NaturalScenarioReview(["r1", "r2"], []);
            else
            {
                var block = context.GetProperty("block").Deserialize<NaturalStageBlock>(PromptJson.Options)!;
                if (block.Id == "request") throw new LlmClientException("LLM_RESPONSE_TRUNCATED", "truncated");
                if (block.Id == "result" && InterruptResult) throw new OperationCanceledException();
                value = new NaturalDesign("동작", [], [NaturalDesignTests.Design("sequence").Edges[0] with {
                    RequirementIds = block.RequirementIds, ControlPath = [] }], []);
            }
            var json = JsonSerializer.SerializeToNode(value, PromptJson.Options)!;
            ScenarioPipelineTests.ScenarioModel.Project(json, request.StructuredSchema!.Value);
            return Task.FromResult(new VllmCompletionResult(json.ToJsonString(), "stop", 1, true, false, 0, request.MaxOutputTokens, 10, 10, 20));
        }
    }
}
