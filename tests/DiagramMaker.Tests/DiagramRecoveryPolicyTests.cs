using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class DiagramRecoveryPolicyTests
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new { type = "object" });

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public async Task FormatRepairAllowsTheTenthRepairButNeverAnEleventh(int succeedsAt, bool success)
    {
        var model = new Model(succeedsAt);
        var work = new StructuredLlmCompletion(model).CompleteAsync<Value>("contract", "request", Schema, 100,
            false, value => value.Text == "valid" ? null : "Invalid" + value.Text, CancellationToken.None);
        if (success) Assert.True((await work).RepairUsed);
        else Assert.Equal("LLM_SCHEMA_INVALID", (await Assert.ThrowsAsync<LlmClientException>(() => work)).Code);
        Assert.Equal(11, model.Requests);
    }

    [Fact]
    public async Task InterruptingFormatRepairPreservesResponsesAndTheOriginalBudget()
    {
        var model = new Model(11);
        var structured = new StructuredLlmCompletion(model);
        IReadOnlyList<DiagramMaker.Domain.SemanticCheckpoint>? saved;
        using (var stop = new CancellationTokenSource())
        {
            SemanticExecution? context = null;
            using (context = new(new LlmOptions(), null, stop.Token, () => {
                if (context!.Checkpoints.Count(c => c.Stage == "structured-response" && c.State == "Completed") == 5) stop.Cancel();
                return Task.CompletedTask;
            }))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Complete(context.Token));
                saved = JsonSerializer.Deserialize<DiagramMaker.Domain.SemanticCheckpoint[]>(JsonSerializer.Serialize(context.Checkpoints));
            }
        }
        using var resumed = new SemanticExecution(new LlmOptions(), saved, CancellationToken.None);
        Assert.True((await Complete(resumed.Token)).RepairUsed);
        Assert.Equal(11, model.Requests);

        Task<StructuredCompletionResult<Value>> Complete(CancellationToken token) => structured.CompleteAsync<Value>(
            "contract", "request", Schema, 100, false, value => value.Text == "valid" ? null : "Invalid" + value.Text, token);
    }

    public sealed record Value(string Text);
    private sealed class Model(int succeedsAt) : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public int Requests { get; private set; }
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var content = ++Requests >= succeedsAt ? "{\"text\":\"valid\"}" : JsonSerializer.Serialize(new Value("invalid" + Requests));
            return Task.FromResult(new VllmCompletionResult(content, "stop", 1, true, false, 0, request.MaxOutputTokens, 1, 1, 2));
        }
    }
}
