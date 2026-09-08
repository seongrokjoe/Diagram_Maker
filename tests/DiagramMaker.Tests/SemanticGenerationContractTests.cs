using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class SemanticGenerationContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task InputLimit_SplitsWholeContextsAndRetainsBothRevisionEvidence()
    {
        var transport = new PlanningTransport();
        var options = new LlmOptions { Enabled = true, MaxInputCharacters = 16_000 };
        IInternalLlmClient client = Client(options, transport);
        var (candidate, bundle) = Input(2, 4300);
        var result = await client.PlanDiagramAsync(candidate, bundle, null, new("view", "flowchart", "balanced"), null, false, CancellationToken.None);
        Assert.Equal("Semantic", result!.Status);
        Assert.Equal(2, result.Diagram.Nodes.Count);
        Assert.Equal(2, result.Explanation!.Changes.Count);
        Assert.Equal(4, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.True(request.SystemPrompt.Length + request.UserPrompt.Length <= options.MaxInputCharacters));
        var plans = transport.Requests.Where(request => request.StructuredSchema!.Value.GetProperty("properties").TryGetProperty("elements", out _)).ToArray();
        Assert.Equal(2, plans.Length);
        Assert.All(plans, request =>
        {
            using var input = JsonDocument.Parse(request.UserPrompt);
            var sources = input.RootElement.GetProperty("facts").EnumerateArray().Where(fact => fact.GetProperty("kind").GetString() == "source").ToArray();
            Assert.Equal(2, sources.Length);
            Assert.All(sources, source => Assert.Equal(4300, source.GetProperty("content").GetString()!.Length));
            Assert.Contains(sources, source => source.GetProperty("span").GetProperty("revisionSha").GetString() == "base");
            Assert.Contains(sources, source => source.GetProperty("span").GetProperty("revisionSha").GetString() == "target");
        });
    }

    [Fact]
    public async Task MeaningReviewFailure_RepairsThenReturnsOriginalWithReason()
    {
        var transport = new PlanningTransport { RejectReview = true };
        var (candidate, bundle) = Input(1, 100);
        var result = await Client(new() { Enabled = true, MaxInputCharacters = 16_000 }, transport)
            .PlanDiagramAsync(candidate, bundle, null, new("view", "flowchart", "balanced"), null, false, CancellationToken.None);
        Assert.Equal("Deterministic", result!.Status);
        Assert.Same(candidate, result.Diagram);
        Assert.Contains(result.Warnings, warning => warning.Contains("유지된 검증을 신규 변경으로 설명"));
        Assert.Equal(4, result.Attempts);
        Assert.Null(result.Explanation);
    }

    [Fact]
    public async Task InvalidEvidence_RejectsBeforeReviewAndNeverSavesASemanticExplanation()
    {
        var transport = new PlanningTransport { WrongFact = true };
        var (candidate, bundle) = Input(1, 100);
        var result = await Client(new() { Enabled = true, MaxInputCharacters = 16_000 }, transport)
            .PlanDiagramAsync(candidate, bundle, null, new("view", "flowchart", "balanced"), null, false, CancellationToken.None);
        Assert.Equal("Deterministic", result!.Status);
        Assert.Equal(2, transport.Requests.Count);
        Assert.All(transport.Requests, request => Assert.True(request.StructuredSchema!.Value.GetProperty("properties").TryGetProperty("elements", out _)));
        Assert.Null(result.Explanation);
    }

    private static InternalLlmClient Client(LlmOptions options, ILlmCompletionTransport transport) =>
        new(Options.Create(options), new SecretMasker(), new DiagramValidator(), transport, new StructuredLlmCompletion(transport));

    private static (DiagramIr, EvidenceBundle) Input(int count, int sourceSize)
    {
        var nodes = Enumerable.Range(0, count).Select(index => new DiagramNode($"node{index}", "원본 동작", "operation", $"method{index}",
            "modified", Confidence.Exact, [$"e{index}"], SourceFactIds: [$"fact{index}"])).ToArray();
        var facts = Enumerable.Range(0, count).SelectMany(index => new SourceFact[]
        {
            new($"fact{index}", "operation", "코드 동작", [$"change{index}"], [$"e{index}"], new("target", "blob", $"F{index}.cs", 1, 1)),
            new($"old{index}", "source", "before", [$"change{index}"], [$"old-e{index}"], new("base", "old", $"F{index}.cs", 1, 1), new string('a', sourceSize)),
            new($"new{index}", "source", "after", [$"change{index}"], [$"e{index}"], new("target", "blob", $"F{index}.cs", 1, 1), new string('b', sourceSize))
        }).ToArray();
        return (new DiagramIr("flowchart", "독립 구문", nodes, [], [], []), new EvidenceBundle("hash", "base", "target",
            Enumerable.Range(0, count).Select(index => $"change{index}").ToArray(), facts, []));
    }

    private sealed class PlanningTransport : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public bool RejectReview { get; init; }
        public bool WrongFact { get; init; }
        public List<VllmCompletionRequest> Requests { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string response;
            if (request.StructuredSchema!.Value.GetProperty("properties").TryGetProperty("accepted", out _))
                response = JsonSerializer.Serialize(new DiagramPlanReview(!RejectReview, RejectReview ? ["유지된 검증을 신규 변경으로 설명했습니다."] : []), Json);
            else
            {
                using var document = JsonDocument.Parse(request.UserPrompt);
                var input = document.RootElement.TryGetProperty("context", out var context) ? context : document.RootElement;
                var candidate = input.GetProperty("candidate").Deserialize<DiagramIr>(Json)!;
                var facts = input.GetProperty("facts").Deserialize<SourceFact[]>(Json)!;
                var ids = candidate.Nodes.SelectMany(node => node.SourceFactIds ?? []).ToHashSet();
                var changes = facts.Where(fact => ids.Contains(fact.Id)).SelectMany(fact => fact.ChangeIds).Distinct().Select(id =>
                    new PageChangeExplanation(id, "코드 비교 설명", WrongFact ? ["foreign-fact"] : facts.Where(fact => fact.ChangeIds.Contains(id)).Select(fact => fact.Id).ToArray(),
                        candidate.Nodes.Where(node => (node.SourceFactIds ?? []).Any(factId => facts.Any(fact => fact.Id == factId && fact.ChangeIds.Contains(id)))).Select(node => node.Id).ToArray(), []));
                response = JsonSerializer.Serialize(new DiagramPlan("페이지별 동작 설명", candidate.Nodes.Select(node => new SemanticElement(node.Id, "검토된 동작", [node.Id])).ToArray(),
                    [], [], changes.ToArray()), Json);
            }
            return Task.FromResult(new VllmCompletionResult(response, "stop", 0, true, false, 0, request.MaxOutputTokens, 0, 0, 0));
        }
    }
}
