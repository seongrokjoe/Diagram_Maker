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
}
