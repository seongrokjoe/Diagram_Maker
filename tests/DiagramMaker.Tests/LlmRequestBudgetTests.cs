using System.Net;
using System.Text.Json;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class LlmRequestBudgetTests
{
    [Fact]
    public async Task ServerTokenBudgetIsUsedAcrossPreflightAndTransportWithOneCachedCount()
    {
        var options = SemanticExecutionTests.OptionsForTest();
        options.UseServerTokenization = true; options.MaxInputTokens = 1000;
        using var handler = new Handler();
        using var client = new VllmClient(options, handler: handler);
        using var execution = new SemanticExecution(options, null, CancellationToken.None);
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var structured = new StructuredLlmCompletion(client);
        var prompt = new string('a', 5000);
        Assert.True(await structured.FitsAsync("system", prompt, schema.RootElement, null, 100, 1000, 10000, false, CancellationToken.None));
        var result = await structured.CompleteAsync<Reply>("system", prompt, schema.RootElement, 100, false, _ => null, CancellationToken.None,
            inputTokenLimit: 1000, inputCharacterLimit: 10000);
        Assert.Equal("ok", result.Value.Result);
        Assert.Equal(1, handler.Tokenizations);
        Assert.Equal(1, execution.Progress.TokenizationRequests);
        Assert.Equal(1, execution.Progress.Requests);
        Assert.Equal(1, execution.Progress.TransportRequests);
        Assert.True(Assert.Single(execution.Diagnostics).EstimatedInputTokens); // Schema reserve remains conservative.
        Assert.All(execution.Progress.StageMilliseconds!, entry => Assert.True(entry.Value >= 0));
        Assert.True(execution.Progress.StageMilliseconds!.ContainsKey("llm-Reply"));
    }

    [Fact]
    public async Task DifferentConcurrentRequestsUseOnlyOneCompletionAtATime()
    {
        using var handler = new Handler();
        using var client = new VllmClient(SemanticExecutionTests.OptionsForTest(), handler: handler);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => client.CompleteAsync(new("system", "request " + i, 100, false), CancellationToken.None)));
        Assert.Equal(4, handler.Completions);
        Assert.Equal(1, handler.MaximumConcurrent);
    }

    private sealed record Reply(string Result);
    private sealed class Handler : HttpMessageHandler
    {
        public int Tokenizations { get; private set; }
        public int Completions { get; private set; }
        public int MaximumConcurrent { get; private set; }
        private int active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/tokenize", StringComparison.Ordinal))
            {
                Tokenizations++;
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"count\":25}") };
            }
            Completions++;
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref active));
            try
            {
                await Task.Delay(20, ct);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"result\\\":\\\"ok\\\"}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":25,\"completion_tokens\":5,\"total_tokens\":30}}") };
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
