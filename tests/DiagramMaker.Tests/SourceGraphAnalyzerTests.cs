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
        var call = result.Edges.First(edge => edge.Type == "calls" && edge.RevisionSha == comparison.TargetSha && edge.StartLine == 4);
        Assert.Contains(result.Evidence, evidence => evidence.Id == Assert.Single(call.EvidenceIds) && evidence.Analyzer == "RoslynInvocation");
        Assert.All(result.Evidence, evidence => Assert.StartsWith("Roslyn", evidence.Analyzer, StringComparison.Ordinal));
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

    [Fact]
    public void Analyze_CSharpExtractsClassMembersAssociationsAndSwitchBranches()
    {
        const string source = """
            namespace Sample;
            public class Store { public void Save() { } }
            public class Service
            {
                private Store _store;
                public int Run(int kind)
                {
                    switch (kind)
                    {
                        case 1: _store.Save(); break;
                        default: return 0;
                    }
                    return 1;
                }
            }
            """;
        var targetSha = new string('b', 40);
        var comparison = new GitComparison(new string('a', 40), targetSha,
            [new ChangedFile("Service.cs", null, ChangeKind.Added, null, "new", [], null, source)]);

        var result = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);

        var service = result.Versions.Single(version => version.QualifiedName == "Sample.Service");
        Assert.Contains(service.Members!, member => member.Name == "_store" && member.Accessibility == "private" && member.DeclaredType == "Store");
        Assert.Contains(service.Members!, member => member.Name == "Run" && member.Accessibility == "public" && member.Kind == "method");
        Assert.Contains(result.Edges, edge => edge.FromIdentityId == service.IdentityId && edge.Type == "association");
        var runIdentity = result.Versions.Single(version => version.QualifiedName == "Sample.Service.Run").IdentityId;
        var flow = result.ControlFlows!.Single(item => item.IdentityId == runIdentity && item.RevisionSha == targetSha);
        Assert.Contains(flow.Edges, edge => edge.Label == "case 1");
        Assert.Contains(flow.Edges, edge => edge.Label == "default");
    }

    private static CppSymbolFact CppFact(string semanticKey, string name, string fingerprint) => new(
        semanticKey, name, name, "function", 0, $"void {name}()", "Service.cpp", null,
        1, 2, fingerprint, [], []);
}
