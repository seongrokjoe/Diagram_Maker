using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class MermaidCompilerTests
{
    private readonly MermaidCompiler _compiler = new(new DiagramValidator());

    [Fact]
    public void Compile_ChangeMarker_EmitsStableEdgeIdBadgeAndRedStyle()
    {
        var marker = new DiagramChangeMarker(DiagramChangeKind.Modified, DiagramChangePrecision.Exact,
            "Service.cs", 5, 5, ["evidence"]);
        var diagram = new DiagramIr("flowchart", "Changed",
            [
                new DiagramNode("a", "A", "method", null, "unchanged", Confidence.Exact, [], ChangeMarker: marker),
                new DiagramNode("b", "B", "method", null, "unchanged", Confidence.Exact, [])
            ],
            [new DiagramEdge("edge", "a", "b", "calls", "Save", "unchanged", Confidence.Exact, [], ChangeMarker: marker)],
            [], []);

        var result = _compiler.Compile(diagram);

        Assert.Contains("A · ~ 수정", result, StringComparison.Ordinal);
        Assert.Contains("n_edge@-->", result, StringComparison.Ordinal);
        Assert.Contains("stroke:#dc2626", result, StringComparison.Ordinal);
    }

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

    [Fact]
    public void Compile_ControlFlow_UsesSemanticShapesAndVisibleBranches()
    {
        var diagram = new DiagramIr(
            "flowchart", "Flow",
            [
                new DiagramNode("start", "시작", "entry", "Service::Run", "modified", Confidence.Exact, [], "terminal"),
                new DiagramNode("condition", "value > 0", "condition", "Service::Run", "modified", Confidence.Exact, [], "decision"),
                new DiagramNode("call", "Save()", "call", "Service::Run", "modified", Confidence.Exact, [], "call")
            ],
            [
                new DiagramEdge("e1", "start", "condition", "control", "", "modified", Confidence.Exact, []),
                new DiagramEdge("e2", "condition", "call", "control", "예", "modified", Confidence.Exact, [])
            ], [], [], "TB");

        var result = _compiler.Compile(diagram);

        Assert.Contains("{\"value &gt; 0\"}", result, StringComparison.Ordinal);
        Assert.Contains("[[\"Save()\"]]", result, StringComparison.Ordinal);
        Assert.Contains("|\"예\"|", result, StringComparison.Ordinal);
        Assert.Contains("stroke-width:2px", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ClassCall_PreservesCallerToCalleeDirection()
    {
        var diagram = new DiagramIr(
            "class", "Classes",
            [
                new DiagramNode("caller", "Caller", "class", null, "modified", Confidence.Exact, []),
                new DiagramNode("callee", "Callee", "class", null, "unchanged", Confidence.Exact, [])
            ],
            [new DiagramEdge("e", "caller", "callee", "calls", "calls", "unchanged", Confidence.Exact, [])],
            [], []);

        var result = _compiler.Compile(diagram);

        Assert.Contains("n_caller --> n_callee", result, StringComparison.Ordinal);
        Assert.DoesNotContain("n_callee --> n_caller", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Sequence_EmitsControlScopesAndIndirectCallLabel()
    {
        var scopes = new[]
        {
            new ControlScope("loop", "loop", "index < count", "body"),
            new ControlScope("branch", "alt", "ready", "then")
        };
        var diagram = new DiagramIr(
            "sequence", "Sequence",
            [
                new DiagramNode("source", "InterfaceCustom", "class", null, "modified", Confidence.Exact, []),
                new DiagramNode("target", "Opr_Xfer", "class", null, "unchanged", Confidence.Exact, [])
            ],
            [new DiagramEdge("e", "source", "target", "calls", "runOrgReturn", "unchanged", Confidence.Inferred, [], 1, true, "RunFunction", scopes)],
            [], []);

        var result = _compiler.Compile(diagram);

        Assert.Contains("loop index < count", result, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", result, StringComparison.Ordinal);
        Assert.Contains("alt ready", result, StringComparison.Ordinal);
        Assert.Contains("-->>+n_target: 간접 API: RunFunction", result, StringComparison.Ordinal);
        Assert.Contains("-->>-n_source: return", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_LabeledLoopBack_UsesMermaidElevenDottedLabelSyntax()
    {
        var diagram = new DiagramIr(
            "flowchart", "Loop",
            [
                new DiagramNode("call", "호출", "call", null, "modified", Confidence.Exact, []),
                new DiagramNode("loop", "반복", "loop", null, "modified", Confidence.Exact, [])
            ],
            [new DiagramEdge("e", "call", "loop", "loopBack", "다음 반복", "modified", Confidence.Exact, [])],
            [], []);

        var result = _compiler.Compile(diagram);

        Assert.Contains("n_call n_e@-. 다음 반복 .-> n_loop", result, StringComparison.Ordinal);
        Assert.DoesNotContain("-.->|", result, StringComparison.Ordinal);
    }
}
