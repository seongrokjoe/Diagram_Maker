using System.Net;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class SemanticExecutionTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static LlmOptions OptionsForTest() => new() { Enabled = true, Endpoint = "http://localhost:19001/v1/chat/completions",
        AllowedOrigin = "http://localhost:19001", UseServerTokenization = false, MaxTransientRetries = 0 };

    [Fact]
    public async Task LegacyInterruptedRunStartsNewExecutionFromItsSnapshotAndPreservesPublishedSummary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "semantic-" + Guid.NewGuid().ToString("N"), "store.json");
        var options = OptionsForTest(); options.SemanticJobBudgetSeconds = 1;
        Guid runId;
        string[] completedRequests;
        await using (var store = new LocalFileAppStore(path))
        {
            await store.InitializeAsync(Ct);
            var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
            var workspace = await service.CreateAsync(new("진행 보존", [new("code", "csharp", "코드",
                "class Tasks { void One(){int first=1;} void Two(){int second=2;} }")],
                [new("g", "그룹", ["code"], [new("flow", "flowchart", "balanced")])]), "owner", Ct);
            var run = await service.StartAsync(workspace.Id, new(1), "owner", Ct); runId = run.Id;
            // Retain the old pipeline regression for persisted internal.5 runs.
            run = run with { GenerationVersion = null, Revision = run.Revision + 1 };
            Assert.True(await store.SaveCodeBlockRunAsync(run, run.Revision - 1, Ct));
            var handler = new PipelineHandler(async ct =>
            {
                var current = await store.GetCodeBlockRunAsync(runId, ct);
                if (current?.Results?.SelectMany(g => g.Views).Any(v => v.Pages.Any(p => p.Diagram.Explanation?.Status == "Semantic")) == true)
                    await Task.Delay(Timeout.Infinite, ct);
            });
            using var transport = new VllmClient(options, handler: handler);
            await Processor(store, options, transport).ProcessAsync((await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
            var stopped = (await store.GetCodeBlockRunAsync(runId, Ct))!;
            Assert.Equal(CodeBlockRunState.Partial, stopped.State); Assert.Equal("budget", stopped.StopReason);
            Assert.Equal("Semantic", stopped.Results![0].Views[0].Pages[0].Diagram.Explanation!.Status);
            Assert.NotEmpty(stopped.Checkpoints!); Assert.True(CodeBlockWorkspaceService.Summary(stopped).CanResume);
            completedRequests = handler.Completed.ToArray(); Assert.NotEmpty(completedRequests);
        }
        await using (var store = new LocalFileAppStore(path))
        {
            await store.InitializeAsync(Ct);
            var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
            var original = (await store.GetCodeBlockRunAsync(runId, Ct))!;
            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResumeAsync(runId, new(original.Revision), "outsider", Ct));
            var resumed = await service.ResumeAsync(runId, new(original.Revision), "owner", Ct);
            Assert.Equal("shared-semantic-v2", resumed.GenerationVersion);
            Assert.Equal(original.Snapshot, resumed.Snapshot);
            Assert.Null(resumed.Checkpoints);
            Assert.Null(resumed.Results);
            await Assert.ThrowsAsync<CodeBlockConflictException>(() => service.ResumeAsync(runId, new(original.Revision), "owner", Ct));
            var handler = new PipelineHandler();
            using var transport = new VllmClient(options, handler: handler);
            await Processor(store, options, transport).ProcessAsync((await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
            var complete = (await store.GetCodeBlockRunAsync(resumed.Id, Ct))!;
            Assert.Equal(CodeBlockRunState.Completed, complete.State);
            Assert.Equal(0, complete.Execution!.ReusedUnits);
            Assert.DoesNotContain(handler.Completed, completedRequests.Contains);
            Assert.Equal(CodeBlockRunState.Partial, (await store.GetCodeBlockRunAsync(runId, Ct))!.State);
            var diagnostic = JsonSerializer.Serialize(complete.Diagnostics, Json);
            Assert.DoesNotContain("Tasks", diagnostic); Assert.DoesNotContain("localhost", diagnostic);
            Assert.All(complete.Diagnostics!, d => Assert.NotNull(d.OutputLimit));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitLeaseReplacementAndCancellationRejectStaleWriters(bool file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "git-semantic-" + Guid.NewGuid().ToString("N"), "store.json");
        await using IAppStore store = file ? new LocalFileAppStore(path) : new InMemoryAppStore();
        await store.InitializeAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var job = new AnalysisJob(Guid.NewGuid(), new(Guid.NewGuid(), "base", "target"), AnalysisState.Queued,
            null, null, 0, "", null, null, null, now, now, null);
        await store.SaveAnalysisAsync(job, Ct);
        var first = (await store.TryLeaseAnalysisAsync(TimeSpan.FromSeconds(-1), Ct))!;
        var second = (await store.TryLeaseAnalysisAsync(TimeSpan.FromMinutes(1), Ct))!;
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.False(await store.UpdateAnalysisAsync(first with { Revision = first.Revision + 1 }, first.Revision, Ct));
        Assert.False(await store.RenewAnalysisLeaseAsync(job.Id, first.LeaseId!.Value, TimeSpan.FromMinutes(1), Ct));
        var cancelled = second with { State = AnalysisState.Cancelled, Revision = second.Revision + 1,
            Checkpoints = [new("key", "stage", "{}")], StopReason = "user-cancelled" };
        Assert.True(await store.UpdateAnalysisAsync(cancelled, second.Revision, Ct));
        Assert.False(await store.UpdateAnalysisAsync(second with { State = AnalysisState.Completed, Revision = second.Revision + 1 }, second.Revision, Ct));
        Assert.Null(await store.TryLeaseAnalysisAsync(TimeSpan.FromMinutes(1), Ct));
        var resumed = cancelled with { State = AnalysisState.Queued, Revision = cancelled.Revision + 1, StopReason = null };
        Assert.True(await store.UpdateAnalysisAsync(resumed, cancelled.Revision, Ct));
        Assert.False(await store.UpdateAnalysisAsync(resumed, cancelled.Revision, Ct));
        Assert.Single((await store.GetAnalysisAsync(job.Id, Ct))!.Checkpoints!);
    }

    [Fact]
    public void ReferenceAliasesDoNotCollideWithUserIdsAndOnlyRestoreReferenceFields()
    {
        var ids = new PromptIds();
        var encoded = ids.Encode("""{"id":"ref1","sourceId":"long-original-id-123","nodeIds":["long-original-id-123"],"label":"ref2"}""");
        using var document = JsonDocument.Parse(encoded);
        Assert.Equal("ref2", document.RootElement.GetProperty("sourceId").GetString());
        var restored = ids.Restore(encoded);
        Assert.Contains("long-original-id-123", restored); Assert.Contains("\"label\":\"ref2\"", restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdditionalContextIsLimitedToProvenRelatedSymbols(bool invalid)
    {
        var input = new CodeBlockWorkspaceInput("문맥", [new("a", "csharp", "코드", "class Work { void Run(){Save();} void Save(){int stored=1;} }")]);
        var graph = await new CodeBlockAnalyzer(new(), Options.Create(new GitWorkerOptions()), Options.Create(new CodeBlockOptions()), null!)
            .AnalyzeAsync(Guid.NewGuid(), input.Blocks, Ct);
        var transport = new ContextTransport(invalid);
        var client = new InternalLlmClient(Options.Create(OptionsForTest()), new(), new(), transport, new(transport));
        if (invalid)
        {
            await Assert.ThrowsAsync<LlmClientException>(() => client.UnderstandCodeBlocksAsync(input, graph, new("g", "그룹", ["a"]),
                [new("flowchart", true, null)], Ct));
            Assert.False(transport.SuppliedContext);
        }
        else
        {
            var result = await client.UnderstandCodeBlocksAsync(input, graph, new("g", "그룹", ["a"]), [new("flowchart", true, null)], Ct);
            Assert.NotNull(result); Assert.True(transport.SuppliedContext);
            var facts = graph.Symbols.SelectMany(s => s.Steps.Select(step => step.Id).Append(s.Id)).ToHashSet();
            Assert.All(result.Behaviors, b => Assert.All(b.FactIds, id => Assert.Contains(id, facts)));
        }
    }

    private sealed class ContextTransport(bool invalid) : ILlmCompletionTransport
    {
        private readonly CodeBlockPipelineTests.CodeTransport fallback = new();
        private bool requested;
        public bool IsEnabled => true;
        public bool SuppliedContext { get; private set; }
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(request.UserPrompt);
            var root = document.RootElement;
            CodeBlockUnderstanding? response = null;
            if (root.TryGetProperty("additionalContext", out var extra))
            {
                SuppliedContext = true;
                Assert.Contains("stored=1", extra[0].GetProperty("code").GetString());
                response = new("저장 동작", "", [new("behavior", "값을 저장", [root.GetProperty("context").GetProperty("symbol").GetProperty("id").GetString()!], [], [])]);
            }
            else if (!requested && root.TryGetProperty("relatedSymbols", out var related) && related.GetArrayLength() > 0)
            {
                requested = true;
                response = new("문맥 필요", "", [], [invalid ? "outside-symbol" : related[0].GetProperty("id").GetString()!]);
            }
            if (response is null) return fallback.CompleteAsync(request, ct);
            return Task.FromResult(new VllmCompletionResult(JsonSerializer.Serialize(response, Json), "stop", 1, true, false, 0, 1000, 10, 10, 20));
        }
    }

    [Fact]
    public async Task TokenBudgetRejectsBeforeSendingAndDiagnosticCannotContainSource()
    {
        var options = OptionsForTest(); options.MaxInputTokens = 10;
        var handler = new PipelineHandler(); using var transport = new VllmClient(options, handler: handler);
        using var execution = new SemanticExecution(options, null, Ct);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => transport.CompleteAsync(new("system", "private-source-value", 100, false), Ct));
        Assert.Equal("LLM_INPUT_LIMIT", error.Code); Assert.Empty(handler.Completed);
        var diagnostic = Assert.Single(execution.Diagnostics); Assert.False(diagnostic.Sent); Assert.True(diagnostic.EstimatedInputTokens);
        Assert.DoesNotContain("private-source", JsonSerializer.Serialize(diagnostic));
    }

    internal static CodeBlockRunProcessor Processor(IAppStore store, LlmOptions options, ILlmCompletionTransport transport) => new(store,
        new(new(), Options.Create(new GitWorkerOptions()), Options.Create(new CodeBlockOptions()), null!),
        new(Options.Create(new CodeBlockOptions())), new(new()),
        new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport)), new(new()), new(), Options.Create(options));

    [Fact]
    public async Task ResumeShowsDurableCompletionBeforeReplayAndCountsEachReusedUnitOnce()
    {
        IReadOnlyList<SemanticCheckpoint> saved;
        SemanticProgress progress;
        var requests = 0;
        Task<DiagramPlanReview?> Generate()
        {
            requests++;
            return Task.FromResult<DiagramPlanReview?>(new(true, []));
        }
        using (var first = new SemanticExecution(OptionsForTest(), null, Ct))
        {
            await SemanticExecution.RunAsync("review", "stable", Generate, value => value.Accepted);
            saved = first.Checkpoints; progress = first.Progress;
            Assert.Equal(1, progress.CompletedUnits);
        }
        using var resumed = new SemanticExecution(OptionsForTest(), saved, Ct, savedProgress: progress);
        Assert.Equal(1, resumed.Progress.CompletedUnits);
        Assert.Equal(0, resumed.Progress.AttemptCompletedUnits);
        Assert.Equal(2, resumed.Progress.AttemptNumber);
        await SemanticExecution.RunAsync("review", "stable", Generate, value => value.Accepted);
        await SemanticExecution.RunAsync("review", "stable", Generate, value => value.Accepted);
        Assert.Equal(1, requests);
        Assert.Equal(1, resumed.Progress.ReusedUnits);
        Assert.Equal(1, resumed.Progress.CompletedUnits);
    }

    [Fact]
    public async Task DurableRejectedReviewIsReusedWhileRepairRemainsPending()
    {
        IReadOnlyList<SemanticCheckpoint> saved;
        using (var first = new SemanticExecution(OptionsForTest(), null, Ct))
        {
            await SemanticExecution.RunAsync<DiagramPlanReview>("review", "first-plan",
                () => Task.FromResult<DiagramPlanReview?>(new(false, ["Unsupported change"])), value => value.Issues.Count > 0);
            saved = first.Checkpoints;
        }
        using var resumed = new SemanticExecution(OptionsForTest(), saved, Ct);
        var review = await SemanticExecution.RunAsync<DiagramPlanReview>("review", "first-plan",
            () => throw new InvalidOperationException("Completed review must not be requested again"), value => value.Issues.Count > 0);
        Assert.False(review!.Accepted);
        Assert.Equal(1, resumed.Progress.ReusedUnits);
    }

    [Fact]
    public async Task PermanentContractFailureDoesNotRepeatAcrossTwoResumes()
    {
        IReadOnlyList<SemanticCheckpoint>? saved = null;
        var requests = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var execution = new SemanticExecution(OptionsForTest(), saved, Ct);
            var error = await Assert.ThrowsAsync<LlmClientException>(() => SemanticExecution.RunAsync<DiagramPlanReview>("review", "invalid",
                () => { requests++; throw new LlmClientException("LLM_SCHEMA_INVALID", "invalid", failureKind: "MalformedJson"); }, value => value.Accepted));
            Assert.Equal("MalformedJson", error.FailureKind);
            Assert.Equal(1, execution.Progress.FailedUnits);
            saved = execution.Checkpoints;
        }
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task LogicalRequestAndActualTransportRetryAreCountedSeparately()
    {
        var options = OptionsForTest(); options.MaxTransientRetries = 1;
        using var execution = new SemanticExecution(options, null, Ct);
        using var transport = new VllmClient(options, handler: new RetryHandler());
        await transport.CompleteAsync(new("system", "synthetic input", 100, false), Ct);
        Assert.Equal(1, execution.Progress.Requests);
        Assert.Equal(2, execution.Progress.TransportRequests);
        Assert.Equal(1, Assert.Single(execution.Diagnostics).Retries);
        Assert.Equal(0, execution.Progress.RejectedBeforeSend);
    }

    private sealed class RetryHandler : HttpMessageHandler
    {
        private int calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(++calls == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """{"choices":[{"message":{"content":"{}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
                    Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task LargeFunctionPagesRetainAllEdgesAndMeaningUsesWholeStatementsWithOriginalScopes()
    {
        var source = "void Run(){\r\n" + string.Join("\r\n", Enumerable.Range(0, 610).Select(i => $"Consume({i}, \"한글😀{new string('x', 80)}\");")) + "\r\n}";
        var input = new CodeBlockWorkspaceInput("큰 함수", [new("a", "csharp", "함수", source)]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var symbol = Assert.Single(graph.Symbols);
        var pages = new CodeBlockProjectionService(new()).Build(graph, [], new("g", "그룹", ["a"]), new("flow", "flowchart", "balanced"));
        Assert.True(pages.Count > 3);
        var detail = pages.Where(p => p.Level == "detail").ToArray();
        Assert.True(symbol.Steps.Select(s => s.Id).ToHashSet().SetEquals(detail.SelectMany(p => p.Diagram.Nodes).Select(n => n.Id)));
        Assert.All(symbol.FlowEdges, edge => Assert.Contains(detail.SelectMany(p => p.Diagram.Edges), e => e.SourceId == edge.SourceId && e.TargetId == edge.TargetId));
        Assert.All(pages, p => new MermaidCompiler(new()).Compile(p.Diagram));
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var client = new InternalLlmClient(Options.Create(OptionsForTest()), new(), new(), transport, new(transport));
        var result = await client.PlanCodeBlockDiagramAsync(detail[1].Diagram, input, graph, null, new("flow", "flowchart", "balanced"), Ct);
        Assert.Equal("Semantic", result!.Status);
        Assert.True(detail[1].Diagram.Nodes.SelectMany(n => n.SourceFactIds!).ToHashSet().SetEquals(result.Diagram.Nodes.SelectMany(n => n.SourceFactIds!)));
        Assert.Contains(transport.Requests, request => request.UserPrompt.Contains("\"partialContext\":true"));
        Assert.All(graph.Evidence, evidence => Assert.Equal(CodeBlockWorkspaceService.Hash(source), evidence.ContentHash));
        var call = symbol.Calls[400];
        Assert.Contains("Consume(400,", source[call.Location.StartOffset..call.Location.EndOffset]);
        Assert.Equal(402, call.Location.StartLine);
    }

    internal sealed class PipelineHandler(Func<CancellationToken, Task>? before = null) : HttpMessageHandler
    {
        private readonly CodeBlockPipelineTests.CodeTransport model = new();
        public List<string> Completed { get; } = [];
        public int? StopAfter { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            if (before is not null) await before(ct);
            if (StopAfter is { } limit && Completed.Count >= limit) await Task.Delay(Timeout.Infinite, ct);
            var body = await message.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement; var messages = root.GetProperty("messages");
            var request = new VllmCompletionRequest(messages[0].GetProperty("content").GetString()!, messages[1].GetProperty("content").GetString()!,
                root.GetProperty("max_tokens").GetInt32(), root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(),
                root.GetProperty("structured_outputs").GetProperty("json").Clone());
            var completion = await model.CompleteAsync(request, ct);
            Completed.Add(CodeBlockWorkspaceService.Hash(body));
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { message = new { content = completion.Content }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 100, completion_tokens = 100, total_tokens = 200 } }), Encoding.UTF8, "application/json") };
        }
    }
}
