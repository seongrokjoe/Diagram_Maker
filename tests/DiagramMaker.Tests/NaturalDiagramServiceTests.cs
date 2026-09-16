using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalDiagramServiceTests
{
    [Fact]
    public async Task GenerateAsync_ReusesSameSessionRequestButKeepsRegenerationAsRevision()
    {
        var llm = new FakeLlm();
        var store = new InMemoryAppStore();
        await store.InitializeAsync(CancellationToken.None);
        using var cache = new NaturalDiagramSessionCache();
        var validator = new DiagramValidator();
        var service = new NaturalDiagramService(
            llm,
            new MermaidCompiler(validator),
            store,
            cache,
            new DiagramPresetCatalog(),
            Options.Create(new LlmOptions { Model = "stable-model" }),
            new TestEnvironment());
        var request = new NaturalDiagramRequest("사용자에서 서비스로 흐름", "flowchart");

        var first = await service.GenerateAsync(request, "reviewer", CancellationToken.None);
        var reused = await service.GenerateAsync(request, "reviewer", CancellationToken.None);
        var regenerated = await service.GenerateAsync(request with { ParentDiagramId = first.Id, ForceRegenerate = true }, "reviewer", CancellationToken.None);
        var originalAgain = await service.GenerateAsync(request, "reviewer", CancellationToken.None);

        Assert.Equal(first.Id, reused.Id);
        Assert.True(reused.Reused);
        Assert.Equal(2, regenerated.Diagram.Version);
        Assert.Equal(first.Id, regenerated.ParentDiagramId);
        Assert.Equal(first.Id, originalAgain.Id);
        Assert.Equal(2, llm.CallCount);
        Assert.Equal("TB", first.Diagram.Ir.Direction);
        Assert.Equal("flow-vertical-overview", first.Request.PresetId);
    }

    [Fact]
    public async Task ReviseViewsAsync_RegeneratesOnlyRequestedViewAndReusesTheOtherView()
    {
        var llm = new FakeLlm();
        var store = new InMemoryAppStore();
        await store.InitializeAsync(CancellationToken.None);
        using var cache = new NaturalDiagramSessionCache();
        var validator = new DiagramValidator();
        var service = new NaturalDiagramService(
            llm, new MermaidCompiler(validator), store, cache, new DiagramPresetCatalog(),
            Options.Create(new LlmOptions { Model = "stable-model" }), new TestEnvironment());
        var views = new[]
        {
            new DiagramViewSelection("flow", "flowchart", "flow-vertical-overview"),
            new DiagramViewSelection("sequence", "sequence", "sequence-caller-context")
        };
        var request = new NaturalDiagramRequest("사용자에서 서비스로 흐름", "flowchart", Views: views);

        var first = await service.GenerateAsync(request, "reviewer", CancellationToken.None);
        var revised = await service.ReviseViewsAsync(first, views, new HashSet<string>(["sequence"]),
            "reviewer", CancellationToken.None);

        Assert.Equal(3, llm.CallCount);
        Assert.Equal(2, revised.Views!.Count);
        Assert.True(revised.Views.Single(view => view.ViewId == "flow").Reused);
        Assert.False(revised.Views.Single(view => view.ViewId == "sequence").Reused);
        Assert.Equal(first.Views!.Single(view => view.ViewId == "flow").Diagram!.Id,
            revised.Views.Single(view => view.ViewId == "flow").Diagram!.Id);
        Assert.Equal(2, revised.Revision);
    }

    [Theory]
    [InlineData("호출 순서를 시퀀스로 그려줘", "sequence")]
    [InlineData("클래스 상속 관계를 그려줘", "class")]
    [InlineData("주문 상태 전이를 그려줘", "state")]
    [InlineData("서비스 구성을 그려줘", "flowchart")]
    public void TypeResolver_UsesDeterministicKoreanKeywords(string prompt, string expected)
    {
        Assert.Equal(expected, NaturalDiagramTypeResolver.Resolve("auto", prompt));
    }

    [Fact]
    public async Task CompactViewKeepsAllNodesAndFailedRegenerationPreservesReviewedSource()
    {
        var llm = new FakeLlm { NodeCount = 45 };
        var store = new InMemoryAppStore();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, new(new()), store, cache, new(),
            Options.Create(new LlmOptions()), new TestEnvironment());
        var view = new DiagramViewSelection("flow", "flowchart", "balanced", new(DetailLevel: "compact"));
        var first = await service.GenerateAsync(new("동작 설계", Views: [view]), "owner", CancellationToken.None);
        Assert.Equal(45, first.Diagram.Ir.Nodes.Count);
        Assert.Equal(44, first.Diagram.Ir.Edges.Count);
        Assert.NotNull((await store.GetNaturalDiagramAsync(first.Id, CancellationToken.None))!.Views![0].Diagram);
        llm.Fail = true;
        var failed = await service.ReviseViewsAsync(first, first.Request.EffectiveViews(), new HashSet<string> { "flow" }, "owner", CancellationToken.None);
        Assert.Equal("Failed", failed.Views![0].State);
        Assert.Equal(first.Diagram.Id, failed.Diagram.Id);
        Assert.Equal(first.Diagram.Id, failed.Views[0].LastSuccessfulDiagram!.Id);
    }

    [Fact]
    public async Task GenerateAsync_CreatesOnePagePerPromptScenarioInsideTheSelectedView()
    {
        const string prompt = "사용자가 요청을 등록한다.\n\n관리자가 요청을 승인한다.";
        var llm = new FakeLlm
        {
            ExtractedRequirements = new("요청 승인", ["사용자", "관리자"],
            [
                new("r1", "요청 등록", "action", "explicit", "사용자가 요청을 등록한다."),
                new("r2", "요청 승인", "action", "explicit", "관리자가 요청을 승인한다.")
            ])
        };
        var store = new InMemoryAppStore();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, new(new()), store, cache, new(),
            Options.Create(new LlmOptions()), new TestEnvironment());

        var result = await service.GenerateAsync(new(prompt, "flowchart"), "owner", CancellationToken.None);

        var view = Assert.Single(result.Views!);
        Assert.Equal("Completed", view.State);
        Assert.Equal(2, view.Pages!.Count);
        Assert.Equal(2, llm.CallCount);
        Assert.Equal(2, result.Requirements!.Scenarios!.Count);
        Assert.Equal(result.Requirements.Scenarios.Select(scenario => scenario.Id),
            view.Pages.Select(page => page.ScenarioId));
        Assert.All(view.Pages, page =>
        {
            Assert.NotNull(page.Diagram);
            Assert.Equal(1, page.Diagram!.Version);
        });
    }

    [Fact]
    public async Task ExecuteRunAsync_ReportsEveryFailedScenarioBeforeReturningTheFailure()
    {
        const string prompt = "첫 작업을 실행한다.\n\n두 번째 작업을 실행한다.";
        var llm = new FakeLlm
        {
            Fail = true,
            ExtractedRequirements = new("실패 보존", ["작업"],
            [
                new("r1", "첫 작업", "action", "explicit", "첫 작업을 실행한다."),
                new("r2", "두 번째 작업", "action", "explicit", "두 번째 작업을 실행한다.")
            ])
        };
        var store = new InMemoryAppStore();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, new(new()), store, cache, new(),
            Options.Create(new LlmOptions()), new TestEnvironment());
        var now = DateTimeOffset.UtcNow;
        var run = new NaturalDiagramRun(Guid.NewGuid(), "owner", new(prompt, "flowchart"),
            NaturalDiagramRunState.Generating, now, now);
        NaturalGenerationProgress? last = null;

        var error = await Assert.ThrowsAsync<LlmClientException>(() => service.ExecuteRunAsync(run,
            (progress, _) => { last = progress; return Task.CompletedTask; }, CancellationToken.None));

        Assert.Equal("NATURAL_REQUIREMENTS_REVIEW", error.Code);
        var view = Assert.Single(last!.Views);
        Assert.Equal("Failed", view.State);
        Assert.Equal(2, view.Pages!.Count);
        Assert.All(view.Pages, page =>
        {
            Assert.Equal("Failed", page.State);
            Assert.Equal("NATURAL_REQUIREMENTS_REVIEW", page.ErrorCode);
            Assert.Null(page.Diagram);
        });
    }

    [Fact]
    public async Task InterruptedRunRetainsExtractionBeforeTheFirstDiagramAndResumeReusesIt()
    {
        const string prompt = "첫 작업을 실행한다.";
        var llm = new FakeLlm { ExtractedRequirements = new("작업", [],
            [new("r1", "첫 작업", "action", "explicit", prompt)]) };
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, new(new()), new InMemoryAppStore(), cache, new(),
            Options.Create(new LlmOptions()), new TestEnvironment());
        var now = DateTimeOffset.UtcNow;
        var run = new NaturalDiagramRun(Guid.NewGuid(), "owner", new(prompt, "flowchart"),
            NaturalDiagramRunState.Generating, now, now);
        NaturalGenerationProgress? saved = null;
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExecuteRunAsync(run, (progress, _) =>
        {
            saved = progress;
            throw new OperationCanceledException();
        }, CancellationToken.None));
        Assert.NotNull(saved!.Requirements);
        Assert.Equal(0, llm.CallCount);
        var result = await service.ExecuteRunAsync(run with { Requirements = saved.Requirements, Views = saved.Views },
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, llm.ExtractionCount);
        Assert.Equal(1, llm.CallCount);
        Assert.Equal("Completed", Assert.Single(result.Views!).State);
    }

    [Fact]
    public async Task CacheKeepsDistinctWhitespaceForOriginalSourceOffsets()
    {
        var llm = new FakeLlm();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, new(new()), new InMemoryAppStore(), cache, new(),
            Options.Create(new LlmOptions()), new TestEnvironment());
        var first = await service.GenerateAsync(new("첫 작업\n\n두 번째 작업", "flowchart"), "owner", CancellationToken.None);
        var second = await service.GenerateAsync(new("첫 작업 두 번째 작업", "flowchart"), "owner", CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("첫 작업 두 번째 작업", second.Request.Prompt);
    }

    private sealed class FakeLlm : IInternalLlmClient
    {
        public bool IsEnabled => true;
        public int CallCount { get; private set; }
        public int ExtractionCount { get; private set; }
        public int NodeCount { get; init; } = 2;
        public bool Fail { get; set; }
        public NaturalRequirements? ExtractedRequirements { get; init; }

        public Task<NaturalRequirements?> ExtractNaturalRequirementsAsync(string prompt, bool thinking, CancellationToken ct)
        {
            ExtractionCount++;
            return Task.FromResult(ExtractedRequirements);
        }

        public Task<DiagramIr?> GenerateNaturalDiagramAsync(
            string prompt,
            string requestedType,
            bool enableThinking,
            DiagramPreset preset,
            DiagramStyleOverrides? style,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (Fail) throw new LlmClientException("NATURAL_REQUIREMENTS_REVIEW", "검토 실패");
            DiagramIr result = new(
                requestedType,
                "Stable",
                [
                    new DiagramNode("a", "사용자", "component", null, "unchanged", Confidence.Inferred, []),
                    new DiagramNode("b", "서비스", "component", null, "unchanged", Confidence.Inferred, [])
                ],
                [new DiagramEdge("e1", "a", "b", "flow", "요청", "unchanged", Confidence.Inferred, [], requestedType == "sequence" ? 1 : null)],
                [],
                []);
            if (NodeCount > 2) result = result with
            {
                Nodes = Enumerable.Range(0, NodeCount).Select(i => new DiagramNode("n" + i, "동작 " + i, "operation", null, "unchanged", Confidence.Inferred, [])).ToArray(),
                Edges = Enumerable.Range(1, NodeCount - 1).Select(i => new DiagramEdge("e" + i, "n" + (i - 1), "n" + i, "flow", "다음", "unchanged", Confidence.Inferred, [])).ToArray()
            };
            return Task.FromResult<DiagramIr?>(result);
        }

        public Task<IReadOnlyList<AnalysisGroupDraft>?> RegroupChangesAsync(
            IReadOnlyList<ChangeCandidate> candidates,
            IReadOnlyList<AnalysisGroupDraft> staticGroups,
            VersionedGraph graph,
            IReadOnlyList<ChangedFile> files,
            bool enableThinking,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ReviewNarrative?> GenerateReviewAsync(VersionedGraph graph, IReadOnlyList<ChangedFile> files, bool enableThinking, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LlmConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LlmContractTestResult> TestDiagramContractAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LlmThinkingContractTestResult> TestThinkingContractAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
