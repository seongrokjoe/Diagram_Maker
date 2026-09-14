using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class DiagramReliabilityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task StaticPagesExistBeforeTransportAndVerifiedAnnotationsSurviveCancellationAndResume()
    {
        await using var store = new InMemoryAppStore();
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var source = "class Work {" + string.Concat(Enumerable.Range(0, 8).Select(i => $"int Task{i}(){{return {i};}}")) + "}";
        var workspace = await service.CreateAsync(new("중단 보존", [new("a", "csharp", "코드", source)],
            [new("g", "그룹", ["a"], [new("flow", "flowchart", "balanced"), new("class", "class", "balanced")])]), "owner", Ct);
        var queued = await service.StartAsync(workspace.Id, new(workspace.Revision), "owner", Ct);
        var options = SemanticExecutionTests.OptionsForTest(); options.DiagramOutputTokens = 800;
        var sends = 0;
        var initialPages = 0;
        SemanticExecutionTests.PipelineHandler? handler = null;
        handler = new(async ct =>
        {
            var saved = (await store.GetCodeBlockRunAsync(queued.Id, Ct))!;
            var pages = saved.Results!.SelectMany(g => g.Views).SelectMany(v => v.Pages).ToArray();
            if (++sends == 1)
            {
                initialPages = pages.Length;
                Assert.Equal(10, initialPages);
                Assert.All(pages, p => Assert.NotEmpty(p.Diagram.MermaidDsl));
            }
            if (handler!.Completed.Count == 2)
            {
                Assert.Equal(initialPages, pages.Length);
                Assert.Contains(pages, p => p.Diagram.Explanation?.Coverage?.VerifiedUnits > 0);
                await service.CancelAsync(queued.Id, "owner", Ct);
                throw new OperationCanceledException(ct);
            }
        });
        using (var transport = new VllmClient(options, handler: handler))
            await SemanticExecutionTests.Processor(store, options, transport).ProcessAsync(
                (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
        var cancelled = (await store.GetCodeBlockRunAsync(queued.Id, Ct))!;
        Assert.Equal(CodeBlockRunState.Cancelled, cancelled.State);
        var completedKeys = handler.Completed.ToHashSet();
        Assert.Equal(2, completedKeys.Count);
        var coverage = cancelled.Execution!.Coverage!;
        Assert.True(coverage.VerifiedUnits > 0 && coverage.PendingUnits > 0);
        var resumed = await service.ResumeAsync(cancelled.Id, new(cancelled.Revision), "owner", Ct);
        using var nextHandler = new SemanticExecutionTests.PipelineHandler();
        using var nextTransport = new VllmClient(options, handler: nextHandler);
        await SemanticExecutionTests.Processor(store, options, nextTransport).ProcessAsync(
            (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!, Ct);
        var complete = (await store.GetCodeBlockRunAsync(resumed.Id, Ct))!;
        Assert.Equal(CodeBlockRunState.Completed, complete.State);
        Assert.DoesNotContain(nextHandler.Completed, completedKeys.Contains);
        Assert.Equal(initialPages, complete.Results!.SelectMany(g => g.Views).Sum(v => v.Pages.Count));
        Assert.Equal(0, complete.Execution!.Coverage!.PendingUnits);
    }

    [Fact]
    public async Task OversizedUnitsDoNotPreventLaterHealthyAnnotationsAndPartialProjectionPreservesEvidence()
    {
        var items = Enumerable.Range(0, 5).Select(i => new SharedSemanticItem("item-" + i, "operation", "작업", [], [], [], [])).ToArray();
        var transport = new CodeBlockPipelineTests.CodeTransport();
        var options = new LlmOptions { Enabled = true, MaxInputCharacters = 6000 };
        var client = new InternalLlmClient(Options.Create(options), new(), new(), transport, new(transport));
        using var execution = new SemanticExecution(options, null, Ct);
        var response = await client.GenerateSharedAsync("code-block", "분리 검사", items,
            batch => new { source = batch.Any(i => i.Id != "item-4") ? new string('x', 7000) : "return value;" },
            [new("flow", "flowchart", "balanced")], false, execution.Token);
        Assert.Equal("item-4", Assert.Single(response.Items).Id);
        Assert.Equal(4, response.Failures!.SelectMany(f => f.ItemIds).Distinct().Count());
        Assert.Equal(2, transport.Requests.Count);

        var input = new CodeBlockWorkspaceInput("부분 설명", [new("a", "csharp", "함수", "int Run(int n){if(n<0)return -1;return n;}")]);
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), input.Blocks);
        var diagram = new CodeBlockProjectionService(new()).Build(graph, [], new("g", "그룹", ["a"]), new("flow", "flowchart", "balanced")).Last().Diagram;
        var projection = new SharedSemanticProjection(true, []);
        projection.Add(new("page", diagram, new("flow", "flowchart", "balanced")));
        var item = projection.Items.Values.First();
        var page = projection.Apply(new("부분 검토", "flowchart", [new(item.Id, "검증된 설명", "코드 근거로 확인한 동작입니다")])).Pages["page"];
        Assert.Equal("Incomplete", page.Status);
        Assert.Equal(1, page.Explanation!.Coverage!.VerifiedUnits);
        Assert.Contains(page.Diagram.Nodes, n => n.Label == "검증된 설명");
        Assert.Equal(diagram.Edges.Select(e => (e.Id, e.SourceId, e.TargetId)), page.Diagram.Edges.Select(e => (e.Id, e.SourceId, e.TargetId)));
        Assert.True(diagram.Nodes.SelectMany(n => n.EvidenceIds).ToHashSet().SetEquals(page.Diagram.Nodes.SelectMany(n => n.EvidenceIds)));
    }
}
