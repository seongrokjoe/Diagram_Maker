using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class DiagramVariantsTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly MermaidCompiler Compiler = new(new());
    private static DiagramPage Source()
    {
        var ir = new DiagramIr("flowchart", "작업", [
            new("first", "Start()", "process", null, "unchanged", Confidence.Exact, ["source"]),
            new("last", "Finish()", "process", null, "unchanged", Confidence.Exact, ["source"])],
            [new("call", "first", "last", "flow", "처리", "unchanged", Confidence.Exact, ["source"],
                Call: new("Finish", ["value"], "result", "int", [], "code"))], [], ["code-block"]);
        return DiagramVariants.PreserveCode(new("overview", "작업", new(Guid.NewGuid(), ir.Type, 1, ir,
            Compiler.Compile(ir), DateTimeOffset.UtcNow, new("코드 구조", [], [], ["source"], "Static", []))));
    }
    private static SemanticGeneration Meaning(DiagramPage page, string status = "Semantic") =>
        new(page.Diagram.Ir with { Nodes = page.Diagram.Ir.Nodes.Select(n => n with { Label = "검토한 의미 설명" }).ToArray() },
            status, [], [], 1, new("검토 완료", [], [], ["source"], status, []));

    [Fact]
    public void OnlyCompleteMeaningCreatesAnIndependentAiOriginal()
    {
        var source = Source();
        var partial = DiagramVariants.Apply(source, Meaning(source, "Incomplete"), Compiler);
        Assert.Same(source.Diagram, partial.Diagram);
        Assert.Null(DiagramVariants.Select(partial, "ai"));
        var result = DiagramVariants.Apply(source, Meaning(source), Compiler, true);
        Assert.NotEqual(source.Diagram.Id, result.Diagram.Id);
        Assert.Same(source.Diagram, DiagramVariants.Select(result, "code"));
        Assert.Same(result.Diagram, DiagramVariants.Select(result, "ai"));
        Assert.Contains("Start()", result.CodeDiagram!.MermaidDsl);
        Assert.DoesNotContain("검토한 의미 설명", result.CodeDiagram.MermaidDsl);
        var rejected = DiagramVariants.Apply(result, Meaning(result, "Incomplete"), Compiler, true);
        Assert.Same(result.Diagram, rejected.Diagram);
        Assert.Equal("Failed", rejected.AiState);
        Assert.Equal(new DiagramResultCounts(1, 1, 0, 1, 1), DiagramVariants.Count([rejected], true, reused: 1));
        Assert.Null(DiagramVariants.Select(result, "unknown"));
    }

    [Fact]
    public void ReviewedSubsetCreatesASeparatePartialAiVariantAndCount()
    {
        var source = Source();
        var explanation = new DiagramExplanation("일부 검토", [], ["source"], ["source"], "Incomplete",
            ["한 항목의 의미 검토를 완료하지 못했습니다."], Coverage: new(2, 1, 1, 1),
            Failures: [new(["item-b"], "semantic-review", "LLM_SEMANTIC_REVIEW", ["source"],
                ["원본 근거에 맞게 실패 항목만 보정합니다."], IssueCodes: ["reversed_condition"])]);
        var generated = new SemanticGeneration(source.Diagram.Ir with
        {
            Nodes = source.Diagram.Ir.Nodes.Select((node, index) => index == 0 ? node with { Label = "검토된 동작" } : node).ToArray()
        }, "Incomplete", explanation.Warnings, [], 2, explanation, "semantic-review");

        var result = DiagramVariants.Apply(source, generated, Compiler, true);

        Assert.Equal("Partial", result.AiState);
        Assert.Equal("semantic", result.ResultKind);
        Assert.True(DiagramVariants.IsPartialAi(result.Diagram));
        Assert.NotNull(DiagramVariants.Select(result, "ai"));
        Assert.Same(source.Diagram, DiagramVariants.Select(result, "code"));
        Assert.Contains("검토된 동작", result.Diagram.MermaidDsl);
        Assert.Equal(new DiagramResultCounts(0, 0, 0, 1, 0, 1), DiagramVariants.Count([result], true));
    }

    [Fact]
    public void LegacyArtifactsRemainReadableAndPendingWorkIsDistinctFromFailure()
    {
        var source = Source();
        var old = source with { CodeDiagram = null, ResultKind = null, AiState = null,
            Diagram = source.Diagram with { Explanation = null } };
        Assert.Same(old.Diagram, DiagramVariants.Select(old, "code"));
        Assert.Equal(new DiagramResultCounts(0, 0, 1, 1), DiagramVariants.Count([source], false));
        Assert.Equal(new DiagramResultCounts(0, 1, 0, 1), DiagramVariants.Count([old], true));
        var roundTrip = JsonSerializer.Deserialize<DiagramPage>(JsonSerializer.Serialize(DiagramVariants.Apply(source, Meaning(source), Compiler)))!;
        Assert.NotEqual(roundTrip.Diagram.Id, roundTrip.CodeDiagram!.Id);
        Assert.Equal(source.Diagram.MermaidDsl, roundTrip.CodeDiagram.MermaidDsl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VariantsHaveIndependentRevisionsAndOwnerScopedDeletion(bool file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "variants-" + Guid.NewGuid().ToString("N"), "store.json");
        await using var store = file ? (IAppStore)new LocalFileAppStore(path) : new InMemoryAppStore();
        await store.InitializeAsync(Ct);
        var service = new CodeBlockWorkspaceService(store, Options.Create(new CodeBlockOptions()), new());
        var workspace = await service.CreateAsync(new("작업", [new("code", "csharp", "코드", "void Run() {}")]), "owner", Ct);
        var run = await service.StartAsync(workspace.Id, new(1), "owner", Ct);
        var leased = (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!;
        var source = Source();
        var result = DiagramVariants.Apply(source, Meaning(source), Compiler, true);
        Assert.True(await store.SaveCodeBlockRunAsync(leased with { Revision = leased.Revision + 1, State = CodeBlockRunState.Completed,
            Results = [new("g", "그룹", ["code"], [new("v", new("v", "flowchart", "balanced"), "Completed", [result], [])], [])] }, leased.Revision, Ct));
        var revisions = new DiagramRevisionService(store, new(), Compiler);
        foreach (var variant in new[] { "ai", "code" })
        {
            var artifact = CodeBlockWorkspaceService.Page((await store.GetCodeBlockRunAsync(run.Id, Ct))!, "g", "v", "overview", variant);
            var document = new DiagramEditDocument(variant, "LR", artifact.Ir.Nodes.Select(n => new EditableDiagramNode(n.Id, n.Label)).ToArray(),
                artifact.Ir.Edges.Select(e => new EditableDiagramEdge(e.Id, e.SourceId, e.TargetId, "사용자 연결", e.Type)).ToArray());
            var revision = await revisions.SaveAsync(artifact, new(artifact.Id, null, 1, document), "owner", "code-block", run.Id, "g", "v", Ct);
            Assert.Equal(2, revision.Version);
            Assert.Null(revision.Diagram.Ir.Edges[0].Call);
            Assert.Single(await store.ListDiagramRevisionsAsync(artifact.Id, "owner", Ct));
            Assert.Empty(await store.ListDiagramRevisionsAsync(artifact.Id, "other", Ct));
        }
        if (file)
        {
            await using var reopened = new LocalFileAppStore(path);
            await reopened.InitializeAsync(Ct);
            var restored = (await reopened.GetCodeBlockRunAsync(run.Id, Ct))!.Results![0].Views[0].Pages[0];
            Assert.Equal(source.Diagram.Id, restored.CodeDiagram!.Id);
            Assert.Equal(result.Diagram.Id, restored.Diagram.Id);
            Assert.Single(await reopened.ListDiagramRevisionsAsync(result.Diagram.Id, "owner", Ct));
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRunAsync(run.Id, "other", Ct));
        await service.DeleteAsync(workspace.Id, workspace.Revision, "owner", Ct);
        Assert.Empty(await store.ListDiagramRevisionsAsync(result.Diagram.Id, "owner", Ct));
        Assert.Empty(await store.ListDiagramRevisionsAsync(source.Diagram.Id, "owner", Ct));
    }

    [Fact]
    public void CompactAnalysisKeepsReusedAiCountsWithoutExplanationBodies()
    {
        var source = Source();
        var result = DiagramVariants.Apply(source, Meaning(source), Compiler, true);
        var compact = result with { Diagram = result.Diagram with { Explanation = null } };
        var view = new AnalysisDiagramViewResult("v", new("v", "flowchart", "balanced"), compact.Diagram, [],
            Reused: true, Document: new("overview", [compact], []));
        var group = new AnalysisDiagramGroupResult("g", "그룹", [], compact.Diagram, new("", "", [], []), [], [view]);
        Assert.Equal(new DiagramResultCounts(1, 0, 0, 1, 1), DiagramVariants.CountAnalysis([group], true));
    }

    [Theory]
    [InlineData("값 != 결과")]
    [InlineData("결과 == true이면 반환")]
    [InlineData("값 <= 10 && 결과 >= 0")]
    public void ComparisonsArePlainTextButStillRequireSemanticReview(string text) =>
        Assert.Null(SharedSemanticValidation.CheckItem(new("a", text, "원본 조건의 의미를 검토합니다"), 0));

    [Theory]
    [InlineData("값 <script>실행</script>")]
    [InlineData("값 ```code```")]
    [InlineData("값 javascript:실행")]
    public void MarkupAndExecutableSyntaxRemainRejected(string text) =>
        Assert.Equal("SharedTextCodeSyntax", SharedSemanticValidation.CheckItem(new("a", text, "원본 설명"), 0)!.Code);

    [Theory]
    [InlineData("#include <windows.h>", true)]
    [InlineData("#include \"winbase.h\"", true)]
    [InlineData("// #include <windows.h>", false)]
    [InlineData("/*\n#include <windows.h>\n*/", false)]
    [InlineData("#include <mywindows.h>", false)]
    public void OnlyExplicitWindowsHeadersEnableBundledContracts(string text, bool expected) =>
        Assert.Equal(expected, CallPresentationBuilder.HasWindowsContract(text));
}
