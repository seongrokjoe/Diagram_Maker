using System.Net;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class SharedReviewRecoveryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string EvidenceId = "private-source-reference-123456789";
    private static readonly DiagramViewSelection[] Views = [new("flow", "flowchart", "balanced")];
    private static SharedSemanticItem[] Items(int count) => Enumerable.Range(0, count).Select(i =>
        new SharedSemanticItem(i.ToString("x64"), "operation", "work" + i, [EvidenceId], [], [], [])).ToArray();
    private static LlmOptions Config() => new() { Enabled = true, Endpoint = "http://127.0.0.1:19099/v1/chat/completions",
        AllowedOrigin = "http://127.0.0.1:19099", UseServerTokenization = false, MaxTransientRetries = 0 };

    [Fact]
    public void ItemAliasesBindTheSchemaAndCannotCollideWithShortSourceIds()
    {
        var targets = new HashSet<string> { "short", new string('a', 64) };
        var ids = new PromptIds(targets);
        var prompt = ids.Encode(JsonSerializer.Serialize(new { sources = new { id = "item1", factId = EvidenceId },
            items = targets.Select(id => new { id }), rejected = new { items = targets.Select(id => new { id }) } }));
        using var data = JsonDocument.Parse(prompt);
        var items = data.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToArray();
        Assert.All(items, id => { Assert.StartsWith("item", id); Assert.NotEqual("item1", id); });
        Assert.StartsWith("ref", data.RootElement.GetProperty("sources").GetProperty("factId").GetString());
        Assert.Equal(items, data.RootElement.GetProperty("rejected").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        var schemaIds = ids.BindSchema(SharedReviewValidation.Schema(2)).GetProperty("properties").GetProperty("items")
            .GetProperty("items").GetProperty("properties").GetProperty("id").GetProperty("enum").EnumerateArray().Select(i => i.GetString());
        Assert.Equal(items.Order(), schemaIds.Order());
        Assert.Contains(EvidenceId, ids.Restore(prompt));
        Assert.Null(ids.CheckResponseMembership(prompt, true));
        Assert.Equal("SharedReviewUnknownIds", ids.CheckResponseMembership("{\"items\":[{\"id\":\"short\",\"issues\":[]}]}", true)!.Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"accepted\":true,\"issues\":[]}")]
    [InlineData("{\"items\":[{\"id\":\"a\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"issues\":null}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"issues\":[null]}]}")]
    [InlineData("{\"items\":[{\"id\":\"a\",\"issues\":[],\"accepted\":true}]}")]
    [InlineData("{\"items\":[],\"items\":[]}")]
    public void MissingWrongTypedAndLegacyReviewFieldsAreRejected(string json) =>
        Assert.Equal("SharedReviewFieldsInvalid", SharedReviewValidation.CheckJson(json));

    [Theory]
    [InlineData("missing", "SharedReviewMissingIds")]
    [InlineData("unknown", "SharedReviewUnknownIds")]
    [InlineData("duplicate", "SharedReviewDuplicateIds")]
    [InlineData("code", "SharedReviewIssuesInvalid")]
    [InlineData("repeated-code", "SharedReviewIssuesInvalid")]
    [InlineData("too-many", "SharedReviewIssuesInvalid")]
    public void ReviewMembershipAndBoundedIssueCodesAreValidated(string mode, string expected)
    {
        var rows = mode switch
        {
            "missing" => Array.Empty<SharedItemReview>(),
            "unknown" => [new SharedItemReview("outside", [])],
            "duplicate" => [new SharedItemReview("a", []), new("a", [])],
            "code" => [new SharedItemReview("a", ["untrusted-source-text"])],
            "repeated-code" => [new SharedItemReview("a", ["mixed_scope", "mixed_scope"])],
            _ => [new SharedItemReview("a", SharedReviewValidation.Codes.Take(4).ToArray())]
        };
        var issue = SharedReviewValidation.Check(new(rows), new HashSet<string> { "a" });
        Assert.Equal(expected, issue!.Code);
        Assert.DoesNotContain("untrusted-source-text", JsonSerializer.Serialize(issue));
    }

    [Theory]
    [InlineData("invalid-review", 4, 2)]
    [InlineData("truncate-review", 4, 3)]
    [InlineData("truncate-review", 1, 2)]
    public async Task ReviewProtocolRecoveryNeverRegeneratesAnnotations(string mode, int count, int reviews)
    {
        using var handler = new Model(mode);
        var result = await Run(handler, count);
        Assert.Equal(count, result.Response.Items.Count);
        Assert.Equal(1, handler.Generations);
        Assert.Equal(reviews, handler.Reviews);
        Assert.All(result.Diagnostics.Where(d => d.ErrorCode is not null), d => Assert.Equal("Recovered", d.RecoveryState));
        Assert.All(result.Diagnostics, d => Assert.NotNull(d.RecoveryGroupId));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(40)]
    public async Task OnlySemanticallyRejectedItemsConsumeTheirSingleRepair(int count)
    {
        using var handler = new Model("reject-once");
        var result = await Run(handler, count);
        Assert.Equal(count, result.Response.Items.Count);
        Assert.Equal((count + 19) / 20 + 1, handler.Generations);
        Assert.Equal(2, handler.Counts["work0"]);
        Assert.All(handler.Counts.Where(kv => kv.Key != "work0"), kv => Assert.Equal(1, kv.Value));
        Assert.Contains(result.Diagnostics, d => d.ValidationCode == "SemanticReviewRejected" &&
            d.ValidationDetails!.IssueCodes!.Contains("reversed_condition") && d.RecoveryState == "Recovered");
    }

    [Fact]
    public async Task ReportedFourErrorsPreserveTheGoodItemsAndDoNotResetGenerationRepairs()
    {
        using var handler = new Model("four-errors");
        var result = await Run(handler, 8);
        Assert.Equal(7, result.Response.Items.Count);
        Assert.Equal(2, handler.Generations);
        Assert.All(handler.Counts.Values, count => Assert.Equal(2, count));
        Assert.Contains(result.Diagnostics, d => d.ValidationCode == "SharedUnknownIds" && d.ValidationDetails!.NonTargetItems == 1);
        Assert.Contains(result.Diagnostics, d => d.ErrorCode == "LLM_RESPONSE_TRUNCATED" && d.OutputLimit == 2000 && d.CompletionTokens == 2000);
        Assert.Contains(result.Diagnostics, d => d.ValidationCode == "SemanticReviewRejected" && d.RecoveryState == "Exhausted");
        Assert.Contains(result.Diagnostics, d => d.ValidationCode == "SharedReviewFieldsInvalid" && d.RecoveryState == "Recovered");
        Assert.DoesNotContain(EvidenceId, JsonSerializer.Serialize(result.Diagnostics));
    }

    [Fact]
    public async Task OutputBudgetSplitsBeforeReviewSendingAndHonorsConfiguredOutputLimits()
    {
        using var small = new Model("valid");
        var result = await Run(small, 40);
        Assert.Equal(40, result.Response.Items.Count); Assert.Equal(2, small.Generations);
        Assert.True(small.Reviews > 1);
        Assert.All(small.ReviewSizes, count => Assert.InRange(count, 1, 26));
        Assert.DoesNotContain(result.Diagnostics, d => d.ErrorCode is not null);
        using var large = new Model("valid");
        var options = Config(); options.ReviewOutputTokens = 10000;
        Assert.Equal(40, (await Run(large, 40, options)).Response.Items.Count);
        Assert.Equal(2, large.Reviews); Assert.All(large.ReviewLimits, limit => Assert.Equal(10000, limit));
    }

    [Fact]
    public async Task IndivisibleReviewOutputBudgetIsRecordedWithoutReviewHttp()
    {
        using var handler = new Model("valid");
        var options = Config(); options.ReviewOutputTokens = 100;
        var result = await Run(handler, 1, options);
        Assert.Empty(result.Response.Items); Assert.Equal(1, handler.Generations); Assert.Equal(0, handler.Reviews);
        Assert.Contains(result.Diagnostics, d => d.ErrorCode == "LLM_OUTPUT_BUDGET" && !d.Sent && d.RequiredOutputTokens > 100 && d.RecoveryState == "Exhausted");
    }

    [Fact]
    public async Task ThreeHundredThirtyTwoItemsAreAllGeneratedAndReviewedOnceWithinBudgets()
    {
        using var model = new Model("valid");
        var result = await Run(model, 332);
        Assert.Equal(332, result.Response.Items.Count);
        Assert.Equal(332, model.ReviewSizes.Sum());
        Assert.All(model.Counts, item => Assert.Equal(1, item.Value));
        Assert.All(result.Diagnostics, record => Assert.Null(record.ErrorCode));
        Assert.InRange(model.Generations + model.Reviews, 2, 28);
    }

    [Fact]
    public async Task RepeatedInvalidReviewsSplitWithoutResettingCorrectionOrRegenerating()
    {
        using var handler = new Model("always-invalid-review");
        var result = await Run(handler, 4);
        Assert.Empty(result.Response.Items);
        Assert.Equal(1, handler.Generations);
        Assert.Equal(8, handler.Reviews); // Parent twice, two children once, four singletons once.
        Assert.All(result.Diagnostics.Where(d => d.Purpose == "review"), d =>
        {
            Assert.InRange(d.Attempt!.Value, 1, 2); Assert.Equal("Exhausted", d.RecoveryState);
        });
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("reject-once")]
    public async Task InterruptionsDuringGenerationAndSplitReviewReuseCompletedHttpRequests(string mode)
    {
        using var handler = new Model(mode);
        var options = Config();
        using var transport = new VllmClient(options, handler: handler);
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        IReadOnlyList<SemanticCheckpoint>? checkpoints = null;
        IReadOnlyList<LlmDiagnostic>? diagnostics = null;
        SemanticProgress? progress = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var interrupted = new CancellationTokenSource();
            SemanticExecution? execution = null;
            execution = new(options, checkpoints, interrupted.Token, () =>
            {
                if (attempt < 2 && execution!.Checkpoints.Count(c => c.Stage.StartsWith("llm-") && c.State == "Completed") >= attempt + 1) interrupted.Cancel();
                return Task.CompletedTask;
            }, diagnostics, progress);
            using (execution)
            {
                var pending = client.GenerateSharedAsync("code-block", "resume", Items(40), _ => new { factId = EvidenceId }, Views, false, execution.Token);
                if (attempt < 2) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                else Assert.Equal(40, (await pending).Items.Count);
                checkpoints = JsonSerializer.Deserialize<SemanticCheckpoint[]>(JsonSerializer.Serialize(execution.Checkpoints));
                diagnostics = JsonSerializer.Deserialize<LlmDiagnostic[]>(JsonSerializer.Serialize(execution.Diagnostics));
                progress = execution.Progress;
            }
        }
        Assert.Equal(mode == "valid" ? 2 : 3, handler.Generations);
        Assert.Equal(handler.RequestKeys.Count, handler.RequestKeys.Distinct().Count());
        Assert.Equal(handler.Generations + handler.Reviews, progress!.TransportRequests);
        Assert.All(diagnostics!.Where(d => d.ErrorCode is not null), d => Assert.Equal("Recovered", d.RecoveryState));
    }

    [Fact]
    public async Task ValidIdentityAllowsOnlyBadTextFieldsToBeRegenerated()
    {
        using var handler = new Model("bad-text-once");
        var result = await Run(handler, 8);
        Assert.Equal(8, result.Response.Items.Count);
        Assert.Equal(2, handler.Counts["work0"]);
        Assert.All(handler.Counts.Where(kv => kv.Key != "work0"), kv => Assert.Equal(1, kv.Value));
        Assert.Contains(result.Diagnostics, d => d.ValidationCode == "SharedTextCodeSyntax" && d.RecoveryState == "Recovered");
    }

    [Fact]
    public async Task ExhaustedItemReviewRetainsApprovedItemsAndStructuredCorrection()
    {
        using var handler = new Model("reject-always");
        var result = await Run(handler, 3);

        Assert.Equal(2, result.Response.Items.Count);
        var failure = Assert.Single(result.Response.Failures!);
        Assert.Equal("semantic-review", failure.Stage);
        Assert.Equal("LLM_SEMANTIC_REVIEW", failure.Code);
        Assert.Equal(new[] { "reversed_condition" }, failure.IssueCodes);
        Assert.NotEmpty(failure.CorrectionInstructions!);
    }

    [Theory]
    [InlineData("http-generation", "generation", 1, 0)]
    [InlineData("http-review", "semantic-review", 1, 1)]
    public async Task ServerFailureKeepsItsPhaseAndStopsOtherBatches(string mode, string phase, int generations, int reviews)
    {
        using var handler = new Model(mode);
        var options = Config();
        using var transport = new VllmClient(options, handler: handler);
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        var result = await client.GenerateSharedAsync("code-block", "failure", Items(40), _ => new { factId = EvidenceId }, Views, false, execution.Token);
        Assert.Empty(result.Items);
        Assert.Equal(generations, handler.Generations); Assert.Equal(reviews, handler.Reviews);
        Assert.Contains(result.Failures!, f => f.Stage == phase && f.Code == "LLM_HTTP_400");
        Assert.Contains(execution.Diagnostics, d => d.HttpStatus == 400 && d.ServerErrorCategory == "unknown" && d.RecoveryState == "RequiresAction");
        Assert.Contains(execution.Checkpoints, c => c.State == "Pending");
        Assert.DoesNotContain("PRIVATE", LlmDiagnosticReport.Text("Partial", null, execution.Progress, execution.Diagnostics));
    }

    private static async Task<(SharedSemanticResponse Response, IReadOnlyList<LlmDiagnostic> Diagnostics)> Run(Model handler, int count, LlmOptions? options = null)
    {
        options ??= Config();
        using var transport = new VllmClient(options, handler: handler);
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        IReadOnlyList<SemanticCheckpoint>? checkpoints = null;
        IReadOnlyList<LlmDiagnostic>? diagnostics = null;
        SemanticProgress? progress = null;
        SharedSemanticResponse? response = null;
        var requests = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var execution = new SemanticExecution(options, checkpoints, CancellationToken.None, savedDiagnostics: diagnostics, savedProgress: progress);
            response = await client.GenerateSharedAsync("code-block", "regression", Items(count),
                _ => new { factId = EvidenceId, code = "synthetic source" }, Views, false, execution.Token);
            checkpoints = execution.Checkpoints; diagnostics = execution.Diagnostics; progress = execution.Progress;
            if (attempt == 0) requests = handler.Generations + handler.Reviews;
            else Assert.Equal(requests, handler.Generations + handler.Reviews);
        }
        return (response!, diagnostics!);
    }

    private sealed class Model(string mode) : HttpMessageHandler
    {
        public int Generations { get; private set; }
        public int Reviews { get; private set; }
        public Dictionary<string, int> Counts { get; } = [];
        public List<int> ReviewSizes { get; } = [];
        public List<int> ReviewLimits { get; } = [];
        public List<string> RequestKeys { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = await request.Content!.ReadAsStringAsync(ct); RequestKeys.Add(SemanticExecution.Hash(text));
            using var body = JsonDocument.Parse(text);
            var wire = body.RootElement;
            using var data = JsonDocument.Parse(wire.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var root = data.RootElement; var context = root.TryGetProperty("context", out var nested) ? nested : root;
            var items = context.GetProperty("items").EnumerateArray().ToArray();
            var schema = wire.GetProperty("structured_outputs").GetProperty("json");
            var fields = schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
            Assert.Equal(items.Select(i => i.GetProperty("id").GetString()).Order(),
                fields.GetProperty("id").GetProperty("enum").EnumerateArray().Select(i => i.GetString()).Order());
            var reviewing = fields.TryGetProperty("issues", out _);
            object result; var reason = "stop";
            if (reviewing)
            {
                Reviews++; ReviewSizes.Add(items.Length); ReviewLimits.Add(wire.GetProperty("max_tokens").GetInt32());
                if (mode == "http-review") return new(HttpStatusCode.BadRequest) { Content = new StringContent("PRIVATE response") };
                if ((mode == "truncate-review" || mode == "four-errors") && Reviews == 1) reason = "length";
                result = new SharedSemanticReview(items.Select(i => new SharedItemReview(i.GetProperty("id").GetString()!,
                    ((mode == "reject-once" && Counts[i.GetProperty("label").GetString()!] == 1) || mode == "reject-always" || mode == "four-errors") &&
                    i.GetProperty("label").GetString() == "work0" ? ["reversed_condition"] : [])).ToArray());
                if (mode == "invalid-review" && Reviews == 1 || mode == "four-errors" && Reviews == 3 || mode == "always-invalid-review")
                    result = new { accepted = true, issues = new[] { "contradiction" } };
            }
            else
            {
                Generations++;
                if (mode == "http-generation") return new(HttpStatusCode.BadRequest) { Content = new StringContent("PRIVATE response") };
                result = new SharedSemanticResponse("원본 코드의 동작", "flowchart", items.Select((i, index) =>
                {
                    var label = i.GetProperty("label").GetString()!; Counts[label] = Counts.GetValueOrDefault(label) + 1;
                    return new SharedSemanticAnnotation(mode == "four-errors" && Generations == 1 && index == 0
                        ? context.GetProperty("sources").GetProperty("factId").GetString()! : i.GetProperty("id").GetString()!,
                        mode == "bad-text-once" && label == "work0" && Counts[label] == 1 ? "값 <script>처리</script>" : "값을 처리합니다", "원본 근거의 조건과 결과를 보존합니다");
                }).ToArray());
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = JsonSerializer.Serialize(result, Json) }, finish_reason = reason } },
                usage = new { prompt_tokens = 100, completion_tokens = reason == "length" ? wire.GetProperty("max_tokens").GetInt32() : 100, total_tokens = 200 }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
