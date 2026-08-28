using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;

namespace DiagramMaker.Tests;

public sealed class MermaidDslRevisionServiceTests
{
    private readonly DiagramValidator _validator = new();

    [Theory]
    [InlineData("flowchart")]
    [InlineData("sequence")]
    [InlineData("class")]
    [InlineData("state")]
    public void Parse_RoundTripsCompilerSubset(string type)
    {
        var compiler = new MermaidCompiler(_validator);
        var source = Diagram(type);
        var service = new MermaidDslRevisionService(new InMemoryAppStore(), _validator, compiler);

        var parsed = service.Parse(compiler.Compile(source), source);
        var recompiled = compiler.Compile(parsed);

        Assert.Equal(source.Type, parsed.Type);
        Assert.Equal(source.Nodes.Count, parsed.Nodes.Count);
        Assert.Equal(source.Edges.Count, parsed.Edges.Count);
        Assert.Equal(compiler.Compile(source), recompiled);
    }

    [Fact]
    public void Parse_RejectsDirectivesAndExternalLinks()
    {
        var source = Diagram("flowchart");
        var service = new MermaidDslRevisionService(new InMemoryAppStore(), _validator, new MermaidCompiler(_validator));
        const string malicious = """
            flowchart LR
              n_a["A"]
              click n_a "https://outside.invalid"
            """;

        var exception = Assert.Throws<ArgumentException>(() => service.Parse(malicious, source));

        Assert.Contains("disallowed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_CreatesPersistentChildRevision()
    {
        var store = new InMemoryAppStore();
        await store.InitializeAsync(CancellationToken.None);
        var compiler = new MermaidCompiler(_validator);
        var source = Diagram("flowchart");
        var parentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var parent = new NaturalDiagramRecord(
            parentId,
            new NaturalDiagramRequest("A에서 B로 이동", "flowchart"),
            new DiagramArtifact(parentId, source.Type, 1, source, compiler.Compile(source), now),
            now,
            "reviewer",
            parentId);
        await store.SaveNaturalDiagramAsync(parent, CancellationToken.None);
        var service = new MermaidDslRevisionService(store, _validator, compiler);
        var edited = compiler.Compile(source).Replace("B", "Result", StringComparison.Ordinal);

        var revision = await service.SaveAsync(parent, edited, "reviewer", CancellationToken.None);
        var restored = await store.GetNaturalDiagramAsync(revision.Id, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(parent.Id, revision.ParentDiagramId);
        Assert.Equal(parent.Id, revision.RootDiagramId);
        Assert.Equal(2, revision.Diagram.Version);
        Assert.Equal("manualDsl", revision.Source);
        Assert.Contains(revision.Diagram.Ir.Nodes, node => node.Label == "Result");
    }

    private static DiagramIr Diagram(string type)
    {
        var nodes = new[]
        {
            new DiagramNode("a", "A", type == "sequence" ? "participant" : "component", null, "unchanged", Confidence.Inferred, []),
            new DiagramNode("b", "B", type == "sequence" ? "participant" : "component", null, "unchanged", Confidence.Inferred, [])
        };
        var edge = new DiagramEdge("e1", "a", "b", type switch
        {
            "sequence" => "message",
            "class" => "inherits",
            "state" => "transition",
            _ => "flow"
        }, "go", "unchanged", Confidence.Inferred, [], type == "sequence" ? 1 : null);
        return new DiagramIr(type, "Round trip", nodes, [edge], [], []);
    }
}
