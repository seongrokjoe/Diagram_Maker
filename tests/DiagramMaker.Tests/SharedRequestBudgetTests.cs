using System.Net;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class SharedRequestBudgetTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DiagramViewSelection[] Views = [new("flow", "flowchart", "balanced")];
    private static SharedSemanticItem[] Items(int count) => Enumerable.Range(0, count).Select(i => new SharedSemanticItem(
        i.ToString("x64"), "operation", "작업 " + i, [], [], [], [])).ToArray();
    private static LlmOptions OptionsForTest() => new() { Enabled = true, Endpoint = "http://127.0.0.1:19099/v1/chat/completions",
        AllowedOrigin = "http://127.0.0.1:19099", UseServerTokenization = false, MaxTransientRetries = 0, MaxInputCharacters = 60000 };

    [Theory]
    [InlineData("summary", "SharedSummaryInvalid")]
    [InlineData("type", "SharedRecommendedTypeInvalid")]
    [InlineData("items", "SharedItemsInvalid")]
    [InlineData("missing", "SharedMissingIds")]
    [InlineData("duplicate", "SharedDuplicateIds")]
    [InlineData("unknown", "SharedUnknownIds")]
    [InlineData("empty", "SharedTextEmpty")]
    [InlineData("length", "SharedTextTooLong")]
    [InlineData("language", "SharedTextNotKorean")]
    [InlineData("syntax", "SharedTextCodeSyntax")]
    public void ValidationExplainsTheSpecificContractFailureWithoutSource(string mode, string code)
    {
        var annotation = new SharedSemanticAnnotation("a", "값 처리", "원본 값을 처리합니다");
        var response = new SharedSemanticResponse("작업 요약", "flowchart", [annotation, annotation with { Id = "b" }]);
        response = mode switch
        {
            "summary" => response with { Summary = "" }, "type" => response with { RecommendedType = "Flow" },
            "items" => response with { Items = null! }, "missing" => response with { Items = [annotation] },
            "duplicate" => response with { Items = [annotation, annotation] },
            "unknown" => response with { Items = [annotation, annotation with { Id = "secret-source-id" }] },
            _ => response with { Items = [annotation with { Summary = mode switch
                { "empty" => "", "length" => new string('가', 81), "language" => "Handle input", _ => "값 != 결과" } }, response.Items[1]] }
        };
        var problem = SharedSemanticValidation.Check(response, new HashSet<string> { "a", "b" }, ["flowchart"]);
        Assert.Equal(code, problem!.Code);
        Assert.DoesNotContain("secret-source-id", JsonSerializer.Serialize(problem));
        Assert.Equal(2, problem.Details.ExpectedItems);
    }

    [Fact]
    public async Task ReviewCharacterOverflowSplitsOnlyReviewsAndResumesTwiceWithoutRegeneration()
    {
        var options = OptionsForTest();
        options.MaxInputCharacters = 40000;
        options.ReviewOutputTokens = 10000; // Exercise the independent character boundary, not proactive output splitting.
        using var handler = new BudgetHandler();
        using var transport = new VllmClient(options, handler: handler);
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        IReadOnlyList<SemanticCheckpoint>? checkpoints = null;
        IReadOnlyList<LlmDiagnostic>? diagnostics = null;
        SemanticProgress? progress = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var interruption = new CancellationTokenSource();
            SemanticExecution? execution = null;
            execution = new SemanticExecution(options, checkpoints, interruption.Token, () =>
            {
                if (attempt < 2 && execution!.Checkpoints.Count(c => c.Stage.StartsWith("llm-") && c.State == "Completed") >= attempt + 1)
                    interruption.Cancel();
                return Task.CompletedTask;
            }, diagnostics, progress);
            using (execution)
            {
                var operation = client.GenerateSharedAsync("code-block", "경계 검사", Items(12),
                    _ => new { code = new string('x', 28000) }, Views, false, execution.Token);
                if (attempt < 2) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                else Assert.Equal(12, (await operation).Items.Count);
                checkpoints = execution.Checkpoints; diagnostics = execution.Diagnostics; progress = execution.Progress;
            }
        }
        Assert.Equal(1, handler.GenerationRequests);
        Assert.True(handler.ReviewRequests >= 2);
        Assert.Contains(diagnostics!, d => d.ErrorCode == "LLM_INPUT_CHARACTERS" && d.Purpose == "review" && !d.Sent && d.InputCharacters > 36500);
        Assert.Contains(checkpoints!, c => c.WasSplit);
        Assert.Equal(handler.GenerationRequests + handler.ReviewRequests, progress!.TransportRequests);
        Assert.True(progress.ReusedUnits > 0);
    }

    [Fact]
    public async Task InvalidResponseThenOversizedRepairUsesJsonPartitionsAndPreservesFailureChain()
    {
        var options = OptionsForTest();
        options.MaxInputCharacters = 40000;
        using var handler = new BudgetHandler { RejectFirst = true };
        using var transport = new VllmClient(options, handler: handler);
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        IReadOnlyList<SemanticCheckpoint>? saved = null;
        var calls = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var execution = new SemanticExecution(options, saved, CancellationToken.None);
            var response = await client.GenerateSharedAsync("code-block", "경계 검사", Items(12),
                _ => new { code = new string('x', 28000) }, Views, false, execution.Token);
            Assert.Equal(12, response.Items.Count);
            saved = execution.Checkpoints;
            if (attempt == 0)
            {
                calls = handler.GenerationRequests + handler.ReviewRequests;
                Assert.Contains(execution.Diagnostics, d => d.ValidationCode == "SharedTextNotKorean" && d.ValidationDetails?.Field == "items.summary" && d.Sent);
                Assert.Contains(execution.Diagnostics, d => d.ErrorCode == "LLM_INPUT_CHARACTERS" && d.Purpose == "repair" && !d.Sent);
                Assert.Contains(execution.Progress.RecentFailures!, d => d.ValidationCode == "SharedTextNotKorean");
                Assert.Contains(execution.Progress.RecentFailures!, d => d.ErrorCode == "LLM_INPUT_CHARACTERS");
            }
            else Assert.Equal(calls, handler.GenerationRequests + handler.ReviewRequests);
        }
        Assert.True(handler.RepairRequests >= 2);
        Assert.All(handler.GenerationCounts.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task IndivisibleCharacterOverflowIsRecordedWithoutHttpAndDoesNotRepeatOnResume()
    {
        var options = OptionsForTest();
        using var handler = new BudgetHandler();
        using var transport = new VllmClient(options, handler: handler);
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        IReadOnlyList<SemanticCheckpoint>? saved = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var execution = new SemanticExecution(options, saved, CancellationToken.None);
            var response = await client.GenerateSharedAsync("code-block", "단일 근거", Items(1),
                _ => new { code = new string('x', 60000) }, Views, false, execution.Token);
            Assert.Empty(response.Items); saved = execution.Checkpoints;
            if (attempt == 0)
            {
                var error = Assert.Single(execution.Diagnostics);
                Assert.Equal("LLM_INPUT_CHARACTERS", error.ErrorCode); Assert.False(error.Sent);
                Assert.Equal(56500, error.InputCharacterLimit); Assert.Equal(0, execution.Progress.TransportRequests);
            }
            else Assert.Empty(execution.Diagnostics);
        }
        Assert.Equal(0, handler.GenerationRequests + handler.ReviewRequests);
    }

    private sealed class BudgetHandler : HttpMessageHandler
    {
        public bool RejectFirst { get; init; }
        public int GenerationRequests { get; private set; }
        public int ReviewRequests { get; private set; }
        public int RepairRequests { get; private set; }
        public Dictionary<string, int> GenerationCounts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var prompt = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.InRange(prompt.Length, 1, 56500);
            using var data = JsonDocument.Parse(prompt);
            var root = data.RootElement;
            object response;
            if (body.RootElement.GetProperty("structured_outputs").GetProperty("json").GetProperty("properties")
                .GetProperty("items").GetProperty("items").GetProperty("properties").TryGetProperty("issues", out _))
            { ReviewRequests++; response = new SharedSemanticReview(root.GetProperty("context").GetProperty("items").EnumerateArray()
                .Select(i => new SharedItemReview(i.GetProperty("id").GetString()!, [])).ToArray()); }
            else
            {
                GenerationRequests++;
                var repair = root.TryGetProperty("rejected", out var rejected);
                if (repair)
                {
                    RepairRequests++;
                    Assert.Equal(JsonValueKind.Object, rejected.ValueKind);
                    Assert.All(rejected.GetProperty("items").EnumerateArray(), i => Assert.StartsWith("item", i.GetProperty("id").GetString()));
                }
                if (root.TryGetProperty("context", out var context)) root = context;
                var annotations = root.GetProperty("items").EnumerateArray().Select(item =>
                {
                    var label = item.GetProperty("label").GetString()!;
                    GenerationCounts[label] = GenerationCounts.GetValueOrDefault(label) + 1;
                    return new SharedSemanticAnnotation(item.GetProperty("id").GetString()!,
                        RejectFirst && GenerationRequests == 1 ? new string('x', 80) : repair ? "값 처리" : "값" + new string('x', 79),
                        repair ? "값을 처리합니다" : "값" + new string('x', 499));
                }).ToArray();
                response = new SharedSemanticResponse("원본 근거의 동작", "flowchart", annotations);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = JsonSerializer.Serialize(response, Json) }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 100, completion_tokens = 100, total_tokens = 200 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
