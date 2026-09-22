using System.Net;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class VllmClientTests
{
    [Fact]
    public async Task SendsVllmContractWithRequestedThinkingAndNoCredentials()
    {
        var handler = new QueueHandler(Response("OK"));
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");
        using var client = CreateClient(handler);

        var result = await client.CompleteAsync(new VllmCompletionRequest(
            "system", "user", 123, EnableThinking: true, schemaDocument.RootElement.Clone()), CancellationToken.None);

        Assert.Equal("OK", result.Content);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal("approved-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal(123, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("object", payload.RootElement.GetProperty("structured_outputs").GetProperty("json").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NegotiatesStructuredModesOnlyWhenServerExplicitlyRejectsThem()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("structured_outputs unsupported") },
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("response_format unsupported") },
            Response("fallback"));
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");
        using var client = CreateClient(handler);

        var result = await client.CompleteAsync(new VllmCompletionRequest(
            "system", "user", 100, EnableThinking: true, schemaDocument.RootElement.Clone()), CancellationToken.None);

        Assert.True(result.StructuredOutputFallbackUsed);
        Assert.False(result.StructuredOutputApplied);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("structured_outputs", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("structured_outputs", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("response_format", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("response_format", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Contains("\"enable_thinking\":true", request.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetriesOneTransientHttpFailure()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Response("OK"));
        var options = Options();
        options.MaxTransientRetries = 1;
        using var client = new VllmClient(options, handler: handler);

        var result = await client.CompleteAsync(new VllmCompletionRequest(
            "system", "user", 100, EnableThinking: false), CancellationToken.None);

        Assert.Equal("OK", result.Content);
        Assert.Equal(1, result.RetryCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("ordinary validation failed", "LLM_HTTP_422")]
    [InlineData("maximum context length exceeded", "LLM_CONTEXT_LIMIT")]
    public async Task DoesNotDowngradeStructuredOutputForUnrelatedServerErrors(string body, string code)
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent(body) });
        using var client = CreateClient(handler);
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.CompleteAsync(new("system", "user", 100, false, schema.RootElement), CancellationToken.None));
        Assert.Equal(code, error.Code); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ServerTokenCountAllowsInputThatConservativeEstimateWouldReject()
    {
        var options = Options(); options.UseServerTokenization = true; options.MaxInputTokens = 25;
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"count\":20}") }, Response("OK"));
        using var client = new VllmClient(options, handler: handler);
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        var result = await client.CompleteAsync(new("system", new string('a', 500), 100, false), CancellationToken.None);
        Assert.Equal("OK", result.Content); Assert.Equal(2, handler.Requests.Count);
        var diagnostic = Assert.Single(execution.Diagnostics);
        Assert.Equal(20, diagnostic.InputTokens); Assert.False(diagnostic.EstimatedInputTokens); Assert.True(diagnostic.Sent);
    }

    [Fact]
    public async Task SupportedAlternativeOutputModeIsRememberedForNextRequest()
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("structured_outputs unsupported") },
            Response("OK"), Response("OK"));
        using var client = CreateClient(handler);
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        await client.CompleteAsync(new("system", "user", 100, false, schema.RootElement), CancellationToken.None);
        await client.CompleteAsync(new("system", "user", 100, false, schema.RootElement), CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("response_format", handler.Requests[2].Body); Assert.DoesNotContain("structured_outputs", handler.Requests[2].Body);
    }

    [Fact]
    public void RejectsEndpointOutsideExactOrigin()
    {
        var options = Options();
        options.AllowedOrigin = "https://llm.invalid:8443";

        var error = Assert.Throws<InvalidOperationException>(() => new VllmClient(options, handler: new QueueHandler()));

        Assert.Contains("outside", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsLengthFinishReason()
    {
        var handler = new QueueHandler(Response("{\"result\":\"partial\"}", "length"));
        using var client = CreateClient(handler);
        var structured = new StructuredLlmCompletion(client);
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");

        var error = await Assert.ThrowsAsync<LlmClientException>(() => structured.CompleteAsync<TestResult>(
            "system", "user", schemaDocument.RootElement.Clone(), 100, false,
            value => string.IsNullOrWhiteSpace(value.Result) ? "MissingResult" : null, CancellationToken.None));

        Assert.Equal("LLM_RESPONSE_TRUNCATED", error.Code);
        Assert.Equal("Truncated", error.FailureKind);
        Assert.False(error.RepairAttempted);
        Assert.Equal(100, error.RequestedMaxOutputTokens);
        Assert.Equal(10, error.PromptTokens);
        Assert.Equal(20, error.CompletionTokens);
        Assert.Equal(30, error.TotalTokens);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RepairsInvalidStructuredResponseWithRejectedContentAsUntrustedData()
    {
        const string rejected = "SENSITIVE_REJECTED_RAW_RESPONSE";
        var handler = new QueueHandler(Response(rejected), Response("{\"result\":\"valid\"}"));
        using var client = CreateClient(handler);
        var structured = new StructuredLlmCompletion(client);
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");

        var result = await structured.CompleteAsync<TestResult>(
            "system", "synthetic user", schemaDocument.RootElement.Clone(), 100, false,
            value => value.Result == "valid" ? null : "MissingResult", CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal("valid", result.Value.Result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(rejected, handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("MalformedJson", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsBoundedFailureKindsWithoutRejectedContent()
    {
        const string firstRejected = "prefix {\"result\":\"secret-one\"}";
        const string secondRejected = "secret-two";
        var handler = new QueueHandler(new[] { Response(firstRejected) }.Concat(Enumerable.Range(0, 10).Select(_ => Response($"{{\"result\":\"{secondRejected}\"}}"))).ToArray());
        using var client = CreateClient(handler);
        var structured = new StructuredLlmCompletion(client);
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");

        var error = await Assert.ThrowsAsync<LlmClientException>(() => structured.CompleteAsync<TestResult>(
            "system", "synthetic user", schemaDocument.RootElement.Clone(), 100, false,
            _ => "SemanticValidation", CancellationToken.None));

        Assert.Equal("LLM_SCHEMA_INVALID", error.Code);
        Assert.Equal("MixedContent", error.InitialFailureKind);
        Assert.Equal("SemanticValidation", error.FailureKind);
        Assert.True(error.RepairAttempted);
        Assert.Equal(100, error.RequestedMaxOutputTokens);
        Assert.Equal(10, error.PromptTokens);
        Assert.Equal(20, error.CompletionTokens);
        Assert.Equal(30, error.TotalTokens);
        Assert.DoesNotContain("secret-one", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secondRejected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptsSingleJsonCodeFence()
    {
        var handler = new QueueHandler(Response("""
            ```json
            {"result":"valid"}
            ```
            """));
        using var client = CreateClient(handler);
        var structured = new StructuredLlmCompletion(client);
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");

        var result = await structured.CompleteAsync<TestResult>(
            "system", "synthetic user", schemaDocument.RootElement.Clone(), 100, false,
            value => value.Result == "valid" ? null : "MissingResult", CancellationToken.None);

        Assert.False(result.RepairUsed);
        Assert.Equal("valid", result.Value.Result);
    }

    [Fact]
    public async Task DiagramContractSendsStrictBoundedSchema()
    {
        var handler = new QueueHandler(Response(ValidDiagramContent));
        var options = Options();
        using var transport = new VllmClient(options, handler: handler);
        var validator = new DiagramValidator();
        var structured = new StructuredLlmCompletion(transport);
        var llm = new InternalLlmClient(
            Microsoft.Extensions.Options.Options.Create(options),
            new SecretMasker(),
            validator,
            transport,
            structured);

        var result = await llm.TestDiagramContractAsync(CancellationToken.None);

        Assert.True(result.Success);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var schema = payload.RootElement.GetProperty("structured_outputs").GetProperty("json");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var nodeSchema = schema.GetProperty("properties").GetProperty("nodes");
        Assert.Equal(1, nodeSchema.GetProperty("minItems").GetInt32());
        Assert.Equal(500, nodeSchema.GetProperty("maxItems").GetInt32());
        var nodeItem = nodeSchema.GetProperty("items");
        Assert.False(nodeItem.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains(nodeItem.GetProperty("required").EnumerateArray(), item => item.GetString() == "group");
        Assert.Equal(0, nodeItem.GetProperty("properties").GetProperty("evidenceIds").GetProperty("maxItems").GetInt32());
        Assert.False(result.ThinkingEnabled);
        Assert.Equal(1_000, result.RequestedMaxOutputTokens);
        Assert.Equal(20, result.CompletionTokens);
    }

    [Fact]
    public async Task ThinkingContractUsesConfiguredThinkingBudget()
    {
        var handler = new QueueHandler(Response("{\"result\":\"ok\"}"));
        var options = Options();
        options.ThinkingOutputTokens = 1_800;
        using var transport = new VllmClient(options, handler: handler);
        var llm = CreateInternalClient(options, transport);

        var result = await llm.TestThinkingContractAsync(CancellationToken.None);

        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.True(payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(1_800, payload.RootElement.GetProperty("max_tokens").GetInt32());
        var schema = payload.RootElement.GetProperty("structured_outputs").GetProperty("json");
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("result", out var resultProperty));
        Assert.False(properties.TryGetProperty("nodes", out _));
        Assert.Equal("ok", Assert.Single(resultProperty.GetProperty("enum").EnumerateArray()).GetString());
        Assert.True(result.ThinkingEnabled);
        Assert.Equal(1_800, result.RequestedMaxOutputTokens);
    }

    [Fact]
    public async Task ThinkingBudgetFallsBackToOutputHardLimit()
    {
        var handler = new QueueHandler(Response("{\"result\":\"ok\"}"));
        var options = Options();
        Assert.Null(options.ThinkingOutputTokens);
        using var transport = new VllmClient(options, handler: handler);
        var llm = CreateInternalClient(options, transport);

        await llm.TestThinkingContractAsync(CancellationToken.None);

        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(options.OutputHardLimit, payload.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task NaturalDiagramThinkingUsesThinkingBudget()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var handler = new QueueHandler(Response(JsonSerializer.Serialize(NaturalExtraction.FromRequirements(NaturalDesignTests.Requirements()), jsonOptions)),
            Response(JsonSerializer.Serialize(new NaturalSourceReview(
                NaturalRequirementEvidence.Prepare(NaturalDesignTests.Prompt).Select(r => r.Id).ToArray(), []), jsonOptions)),
            Response(JsonSerializer.Serialize(NaturalDesignTests.Meaning("flowchart"), jsonOptions)),
            Response("{\"accepted\":true,\"reviewedRequirementIds\":[\"r1\"],\"issues\":[],\"itemIssues\":[]}"));
        var options = Options();
        options.ThinkingOutputTokens = 1_750;
        using var transport = new VllmClient(options, handler: handler);
        var llm = CreateInternalClient(options, transport);

        var diagram = await llm.GenerateNaturalDiagramAsync(
            NaturalDesignTests.Prompt, "flowchart", enableThinking: true,
            new DiagramPresetCatalog().Resolve("flowchart", "balanced"), null, CancellationToken.None);

        Assert.NotNull(diagram);
        using var payload = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal(1_750, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, payload.RootElement.GetProperty("temperature").GetDouble());
        Assert.True(payload.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        var properties = payload.RootElement.GetProperty("structured_outputs").GetProperty("json").GetProperty("properties");
        Assert.True(properties.TryGetProperty("concepts", out _));
        Assert.True(properties.TryGetProperty("connections", out _));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task NaturalDiagram_InvalidRequirementsStopAfterTenContractRepairs()
    {
        var handler = new QueueHandler(Enumerable.Range(0, 11).Select(_ => Response(ValidDiagramContent)).ToArray());
        var options = Options();
        using var transport = new VllmClient(options, handler: handler);
        var llm = CreateInternalClient(options, transport);

        var error = await Assert.ThrowsAsync<LlmClientException>(() => llm.GenerateNaturalDiagramAsync(
            "synthetic request", "flowchart", enableThinking: false,
            new DiagramPresetCatalog().Resolve("flowchart", "balanced"), null, CancellationToken.None));

        Assert.Equal("NATURAL_REQUIREMENTS_INVALID", error.Code);
        Assert.Equal(11, handler.Requests.Count);
    }

    [Fact]
    public void RejectsThinkingBudgetAboveHardLimit()
    {
        var options = Options();
        options.ThinkingOutputTokens = options.OutputHardLimit + 1;

        Assert.Throws<InvalidOperationException>(() => new VllmClient(options, handler: new QueueHandler()));
    }

    private static VllmClient CreateClient(HttpMessageHandler handler) => new(Options(), handler: handler);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SemanticPlanning_CarriesBothRevisionsAndOptions_AndReviewsBeforeAccepting(bool repair)
    {
        const string plan = """{"summary":"헤더 변경","elements":[{"id":"operation","summary":"Header 포인터 할당","nodeIds":["operation"]}],"messages":[],"changes":[{"changeId":"change","summary":"포인터 형 변환을 명시했습니다.","factIds":["before","after"],"nodeIds":["operation"],"edgeIds":[]}],"instructionResults":["포인터 할당 의미를 강조했습니다."]}""";
        const string accepted = """{"accepted":true,"issues":[]}""";
        var handler = repair ? new QueueHandler(Response(plan), Response("""{"accepted":false,"issues":["이전 버전 차이를 설명하세요."]}"""),
            Response(plan.Replace("포인터 형 변환을 명시했습니다.", "이전 암시적 포인터 형 변환을 명시적 형 변환으로 바꿨습니다.")), Response(accepted)) :
            new QueueHandler(Response(plan), Response(accepted));
        var options = Options();
        using var transport = new VllmClient(options, handler: handler);
        var llm = CreateInternalClient(options, transport);
        var candidate = new DiagramIr("flowchart", "test", [new DiagramNode("operation", "처리", "operation", null,
            "modified", Confidence.Exact, [], SourceFactIds: ["fact"])], [], [], []);
        var bundle = PlanningBundle();
        var result = await llm.PlanDiagramAsync(candidate, bundle, null,
            new DiagramViewSelection("view", "flowchart", "balanced", Overrides: new DiagramStyleOverrides(Direction: "TB"),
                RefinementInstruction: "포인터 할당 의미를 강조"), candidate, false, CancellationToken.None);
        Assert.Equal("Semantic", result!.Status);
        Assert.Equal(repair ? 4 : 2, handler.Requests.Count);
        Assert.Equal(handler.Requests.Count, result.Attempts);
        using var request = JsonDocument.Parse(handler.Requests[0].Body);
        using var input = JsonDocument.Parse(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal("bundle-hash", input.RootElement.GetProperty("hash").GetString());
        Assert.Equal("TB", input.RootElement.GetProperty("selection").GetProperty("overrides").GetProperty("direction").GetString());
        var facts = input.RootElement.GetProperty("facts").EnumerateArray().ToArray();
        Assert.Contains(facts, fact => fact.GetProperty("id").GetString() == "before");
        Assert.Contains(facts, fact => fact.GetProperty("id").GetString() == "after");
        Assert.Equal("Header 포인터 할당", result.Diagram.Nodes[0].Label);
        Assert.Equal("헤더 변경", result.Explanation!.Summary);
        Assert.Equal(new[] { "before", "after" }, Assert.Single(result.Explanation.Changes).FactIds);
    }

    [Fact]
    public async Task SemanticPlanning_OversizedOrUnresolvedEvidenceNeverProducesSuccessOrMalformedJson()
    {
        var handler = new QueueHandler();
        var options = Options();
        using var transport = new VllmClient(options, handler: handler);
        var llm = CreateInternalClient(options, transport);
        var candidate = new DiagramIr("flowchart", "test", [new DiagramNode("operation", "처리", "operation", null,
            "modified", Confidence.Exact, [], SourceFactIds: ["fact"])], [], [], []);
        var bundle = PlanningBundle();
        var huge = bundle with { Facts = bundle.Facts.Select(fact => fact.Id == "after" ? fact with { Content = new string('x', 20_000) } : fact).ToArray() };
        var selection = new DiagramViewSelection("view", "flowchart", "balanced");
        var result = await llm.PlanDiagramAsync(candidate, huge, null, selection, null, false, CancellationToken.None);
        Assert.Equal("Deterministic", result!.Status);
        Assert.Empty(handler.Requests);
        var missing = await llm.PlanDiagramAsync(candidate, bundle with { Facts = [] }, null, selection, null, false, CancellationToken.None);
        Assert.Equal("Deterministic", missing!.Status);
        Assert.Empty(handler.Requests);
    }

    private static EvidenceBundle PlanningBundle() => new("bundle-hash", "base", "target", ["change"],
        [new("fact", "operation", "할당", ["change"], [], new("target", "new", "S.cpp", 1, 1)),
         new("before", "source", "Run", ["change"], [], new("base", "old", "S.cpp", 1, 1), "auto p = pHeaderBuf;"),
         new("after", "source", "Run", ["change"], [], new("target", "new", "S.cpp", 1, 1), "T_HEADER_COMM *pHeader = (T_HEADER_COMM *) pHeaderBuf;")], []);

    private static InternalLlmClient CreateInternalClient(LlmOptions options, VllmClient transport)
    {
        var validator = new DiagramValidator();
        return new InternalLlmClient(
            Microsoft.Extensions.Options.Options.Create(options),
            new SecretMasker(),
            validator,
            transport,
            new StructuredLlmCompletion(transport));
    }

    private static LlmOptions Options() => new()
    {
        Enabled = true,
        Endpoint = "https://llm.invalid/v1/chat/completions",
        AllowedOrigin = "https://llm.invalid",
        Model = "approved-model",
        ConnectTimeoutSeconds = 1,
        NoResponseTimeoutSeconds = 1,
        RequestTimeoutSeconds = 5,
        DiagramOutputTokens = 1_000,
        ReviewOutputTokens = 1_000,
        OutputHardLimit = 2_000,
        MaxInputCharacters = 10_000,
        MaxTransientRetries = 0
    };

    private static HttpResponseMessage Response(string content, string finishReason = "stop")
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content, reasoning_content = "must-not-be-read" },
                    finish_reason = finishReason
                }
            },
            usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record TestResult(string Result);

    private const string ValidDiagramContent = """
        {
          "type":"flowchart",
          "title":"Synthetic Diagram",
          "nodes":[
            {"id":"n1","label":"Client","kind":"component","group":null,"status":"unchanged","confidence":"Inferred","evidenceIds":[]},
            {"id":"n2","label":"Service","kind":"component","group":null,"status":"unchanged","confidence":"Inferred","evidenceIds":[]}
          ],
          "edges":[
            {"id":"e1","sourceId":"n1","targetId":"n2","type":"flow","label":"request","status":"unchanged","confidence":"Inferred","evidenceIds":[],"sequenceIndex":1}
          ],
          "notes":[],
          "provenance":[]
        }
        """;

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.Headers.Authorization?.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_responses.Count == 0) throw new InvalidOperationException("No fake response is available.");
            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(string? Authorization, string Body);
}
