using System.Net;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class LlmCompatibilityTests
{
    private const string Xgrammar = "The provided JSON schema contains features not supported by xgrammar.";
    private static LlmOptions Options() => new() { Enabled = true, Endpoint = "http://127.0.0.1:19099/v1/chat/completions",
        AllowedOrigin = "http://127.0.0.1:19099", UseServerTokenization = false, MaxTransientRetries = 0 };
    private static JsonElement Schema(string name, int limit = 10) => JsonSerializer.SerializeToElement(new {
        type = "object", additionalProperties = false, required = new[] { name },
        properties = new Dictionary<string, object> { [name] = new { type = "string", maxLength = limit, @enum = new[] { "ok" } } }
    });
    private static HttpResponseMessage Failure(string text, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { message = text } })) };
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent(
        """{"choices":[{"message":{"content":"{\"result\":\"ok\"}"},"finish_reason":"stop"}]}""") };

    [Fact]
    public async Task XgrammarErrorRetriesOneCompatibleSchemaAndRetainsRequiredIdsAndEnums()
    {
        var calls = 0;
        using var handler = new Handler(body => {
            calls++;
            var schema = body.GetProperty("structured_outputs").GetProperty("json");
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal("result", schema.GetProperty("required")[0].GetString());
            var field = schema.GetProperty("properties").GetProperty("result");
            Assert.Equal("ok", field.GetProperty("enum")[0].GetString());
            Assert.Equal(calls == 1, field.TryGetProperty("maxLength", out _));
            return calls == 1 ? Failure(Xgrammar) : Success();
        });
        var options = Options();
        using var client = new VllmClient(options, handler: handler);
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        await client.CompleteAsync(new("system", "user", 100, false, Schema("result"), AllowSchemaRelaxation: true), CancellationToken.None);
        Assert.Equal(2, calls);
        var diagnostic = Assert.Single(execution.Diagnostics);
        Assert.True(diagnostic.SchemaRelaxed); Assert.Equal("structured_outputs", diagnostic.OutputMode);
        Assert.Equal(2, diagnostic.TransportAttempts);
        Assert.DoesNotContain("uniqueItems", SharedReviewValidation.Schema(2).GetRawText());
        Assert.Equal("SharedReviewIssuesInvalid", SharedReviewValidation.Check(new([new("a", ["mixed_scope", "mixed_scope"])]), new HashSet<string> { "a" })!.Code);
    }

    [Fact]
    public async Task RejectedCompatibilityDoesNotChangeAnotherRequestSchemaOrRetryForever()
    {
        var calls = 0;
        using var handler = new Handler(body => {
            calls++;
            if (body.GetProperty("structured_outputs").GetProperty("json").GetProperty("properties").TryGetProperty("other", out _)) return Success();
            return Failure(Xgrammar);
        });
        using var client = new VllmClient(Options(), handler: handler);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.CompleteAsync(new("system", "user", 100, false,
            Schema("result"), AllowSchemaRelaxation: true), CancellationToken.None));
        Assert.Equal("schema-constraint", error.ServerErrorCategory); Assert.Equal(2, calls);
        await client.CompleteAsync(new("system", "user", 100, false, Schema("other")), CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ConcurrentSchemasNegotiateIndependently()
    {
        using var handler = new Handler(body => {
            if (body.TryGetProperty("structured_outputs", out var output) && output.GetProperty("json").GetProperty("properties").TryGetProperty("result", out _))
                return Failure("structured_outputs unsupported");
            if (body.TryGetProperty("response_format", out var alternative))
                Assert.True(alternative.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").TryGetProperty("result", out _));
            return Success();
        });
        using var client = new VllmClient(Options(), handler: handler);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => client.CompleteAsync(
            new("system", "user", 100, false, Schema(i % 2 == 0 ? "result" : "other")), CancellationToken.None)));
        for (var i = 0; i < responses.Length; i++) Assert.Equal(i % 2 == 0 ? "response_format" : "structured_outputs", responses[i].OutputMode);
    }

    [Fact]
    public async Task FinalFallbackBudgetIsCheckedBeforeSendingAndModeDiagnosticIsAccurate()
    {
        var calls = 0;
        using var handler = new Handler(_ => ++calls == 1 ? Failure("structured_outputs unsupported") : Failure("response_format unsupported"));
        var options = Options();
        using var client = new VllmClient(options, handler: handler);
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        var schema = Schema("result");
        var limit = schema.GetRawText().Length + "systemuser".Length + 1;
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.CompleteAsync(new("system", "user", 100, false,
            schema, InputCharacterLimit: limit), CancellationToken.None));
        Assert.Equal("LLM_INPUT_CHARACTERS", error.Code); Assert.Equal(2, calls);
        var diagnostic = Assert.Single(execution.Diagnostics);
        Assert.Equal("json_prompt", diagnostic.OutputMode); Assert.True(diagnostic.InputCharacters > limit);
    }

    [Theory]
    [InlineData("PRIVATE source and address", "unknown")]
    [InlineData("The model does not exist", "model")]
    [InlineData("max_tokens must be at most 4096", "output-limit")]
    [InlineData("chat_template_kwargs enable_thinking not supported", "template")]
    public async Task ServerErrorsAreClassifiedWithoutEchoingBodiesOrBlindRetries(string body, string category)
    {
        var calls = 0;
        using var handler = new Handler(_ => { calls++; return Failure(body); });
        using var client = new VllmClient(Options(), handler: handler);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.CompleteAsync(new("system", "user", 100, false, Schema("result")), CancellationToken.None));
        Assert.Equal(category, error.ServerErrorCategory); Assert.Equal(400, error.HttpStatus); Assert.Equal(1, calls);
        Assert.DoesNotContain(body, error.Message);
    }

    [Fact]
    public async Task FinalFallbackTokenBudgetPreventsTheThirdTransmission()
    {
        var calls = 0;
        using var handler = new Handler(_ => ++calls == 1 ? Failure("structured_outputs unsupported") : Failure("response_format unsupported"));
        var options = Options();
        using var client = new VllmClient(options, handler: handler);
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        var schema = Schema("result");
        var inputLimit = System.Text.Encoding.UTF8.GetByteCount("systemuser" + schema.GetRawText()) + 257;
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.CompleteAsync(new("system", "user", 100, false,
            schema, InputTokenLimit: inputLimit), CancellationToken.None));
        Assert.Equal("LLM_INPUT_LIMIT", error.Code); Assert.Equal(2, calls);
        Assert.Equal("json_prompt", Assert.Single(execution.Diagnostics).OutputMode);
        Assert.Equal(2, execution.Progress.TransportRequests);
    }

    [Fact]
    public void SettingsReportOmitsAddressAndModelAndPolicyFingerprintTracksLimits()
    {
        var options = Options(); options.Model = "PRIVATE_MODEL";
        var original = SemanticExecution.PolicyFingerprint(options);
        options.MaxContextTokens--;
        Assert.NotEqual(original, SemanticExecution.PolicyFingerprint(options));
        var settings = JsonSerializer.Serialize(LlmDiagnosticReport.Settings(options));
        Assert.DoesNotContain("PRIVATE_MODEL", settings);
        Assert.DoesNotContain("127.0.0.1", settings);
        Assert.DoesNotContain("Endpoint", settings);
    }

    private sealed class Handler(Func<JsonElement, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return respond(body.RootElement);
        }
    }
}
