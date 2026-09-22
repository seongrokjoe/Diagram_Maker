using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalSemanticAssemblyTests
{
    private static NaturalRequirements Requirements() => new("두 요구사항", ["장비"],
        [new("r1", "요청을 처리한다.", "behavior", "explicit", ""), new("r2", "결과를 확인한다.", "behavior", "explicit", "")]);

    [Theory]
    [InlineData("flowchart")]
    [InlineData("sequence")]
    [InlineData("class")]
    [InlineData("state")]
    public void ServerAssignsStableOwnershipAndKeepsIndependentSequencesSeparate(string type)
    {
        var requirements = Requirements();
        var plan = new NaturalSemanticPlan(type, requirements, requirements.Requirements.Select(r =>
            new NaturalSemanticUnitResult(r.Id, NaturalDesignTests.Meaning(type), false)).ToArray());
        var design = NaturalSemanticAssembly.Assemble(plan);
        Assert.Null(NaturalDesignValidation.Design(design, type, requirements));
        Assert.Equal(JsonSerializer.Serialize(design), JsonSerializer.Serialize(NaturalSemanticAssembly.Assemble(plan)));
        Assert.All(design.Nodes, node => Assert.NotEmpty(node.RequirementIds));
        if (type is "class" or "sequence") Assert.Single(design.Nodes, n => n.Label == "장비");
        else Assert.Equal(2, design.Nodes.Count(n => n.Label == (type == "state" ? "대기" : "운전")));
        var compiled = NaturalSemanticAssembly.Compile(plan);
        if (type == "sequence") Assert.Equal(2, compiled.SequenceBlocks!.Count);
        Assert.NotEmpty(new MermaidCompiler(new()).Compile(compiled));
    }

    [Fact]
    public void LocalReferencesAndMemberAssumptionsAreValidatedBeforeAssembly()
    {
        var meaning = NaturalDesignTests.Meaning("flowchart");
        Assert.Equal("NaturalMeaningReferencesInvalid", Assert.Single(NaturalSemanticAssembly.Validate(
            meaning with { Connections = [meaning.Connections[0] with { Target = "missing" }] }, "flowchart", Requirements(), "r1")).Code);
        var classUnit = NaturalDesignTests.Meaning("class");
        classUnit = classUnit with { Concepts = [classUnit.Concepts[0] with { Members =
            classUnit.Concepts[0].Members!.Select(m => m with { Assumption = true }).ToArray() }] };
        var ir = NaturalSemanticAssembly.Compile(new("class", Requirements(), [new("r1", classUnit, false)]));
        Assert.Contains(ir.Nodes[0].Details!, text => text.Contains("설계 가정"));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task WholeScenarioRepairsShareTenCorrections(int rejectedReviews, bool success)
    {
        var model = new Model(rejectedReviews);
        var client = new InternalLlmClient(Options.Create(new LlmOptions { Enabled = true }), new(), new(), model, new(model));
        var task = client.GenerateDesignedNaturalAsync("요청을 처리한다. 결과를 확인한다.", "flowchart", false,
            new DiagramPresetCatalog().Resolve("flowchart", "balanced"), null, Requirements(), CancellationToken.None);
        if (success)
        {
            var result = await task;
            Assert.True(result!.Quality!.RepairUsed);
            Assert.Equal(2, result.Quality.ReviewedRequirementIds.Count);
        }
        else Assert.Equal("NATURAL_DESIGN_REJECTED", (await Assert.ThrowsAsync<LlmClientException>(() => task)).Code);
        Assert.Equal(11, model.Generations);
        Assert.Equal(11, model.Reviews);
    }

    private sealed class Model(int rejectedReviews) : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public int Generations { get; private set; }
        public int Reviews { get; private set; }
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(request.UserPrompt);
            var context = json.RootElement;
            var ids = context.GetProperty("requirements").GetProperty("requirements").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();
            object value;
            if (request.Purpose is "scenario-design" or "scenario-repair")
            {
                Generations++;
                var design = NaturalDesignTests.Design("flowchart");
                value = design with { Nodes = design.Nodes.Select(n => n with { Label = n.Label + Generations, RequirementIds = ids }).ToArray(),
                    Edges = design.Edges.Select(e => e with { RequirementIds = ids }).ToArray() };
            }
            else
            {
                Reviews++;
                value = new NaturalScenarioReview(ids, Reviews <= rejectedReviews ?
                    [new("r2", "label", Reviews % 2 == 0 ? "NaturalConditionChanged" : "NaturalUnsupportedClaim", "조건을 보존하세요", ["r2"])] : []);
            }
            var wire = JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            ScenarioPipelineTests.ScenarioModel.Project(wire, request.StructuredSchema!.Value);
            return Task.FromResult(new VllmCompletionResult(wire.ToJsonString(), "stop", 1, true, false, 0, request.MaxOutputTokens, 10, 10, 20));
        }
    }
}