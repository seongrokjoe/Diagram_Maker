using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class ScenarioPipelineTests
{
    [Fact]
    public void ReferenceAliasesAgreeInPromptSchemaAndReturnedLinks()
    {
        var ids = new PromptIds();
        const string original = "n12345678901234567890";
        var prompt = ids.Encode(JsonSerializer.Serialize(new { concepts = new[] { new { id = original } }, source = "ordinary source text" }));
        using var encoded = JsonDocument.Parse(prompt);
        var alias = encoded.RootElement.GetProperty("concepts")[0].GetProperty("id").GetString();
        var schema = ids.BindSchema(JsonSerializer.SerializeToElement(new { type = "object", properties = new {
            source = new { type = "string", @enum = new[] { original } } } }));
        Assert.Equal(alias, schema.GetProperty("properties").GetProperty("source").GetProperty("enum")[0].GetString());
        Assert.Contains("ordinary source text", prompt);
        using var restored = JsonDocument.Parse(ids.Restore(JsonSerializer.Serialize(new { source = alias, target = alias })));
        Assert.Equal(original, restored.RootElement.GetProperty("source").GetString());
        Assert.Equal(original, restored.RootElement.GetProperty("target").GetString());
    }

    [Theory]
    [InlineData("flowchart", "members", "guard")]
    [InlineData("sequence", "shape", "event")]
    [InlineData("state", "members", "controlPath")]
    public void ScenarioSchemasExcludeFieldsFromOtherDiagramTypes(string type, string nodeField, string edgeField)
    {
        var schema = NaturalDesignValidation.DesignSchemaFor(type).GetProperty("properties");
        Assert.False(schema.GetProperty("nodes").GetProperty("items").GetProperty("properties").TryGetProperty(nodeField, out _));
        Assert.False(schema.GetProperty("edges").GetProperty("items").GetProperty("properties").TryGetProperty(edgeField, out _));
    }

    [Fact]
    public async Task FormatBudgetIsSharedAcrossCandidatesAndRestoredWithoutChargingReplayedRequests()
    {
        IReadOnlyList<SemanticCheckpoint> saved;
        using (var execution = new SemanticExecution(new(), null, CancellationToken.None))
        {
            var budget = new DiagramRecoveryBudget("unit");
            for (var i = 0; i < 10; i++) await budget.ChargeAsync("format", "candidate" + i);
            saved = execution.Checkpoints;
        }
        using var resumed = new SemanticExecution(new(), saved, CancellationToken.None);
        var restored = new DiagramRecoveryBudget("unit");
        await restored.ChargeAsync("format", "candidate0");
        Assert.Equal(10, restored.FormatUsed);
        Assert.Equal("LLM_REPAIR_EXHAUSTED", (await Assert.ThrowsAsync<LlmClientException>(() => restored.ChargeAsync("format", "new"))).Code);
        await new DiagramRecoveryBudget("independent").ChargeAsync("format", "new");
    }

    [Fact]
    public async Task ChangedCandidatesCannotRepeatTheSameUnresolvedIssueIndefinitely()
    {
        var budget = new DiagramRecoveryBudget("no-progress");
        Assert.True(await budget.ObserveAsync("0", "first", ["r1:guard:polarity"], false));
        Assert.True(await budget.ObserveAsync("1", "second", ["r1:guard:polarity"], true));
        Assert.True(await budget.ObserveAsync("2", "third", ["r1:guard:polarity"], true));
        Assert.False(await budget.ObserveAsync("3", "fourth", ["r1:guard:polarity"], true));
    }

    [Fact]
    public void GroundedReviewHasOneDecisionSourceAndRejectsUnknownEvidence()
    {
        var requirements = NaturalDesignTests.Requirements();
        var review = new NaturalScenarioReview(["r1"], [new("r1", "guard", "NaturalConditionChanged", "Preserve the condition.", ["r1"], SourceQuote: NaturalDesignTests.Prompt, RelatedElementIds: [])]);
        Assert.Null(NaturalScenarioReviewValidation.Check(review, requirements, new HashSet<string>()));
        Assert.False(review.Accepted);
        Assert.DoesNotContain("accepted", JsonSerializer.Serialize(review, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("NaturalReviewTargetUnknown", NaturalScenarioReviewValidation.Check(review with {
            Issues = [review.Issues[0] with { EvidenceIds = ["unknown"] }] }, requirements, new HashSet<string>())!.Code);
    }

    [Fact]
    public void CyclicStateDiagramDoesNotRequireAnInventedBoundary()
    {
        var design = NaturalDesignTests.Design("state");
        design = design with { Nodes = design.Nodes.Where(n => n.Kind == "state").ToArray(),
            Edges = design.Edges.Where(e => e.Id == "e2").ToArray() };
        Assert.Null(NaturalDesignValidation.Design(design, "state", NaturalDesignTests.Requirements()));
    }

    [Theory]
    [InlineData("flowchart")]
    [InlineData("sequence")]
    [InlineData("state")]
    [InlineData("class")]
    public async Task AllRequirementsInAScenarioUseOneDesignAndOneGroundedReview(string type)
    {
        var requirements = NaturalDesignTests.Requirements();
        requirements = requirements with { Requirements = [.. requirements.Requirements,
            new("r2", "결과를 알린다", "behavior", "explicit", "") ] };
        var transport = new ScenarioModel();
        var client = new InternalLlmClient(Options.Create(new LlmOptions { Enabled = true }), new(), new(), transport, new(transport));
        var result = await client.GenerateDesignedNaturalAsync("문 조건을 검사하고 결과를 알린다", type, false,
            new DiagramPresetCatalog().Resolve(type, "balanced"), null, requirements, CancellationToken.None);
        Assert.Equal(type is "sequence" or "class"
            ? new[] { "scenario-plan", type == "class" ? "class-members" : "scenario-block", "scenario-review" }
            : new[] { "scenario-design", "scenario-review" }, transport.Purposes);
        Assert.Equal(2, result!.Quality!.ReviewedRequirementIds.Count);
        Assert.NotEmpty(new MermaidCompiler(new()).Compile(result.Diagram));
    }

    internal sealed class ScenarioModel : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public List<string> Purposes { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            Purposes.Add(request.Purpose!);
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var root = doc.RootElement;
            var ids = root.GetProperty("requirements").GetProperty("requirements").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray();
            object value;
            if (request.Purpose == "scenario-review") value = new NaturalScenarioReview(ids, []);
            else
            {
                var design = NaturalDesignTests.Design(root.GetProperty("type").GetString()!);
                value = design with { Nodes = design.Nodes.Select(n => n with { RequirementIds = ids }).ToArray(),
                    Edges = design.Edges.Select(e => e with { RequirementIds = ids }).ToArray() };
            }
            var json = JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Adapt(json, request);
            Project(json, request.StructuredSchema!.Value);
            return Task.FromResult(new VllmCompletionResult(json.ToJsonString(), "stop", 1, true, false, 0, request.MaxOutputTokens, 10, 10, 20));
        }

        // The fixture emits the declared contract, then production code validates
        // references and semantics. It never repairs a response on the server.
        internal static void Adapt(JsonNode value, VllmCompletionRequest request)
        {
            if (value is not JsonObject obj || obj["nodes"] is not JsonArray nodes) return;
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var context = doc.RootElement;
            var properties = request.StructuredSchema!.Value.GetProperty("properties");
            if (properties.TryGetProperty("blocks", out _))
            {
                var ids = nodes.Select(n => n!["id"]!.GetValue<string>()).ToArray();
                var refs = context.GetProperty("requirements").GetProperty("requirements").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToArray();
                var type = context.GetProperty("type").GetString();
                obj["blocks"] = JsonSerializer.SerializeToNode((type == "class" ? ids.Select(id => new[] { id }) : [ids])
                    .Select((blockIds, index) => new { id = "block" + index, title = "동작 " + index,
                        requirementIds = refs, nodeIds = blockIds, controlPath = Array.Empty<ControlScope>() }));
                if (type == "class") foreach (var node in nodes) node!["members"] = new JsonArray();
                else obj["edges"] = new JsonArray();
            }
            else if (context.TryGetProperty("block", out var block))
            {
                if (context.GetProperty("type").GetString() == "sequence") obj["nodes"] = new JsonArray();
                else
                {
                    var id = block.GetProperty("nodeIds")[0].GetString();
                    obj["nodes"] = new JsonArray(nodes.Where(n => n!["id"]!.GetValue<string>() == id).Select(n => n!.DeepClone()).ToArray());
                    obj["edges"] = new JsonArray();
                }
            }
            else if (properties.TryGetProperty("baseHash", out var hash))
            {
                obj["baseHash"] = hash.GetProperty("enum")[0].GetString();
                obj["removedNodeIds"] = new JsonArray(); obj["removedEdgeIds"] = new JsonArray(); obj["edgeOrder"] = new JsonArray();
            }
        }

        internal static void Project(JsonNode value, JsonElement schema)
        {
            if (value is JsonObject obj && schema.TryGetProperty("properties", out var properties))
                foreach (var key in obj.Select(p => p.Key).ToArray())
                    if (!properties.TryGetProperty(key, out var property)) obj.Remove(key);
                    else if (obj[key] is { } child) Project(child, property);
            if (value is JsonArray array && schema.TryGetProperty("items", out var items))
                foreach (var child in array) if (child is not null) Project(child, items);
        }
    }
}
