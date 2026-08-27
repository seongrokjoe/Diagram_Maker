using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class MermaidCompilerTests
{
    private readonly MermaidCompiler _compiler = new(new DiagramValidator());

    [Fact]
    public void Compile_RemovesDirectiveAndHtmlCharacters()
    {
        var diagram = new DiagramIr(
            "flowchart",
            "Safe",
            [
                new DiagramNode("a", "%%{init<script>", "component", null, "added", Confidence.Exact, []),
                new DiagramNode("b", "Result", "component", null, "unchanged", Confidence.Exact, [])
            ],
            [new DiagramEdge("e", "a", "b", "calls", "go", "unchanged", Confidence.Exact, [])],
            [], []);

        var result = _compiler.Compile(diagram);

        Assert.DoesNotContain("%%", result, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", result, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_RejectsUnknownEdgeNode()
    {
        var diagram = new DiagramIr(
            "flowchart", "Invalid",
            [new DiagramNode("a", "A", "component", null, "unchanged", Confidence.Exact, [])],
            [new DiagramEdge("e", "a", "missing", "calls", "", "unchanged", Confidence.Exact, [])],
            [], []);

        Assert.Throws<DiagramValidationException>(() => _compiler.Compile(diagram));
    }
}
