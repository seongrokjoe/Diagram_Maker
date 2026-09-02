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

    [Theory]
    [InlineData("호출 순서를 시퀀스로 그려줘", "sequence")]
    [InlineData("클래스 상속 관계를 그려줘", "class")]
    [InlineData("주문 상태 전이를 그려줘", "state")]
    [InlineData("서비스 구성을 그려줘", "flowchart")]
    public void TypeResolver_UsesDeterministicKoreanKeywords(string prompt, string expected)
    {
        Assert.Equal(expected, NaturalDiagramTypeResolver.Resolve("auto", prompt));
    }

    private sealed class FakeLlm : IInternalLlmClient
    {
        public bool IsEnabled => true;
        public int CallCount { get; private set; }

        public Task<DiagramIr?> GenerateNaturalDiagramAsync(
            string prompt,
            string requestedType,
            bool enableThinking,
            DiagramPreset preset,
            DiagramStyleOverrides? style,
            CancellationToken cancellationToken)
        {
            CallCount++;
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
