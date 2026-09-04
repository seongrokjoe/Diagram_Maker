using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;

namespace DiagramMaker.Tests;

public sealed class DiagramRevisionServiceTests
{
    [Fact]
    public async Task PreviewAsync_DoesNotPersistAndPreservesExistingChangeMarkers()
    {
        await using var store = new InMemoryAppStore();
        var validator = new DiagramValidator();
        var service = new DiagramRevisionService(store, validator, new MermaidCompiler(validator));
        var marker = new DiagramChangeMarker(DiagramChangeKind.Modified, DiagramChangePrecision.Exact,
            "Service.cs", 3, 3, ["evidence"]);
        var basis = Artifact();
        basis = basis with { Ir = basis.Ir with { Nodes = basis.Ir.Nodes.Select((node, index) => index == 0 ? node with { ChangeMarker = marker } : node).ToArray() } };
        var document = new DiagramEditDocument("미리보기", "TB",
            [new EditableDiagramNode("a", "변경된 A"), new EditableDiagramNode("b", "B")],
            [new EditableDiagramEdge("e", "a", "b", "호출")]);

        var preview = await service.PreviewAsync(basis,
            new SaveDiagramEditRequest(basis.Id, null, 1, document), "reviewer", CancellationToken.None);

        Assert.Equal(1, preview.Version);
        Assert.Equal(marker, preview.Ir.Nodes[0].ChangeMarker);
        Assert.Empty(await store.ListDiagramRevisionsAsync(basis.Id, "reviewer", CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_CreatesValidatedRevisionAndDetectsStaleVersion()
    {
        var store = new InMemoryAppStore();
        await store.InitializeAsync(CancellationToken.None);
        var validator = new DiagramValidator();
        var service = new DiagramRevisionService(store, validator, new MermaidCompiler(validator));
        var artifact = Artifact();
        var document = new DiagramEditDocument(
            "수정된 흐름", "TB",
            [new EditableDiagramNode("a", "시작"), new EditableDiagramNode("b", "완료")],
            [new EditableDiagramEdge("e", "a", "b", "처리")]);
        var request = new SaveDiagramEditRequest(artifact.Id, null, artifact.Version, document);

        var created = await service.SaveAsync(artifact, request, "reviewer", "natural", Guid.NewGuid(), null,
            "flow", CancellationToken.None);

        Assert.Equal(2, created.Version);
        Assert.Equal("TB", created.Diagram.Ir.Direction);
        Assert.Contains("처리", created.Diagram.MermaidDsl, StringComparison.Ordinal);
        var conflict = await Assert.ThrowsAsync<DiagramRevisionConflictException>(() => service.SaveAsync(
            artifact, request, "reviewer", "natural", Guid.NewGuid(), null, "flow", CancellationToken.None));
        Assert.Equal(2, conflict.CurrentVersion);
    }

    [Fact]
    public async Task SaveAsync_RejectsEdgeThatReferencesDeletedNode()
    {
        var store = new InMemoryAppStore();
        await store.InitializeAsync(CancellationToken.None);
        var validator = new DiagramValidator();
        var service = new DiagramRevisionService(store, validator, new MermaidCompiler(validator));
        var artifact = Artifact();
        var document = new DiagramEditDocument(
            "잘못된 흐름", "LR",
            [new EditableDiagramNode("a", "시작")],
            [new EditableDiagramEdge("e", "a", "b", "처리")]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            artifact, new SaveDiagramEditRequest(artifact.Id, null, 1, document), "reviewer", "analysis",
            Guid.NewGuid(), "group", "flow", CancellationToken.None));
    }

    private static DiagramArtifact Artifact()
    {
        var ir = new DiagramIr(
            "flowchart", "흐름",
            [
                new DiagramNode("a", "A", "component", null, "unchanged", Confidence.Exact, []),
                new DiagramNode("b", "B", "component", null, "unchanged", Confidence.Exact, [])
            ],
            [new DiagramEdge("e", "a", "b", "flow", "호출", "unchanged", Confidence.Exact, [])],
            [], [], "LR");
        var compiler = new MermaidCompiler(new DiagramValidator());
        return new DiagramArtifact(Guid.NewGuid(), "flowchart", 1, ir, compiler.Compile(ir), DateTimeOffset.UtcNow);
    }
}
