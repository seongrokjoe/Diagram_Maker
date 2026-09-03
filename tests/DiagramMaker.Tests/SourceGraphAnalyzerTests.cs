using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class SourceGraphAnalyzerTests
{
    [Fact]
    public void Analyze_MapsCSharpMethodChangeAndCall()
    {
        const string before = """
            namespace Sample;
            public class Service
            {
                public void Run() { Save(); }
                private void Save() { }
            }
            """;
        const string after = """
            namespace Sample;
            public class Service
            {
                public void Run() { Validate(); Save(); }
                private void Validate() { }
                private void Save() { }
            }
            """;
        var comparison = new GitComparison(
            new string('a', 40),
            new string('b', 40),
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new",
                [new DiffHunk(4, 1, 4, 2, "@@")], before, after)]);

        var result = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);

        Assert.Contains(result.Changes, change => change.Type == SymbolChangeKind.ModifyBody);
        Assert.Contains(result.Changes, change => change.Type == SymbolChangeKind.AddSymbol);
        Assert.Contains(result.Edges, edge => edge.Type == "calls");
        Assert.All(result.Evidence, evidence => Assert.Equal("RoslynSyntax", evidence.Analyzer));
    }

    [Fact]
    public void Analyze_CppDuplicateIdentityEvidence_DoesNotThrowAndKeepsEdge()
    {
        var targetSha = new string('b', 40);
        var comparison = new GitComparison(
            new string('a', 40),
            targetSha,
            [new ChangedFile("Service.cpp", null, ChangeKind.Modified, "before", "after", [], "void Run() {}", "void Run() { Save(); }")]);
        var run = CppFact("function:Run()", "Run", "run") with
        {
            ControlNodes =
            [
                new CppControlNodeFact("start", "entry", "시작", 1, 1),
                new CppControlNodeFact("call", "call", "Save(1)", 1, 1, 1, "function:Save(int)", true, "RunFunction")
            ],
            ControlEdges = [new CppControlEdgeFact("start", "call", "control", "")]
        };
        var save = CppFact("function:Save(int)", "Save", "save");
        var duplicateSave = save with { FilePath = "Service.cpp", StartLine = 20, EndLine = 21 };
        var index = new CppSourceIndex(
            "tree-sitter-cpp-0.23.4/index-v2",
            [run, save, duplicateSave],
            [new CppEdgeFact(run.SemanticKey, save.SemanticKey, "calls", "calls", Confidence.Exact, "Service.cpp", 1, 1, true, "RunFunction")],
            [], [], 0, 1, 100, false, []);

        var result = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison, index);

        Assert.Single(result.Identities, identity => identity.SemanticKey == save.SemanticKey);
        var edge = Assert.Single(result.Edges, edge => edge.Type == "calls");
        Assert.True(edge.IsIndirect);
        Assert.Equal("RunFunction", edge.ViaApi);
        var flow = Assert.Single(result.ControlFlows!);
        Assert.Equal(2, flow.Nodes.Count);
        Assert.Contains(flow.Nodes, node => node.IsIndirect && node.CallTargetIdentityId == edge.ToIdentityId);
    }

    private static CppSymbolFact CppFact(string semanticKey, string name, string fingerprint) => new(
        semanticKey, name, name, "function", 0, $"void {name}()", "Service.cpp", null,
        1, 2, fingerprint, [], []);
}
