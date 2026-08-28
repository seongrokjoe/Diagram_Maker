using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class DiagramProjectionServiceTests
{
    [Fact]
    public void Build_DefaultImpactScope_ExcludesUnrelatedSymbolsAndIncludesExternalCaller()
    {
        var repositoryId = Guid.NewGuid();
        const string beforeService = "namespace Sample; public class Service { public void Run() { Save(); } private void Save() { } public void Unrelated() { } }";
        const string afterService = "namespace Sample; public class Service { public void Run() { Validate(); Save(); } private void Validate() { } private void Save() { } public void Unrelated() { } }";
        const string caller = "namespace Sample; public class Caller { public void Invoke(Service service) { service.Run(); } }";
        var comparison = new GitComparison(
            new string('a', 40), new string('b', 40),
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new", [new DiffHunk(1, 1, 1, 1, "@@")], beforeService, afterService)],
            [
                new RepositoryFileSnapshot("Caller.cs", new string('a', 40), "caller-old", caller),
                new RepositoryFileSnapshot("Caller.cs", new string('b', 40), "caller-new", caller)
            ]);
        var graph = new SourceGraphAnalyzer().Analyze(repositoryId, comparison);
        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["flowchart"], 1, 1, false);
        var diagram = Assert.Single(result.Artifacts).Ir;

        Assert.Contains(diagram.Nodes, node => node.Label.Contains("Service.Run", StringComparison.Ordinal));
        Assert.Contains(diagram.Nodes, node => node.Label.Contains("Caller.Invoke", StringComparison.Ordinal));
        Assert.DoesNotContain(diagram.Nodes, node => node.Label.Contains("Unrelated", StringComparison.Ordinal));
        Assert.Contains(diagram.Edges, edge => edge.Type == "calls");
    }

    [Fact]
    public void Build_StateType_IsReportedUnavailableWithoutTransitionEvidence()
    {
        var comparison = new GitComparison(
            new string('a', 40), new string('b', 40),
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new", [], "class Service { void Run() {} }", "class Service { void Run() { } }")]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);

        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["state"], 1, 1, false);

        var availability = Assert.Single(result.Availability);
        Assert.False(availability.Available);
        Assert.Contains("상태 전이", availability.Reason, StringComparison.Ordinal);
    }
}
