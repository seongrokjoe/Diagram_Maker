using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class GitExecutionTests
{
    private static GitComparison Compare(string before, string after) => new(new('a', 40), new('b', 40),
        [new("Device.cs", null, ChangeKind.Modified, "old", "new", [], before, after)]);
    [Fact]
    public void GitSequenceRetainsUnresolvedCallsAndFullFunctionOnFirstPage()
    {
        var comparison = Compare("class Device{bool Run(){return false;}}",
            "class Device{bool Run(){bool result=false;if(Check()!=true)return result;Sleep(10);if(Send()!=1)return result;result=true;return result;}}");
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var artifact = Assert.Single(new DiagramProjectionService().Build("device", graph, comparison, ["sequence"], 1, 1, false).Artifacts);
        var document = DiagramDocumentBuilder.Build(artifact, DiagramEvidenceBuilder.Build(graph, comparison, graph.Changes.Select(c => c.Id).ToArray()));
        var overview = document.Pages.Single(p => p.Id == "overview").Diagram.Ir;
        Assert.Equal(new[] { "Check()", "Sleep(10)", "Send()" }, overview.Edges.Where(e => e.Type == "message").Select(e => e.OriginalExpression));
        Assert.Equal(new[] { "false", "false", "true" }, overview.Edges.Where(e => e.Type == "return").Select(e => e.ReturnValue));
        Assert.All(overview.Edges, e => Assert.All(e.EvidenceIds, id => Assert.Contains(graph.Evidence, item => item.Id == id && item.StartOffset.HasValue)));
    }
    [Fact]
    public void GitStatePairsChangedGuardAndRetainsBothRevisionEvidence()
    {
        var comparison = Compare("class Device{int state;bool ready;void Tick(){if(state==0)state=1;}}",
            "class Device{int state;bool ready;void Tick(){if(state==0 && ready)state=1;}}");
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var ir = Assert.Single(new DiagramProjectionService().Build("device", graph, comparison, ["state"], 1, 1, false).Artifacts).Ir;
        var transition = Assert.Single(ir.Edges);
        Assert.Equal("modified", transition.Status);
        Assert.Equal(DiagramChangeKind.Modified, transition.ChangeMarker!.Kind);
        Assert.Equal(new[] { comparison.BaseSha, comparison.TargetSha }, graph.Evidence.Where(e => transition.EvidenceIds.Contains(e.Id)).Select(e => e.RevisionSha).Distinct().Order());
        Assert.Equal(new[] { "0", "1" }, ir.Nodes.Select(n => n.Label));
    }
    [Theory]
    [InlineData("if(state==0){Mutate();state=1;}")]
    [InlineData("if(state==0 || ready)state=1;")]
    [InlineData("bool result=false; if(Check())return result;result=true;return result;")]
    public void StateDoesNotInventTransitionsAcrossUnknownMutationOrReturnFlags(string body)
    {
        var comparison = Compare("class Device{void Tick(){}}", "class Device{int state;bool ready;void Tick(){" + body + "}}");
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var result = new DiagramProjectionService().Build("device", graph, comparison, ["state"], 1, 1, false);
        Assert.Empty(result.Artifacts);
    }
}
