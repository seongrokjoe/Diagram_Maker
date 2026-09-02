using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class AnalysisPlanProcessorTests
{
    [Fact]
    public void StaticGrouping_MakesCallConnectedChangesExclusiveAndSuggestsSequence()
    {
        var repositoryId = Guid.NewGuid();
        var identities = new[]
        {
            new SymbolIdentity("a", repositoryId, "cpp", "function", "A"),
            new SymbolIdentity("b", repositoryId, "cpp", "function", "B"),
            new SymbolIdentity("c", repositoryId, "cpp", "function", "C")
        };
        var versions = identities.Select(identity => new SymbolVersion(
            $"v-{identity.Id}", identity.Id, "target", identity.SemanticKey, $"void {identity.SemanticKey}()",
            $"{identity.Id}.cpp", 1, 2, identity.Id)).ToArray();
        var changes = identities.Select(identity => new SymbolChange(
            $"c-{identity.Id}", SymbolChangeKind.ModifyBody,
            $"v-{identity.Id}", $"v-{identity.Id}", Confidence.Exact, [])).ToArray();
        var graph = new VersionedGraph(
            identities,
            versions,
            [new GraphEdge("ab", "a", "b", "calls", "B", Confidence.Exact, [])],
            [],
            changes);

        var candidates = AnalysisPlanProcessor.BuildCandidates(graph);
        var groups = AnalysisPlanProcessor.BuildStaticGroups(candidates, graph);

        Assert.Equal(2, groups.Count);
        var connected = Assert.Single(groups, group => group.ChangeIds.Contains("c-a"));
        Assert.Contains("c-b", connected.ChangeIds);
        Assert.Equal("sequence", connected.SuggestedDiagramType);
        Assert.Equal(candidates.Count, groups.SelectMany(static group => group.ChangeIds).Distinct().Count());
    }

    [Fact]
    public void StaticGrouping_CapsLargePlansAtFiftyExclusiveGroups()
    {
        var repositoryId = Guid.NewGuid();
        var identities = Enumerable.Range(0, 60)
            .Select(index => new SymbolIdentity($"identity-{index}", repositoryId, "cpp", "function", $"Function{index}"))
            .ToArray();
        var versions = identities.Select((identity, index) => new SymbolVersion(
            $"version-{index}", identity.Id, "target", identity.SemanticKey, $"void Function{index}()",
            $"src/File{index}.cpp", 1, 2, identity.Id)).ToArray();
        var changes = identities.Select((identity, index) => new SymbolChange(
            $"change-{index}", SymbolChangeKind.ModifyBody,
            versions[index].Id, versions[index].Id, Confidence.Exact, [])).ToArray();
        var graph = new VersionedGraph(identities, versions, [], [], changes);

        var candidates = AnalysisPlanProcessor.BuildCandidates(graph);
        var groups = AnalysisPlanProcessor.BuildStaticGroups(candidates, graph);
        var groupedIds = groups.SelectMany(static group => group.ChangeIds).ToArray();

        Assert.InRange(groups.Count, 1, 50);
        Assert.Equal(candidates.Count, groupedIds.Length);
        Assert.Equal(groupedIds.Length, groupedIds.Distinct(StringComparer.Ordinal).Count());
    }
}
