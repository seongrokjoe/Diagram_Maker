using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class CodeBlockAnalyzerTests
{
    private readonly SourceGraphAnalyzer analyzer = new();
    private static CodeBlockInput Block(string id, string code) => new(id, "csharp", id, code);
    [Fact]
    public void FragmentsRetainOriginalUnicodeAndCrLfLocations()
    {
        const string code = "// 한글 😀\r\nif (ready) { Save(); } return;";
        var graph = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("a", code)]);
        var symbol = Assert.Single(graph.Symbols);
        Assert.True(symbol.IsFragment);
        Assert.DoesNotContain("__", symbol.Name);
        var call = Assert.Single(symbol.Calls);
        Assert.Equal("Save()", code[call.Location.StartOffset..call.Location.EndOffset]);
        Assert.Equal(2, call.Location.StartLine);
        Assert.All(graph.Evidence, e => Assert.InRange(e.Location.EndOffset, 0, code.Length));
    }
    [Fact]
    public void SemanticBindingResolvesCallsAcrossRealTypes()
    {
        var graph = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("a", "class A { void Run(){ B.Save(1); } }"),
            Block("b", "class B { public static void Save(int x){} public static void Save(string x){} }")]);
        var call = Assert.Single(graph.Symbols.SelectMany(s => s.Calls));
        Assert.NotNull(call.TargetSymbolId);
        Assert.Contains("int", graph.Symbols.Single(s => s.Id == call.TargetSymbolId).Signature);
    }
    [Fact]
    public void SeparateMethodFragmentsDoNotInventSharedOwners()
    {
        var graph = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("a", "void Run(){Save();}"), Block("b", "void Save(){}")]);
        Assert.Null(Assert.Single(graph.Symbols.SelectMany(s => s.Calls)).TargetSymbolId);
        Assert.DoesNotContain(graph.Symbols, s => s.Kind == "class");
    }
    [Fact]
    public void StateRequiresGuardedAssignmentNotEnumNames()
    {
        var graph = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("a", "enum S { Idle, Running } class A { S state; void Tick(){ if(state == S.Idle) state = S.Running; } }")]);
        var transition = Assert.Single(graph.Transitions);
        Assert.Equal("S.Idle", transition.From);
        Assert.Equal("S.Running", transition.To);
        var empty = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("b", "enum S { Idle, Running }")]);
        Assert.Empty(empty.Transitions);
    }
    [Fact]
    public void MalformedAndEmptyAnalysisAreVisible()
    {
        var graph = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("a", "void Run(){ if (")]);
        Assert.NotEmpty(graph.Warnings);
    }
    [Theory]
    [InlineData("if (!(state == S.Idle)) state = S.Running;")]
    [InlineData("if (state == S.Idle || ready) state = S.Running;")]
    public void NegativeAndAlternativeGuardsDoNotInventStateOrigins(string body)
    {
        var graph = analyzer.AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [Block("a", "enum S { Idle, Running } class A { S state; bool ready; void Tick(){" + body + "} }")]);
        Assert.Empty(graph.Transitions);
    }
}
