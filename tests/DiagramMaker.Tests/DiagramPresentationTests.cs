using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class DiagramPresentationTests
{
    private static CodeBlockGraph Graph(string source) => new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(
        Guid.NewGuid(), [new("block", "csharp", "block", source)]);
    private static DiagramIr Sequence(string source)
    {
        var graph = Graph(source);
        return ExecutionSequenceProjection.Build(graph.Symbols.First(s => s.Name == "Run"), graph, "TB");
    }
    private static IEnumerable<SequenceBlock> Blocks(IEnumerable<SequenceBlock> blocks) =>
        blocks.SelectMany(b => new[] { b }.Concat(Blocks(b.Children)));

    [Theory]
    [InlineData("ready && allowed")]
    [InlineData("status != Ready")]
    [InlineData("value > 3 || fallback")]
    public void CodeFlowKeepsPredicateAfterSharedSemanticProjection(string expression)
    {
        var graph = Graph($"void Run(){{ if ({expression}) Save(); }}");
        var source = new CodeBlockProjectionService(new()).Build(graph, [], new("g", "g", ["block"]),
            new("flow", "flowchart", "balanced")).First().Diagram;
        var node = Assert.Single(source.Nodes, n => n.Kind == "condition");
        Assert.Equal(expression, node.OriginalExpression);
        Assert.EndsWith("\n" + expression, node.Label);
        var shared = new SharedSemanticProjection(true, []);
        shared.Add(new("page", source, new("flow", "flowchart", "balanced")));
        var result = shared.Apply(new("조건을 확인합니다", "flowchart", shared.Items.Values.Select(i =>
            new SharedSemanticAnnotation(i.Id, "진행 가능 여부 확인", "입력의 조건식으로 진행 여부를 확인합니다")).ToArray()));
        var condition = Assert.Single(result.Pages["page"].Diagram.Nodes, n => n.Kind == "condition");
        Assert.Equal("진행 가능 여부 확인\n" + expression, condition.Label);
        Assert.Equal(node.EvidenceIds, condition.EvidenceIds);
        Assert.Equal(expression, condition.OriginalExpression);
    }

    [Fact]
    public void GitFlowShowsBothRevisionPredicatesInsteadOfGenericBranchLabel()
    {
        var comparison = new GitComparison(new('a', 40), new('b', 40),
            [new ChangedFile("Work.cs", null, ChangeKind.Modified, "old", "new", [new DiffHunk(1, 1, 1, 1, "@@")],
                "class Work { void Run(){if(status != 1) Save();} void Save(){} }",
                "class Work { void Run(){if(status != 2) Save();} void Save(){} }")]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var result = new DiagramProjectionService().Build("work", graph, comparison, ["flowchart"], 1, 1, false);
        var conditions = result.Artifacts.SelectMany(a => a.Ir.Nodes).Where(n => n.Kind == "condition").ToArray();
        Assert.NotEmpty(conditions);
        Assert.Contains(conditions, n => n.OriginalExpression == "status != 2");
        Assert.All(conditions, n => { Assert.NotNull(n.OriginalExpression); Assert.Contains(n.OriginalExpression!, n.Label); });
        Assert.DoesNotContain(result.Artifacts, a => a.MermaidDsl.Contains("조건에 따른 분기"));
    }

    [Theory]
    [InlineData("Read()", false)]
    [InlineData("(Read())", false)]
    [InlineData("(int)Read()", true)]
    [InlineData("((int)(Read()))", true)]
    public void WrappedCallAssignmentIsRepresentedOnceWithEvidence(string value, bool conversion)
    {
        var diagram = Sequence($"int Run(){{int result={value}; return result;}}");
        var response = Assert.Single(diagram.Edges, e => e.Type == "response");
        Assert.Equal("result", response.Call!.AssignedTo);
        Assert.Equal(conversion, response.Label.Contains("형 변환"));
        Assert.True(response.SourceFactIds!.Count >= 2);
        Assert.DoesNotContain(Blocks(diagram.SequenceBlocks!), b => b.Kind == "note" && b.OriginalExpression?.Contains("result=") == true);
    }

    [Fact]
    public void LocalNotesAreSummarizedAndRetainIndividualOriginalEvidence()
    {
        var diagram = Sequence("int Run(){int count=0; int size=10; count++; Save(); return count;}");
        var notes = Blocks(diagram.SequenceBlocks!).Where(b => b.Kind == "note").ToArray();
        Assert.Single(notes);
        Assert.Contains("3개 동작", notes[0].Label);
        Assert.Equal(3, notes[0].Children.Count);
        Assert.All(notes[0].Children, b => { Assert.NotNull(b.OriginalExpression); Assert.NotEmpty(b.EvidenceIds!); });
        var shared = new SharedSemanticProjection(true, []);
        shared.Add(new("page", diagram, new("sequence", "sequence", "balanced")));
        Assert.Contains(shared.Items.Values, i => i.Details.Contains("note") && i.FactIds.Count == 3);
        var dsl = new MermaidCompiler(new()).Compile(diagram);
        Assert.DoesNotContain("count=0", dsl);
        Assert.DoesNotContain("size=10", dsl);
    }

    [Fact]
    public void EqualLookingCallsAndAssignmentsInDifferentBranchesStayDistinct()
    {
        var diagram = Sequence("int Run(){int result; if(ready){result=Read();result=2;}else{result=Read();result=3;}return result;}");
        Assert.Equal(2, diagram.Edges.Count(e => e.Type == "message"));
        var notes = Blocks(diagram.SequenceBlocks!).Where(b => b.Kind == "note").ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes, b => b.Label.Contains("2로 설정"));
        Assert.Contains(notes, b => b.Label.Contains("3로 설정"));
    }

    [Fact]
    public void EmptyEvaluationFragmentsDoNotRenderAndFalseOnlyPathKeepsPolarity()
    {
        var diagram = Sequence("void Run(){if(a && b){} if(c){}else Save();}");
        var before = Blocks(diagram.SequenceBlocks!).Count();
        var dsl = new MermaidCompiler(new()).Compile(diagram);
        Assert.DoesNotContain("    alt ", dsl);
        Assert.DoesNotContain("    else ", dsl);
        Assert.Contains("    opt !(c)", dsl);
        Assert.Contains("Save()", dsl);
        Assert.Equal(before, Blocks(diagram.SequenceBlocks!).Count());
        Assert.Contains(Blocks(diagram.SequenceBlocks!), b => b.OriginalExpression == "a && b");
    }
}
