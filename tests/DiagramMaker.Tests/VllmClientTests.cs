using System.Net;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
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
    public async Task FallsBackOnceWhenStructuredOutputsAreUnsupported()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity),
            Response("fallback"));
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");
        using var client = CreateClient(handler);

        var result = await client.CompleteAsync(new VllmCompletionRequest(
            "system", "user", 100, EnableThinking: true, schemaDocument.RootElement.Clone()), CancellationToken.None);

        Assert.True(result.StructuredOutputFallbackUsed);
        Assert.False(result.StructuredOutputApplied);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("structured_outputs", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("structured_outputs", handler.Requests[1].Body, StringComparison.Ordinal);
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
            value => !string.IsNullOrWhiteSpace(value.Result), CancellationToken.None));

        Assert.Equal("LLM_RESPONSE_TRUNCATED", error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RepairsInvalidStructuredResponseWithoutRepeatingRawContent()
    {
        const string rejected = "SENSITIVE_REJECTED_RAW_RESPONSE";
        var handler = new QueueHandler(Response(rejected), Response("{\"result\":\"valid\"}"));
        using var client = CreateClient(handler);
        var structured = new StructuredLlmCompletion(client);
        using var schemaDocument = JsonDocument.Parse("""{"type":"object"}""");

        var result = await structured.CompleteAsync<TestResult>(
            "system", "synthetic user", schemaDocument.RootElement.Clone(), 100, false,
            value => value.Result == "valid", CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal("valid", result.Value.Result);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(rejected, handler.Requests[1].Body, StringComparison.Ordinal);
    }

    private static VllmClient CreateClient(HttpMessageHandler handler) => new(Options(), handler: handler);

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
            }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed record TestResult(string Result);

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
