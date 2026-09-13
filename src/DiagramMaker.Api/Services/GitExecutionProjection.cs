using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class GitExecutionProjection
{
    public static DiagramIr Build(string title, VersionedGraph graph, GitComparison comparison,
        IReadOnlySet<string> selected, string direction)
    {
        var methods = (graph.Executions ?? []).Where(e => selected.Contains(e.IdentityId) &&
            (e.RevisionSha == comparison.TargetSha || !graph.Executions!.Any(t => t.IdentityId == e.IdentityId && t.RevisionSha == comparison.TargetSha))).ToArray();
        var symbols = new List<CodeBlockSymbol>();
        var evidence = new List<CodeBlockEvidence>();
        foreach (var method in methods)
        {
            var version = graph.Versions.First(v => v.IdentityId == method.IdentityId && v.RevisionSha == method.RevisionSha);
            var id = version.Id;
            CodeBlockLocation Location(EvidenceRef e) => new(method.FilePath, e.StartLine, e.EndLine, e.StartOffset ?? 0, e.EndOffset ?? 0);
            foreach (var e in graph.Evidence.Where(e => e.RevisionSha == method.RevisionSha && e.FilePath == method.FilePath))
                evidence.Add(new(e.Id, method.FilePath, e.BlobOid, Location(e)));
            var facts = ExecutionSequenceProjection.Flatten(method.Events).ToArray();
            var calls = facts.Where(e => e.Kind == "call").Select((e, index) =>
            {
                var source = graph.Evidence.First(item => (e.EvidenceIds ?? []).Contains(item.Id));
                var resolved = graph.Edges.Where(edge => edge.FromIdentityId == method.IdentityId && edge.RevisionSha == method.RevisionSha &&
                    e.CallSiteId is not null && edge.EvidenceIds.Contains(e.CallSiteId)).ToArray();
                var target = resolved.Length == 1 && resolved[0].Confidence == Confidence.Exact ?
                    graph.Versions.FirstOrDefault(v => v.IdentityId == resolved[0].ToIdentityId && v.RevisionSha == method.RevisionSha)?.Id : null;
                return new CodeBlockCall(e.CallSiteId ?? e.Id, id, e.Expression, 0, Location(source), [], index + 1, target, Statement: e.Expression);
            }).ToArray();
            var ownEvidence = graph.Evidence.Where(e => e.RevisionSha == method.RevisionSha && e.FilePath == method.FilePath && e.StartLine == version.StartLine && e.EndLine == version.EndLine).ToArray();
            symbols.Add(new(id, method.FilePath, version.QualifiedName, "method", version.Signature,
                new(method.FilePath, version.StartLine, version.EndLine, 0, 0), ownEvidence.Select(e => e.Id).ToArray(), [], [], calls, [], [],
                Execution: method.Events));
        }
        foreach (var version in graph.Versions.Where(v => symbols.All(s => s.Id != v.Id) &&
            symbols.Any(s => s.Calls.Any(c => c.TargetSymbolId == v.Id))))
            symbols.Add(new(version.Id, version.FilePath, version.QualifiedName, "method", version.Signature,
                new(version.FilePath, version.StartLine, version.EndLine, 0, 0), [], [], [], [], [], []));
        var adapted = new CodeBlockGraph(symbols, [], evidence.DistinctBy(e => e.Id).ToArray(), [], [], SourceGraphAnalyzer.IndexVersion);
        var called = symbols.SelectMany(s => s.Calls).Select(c => c.TargetSymbolId).OfType<string>().ToHashSet();
        var diagrams = symbols.Where(s => s.Calls.Count > 0).OrderBy(s => called.Contains(s.Id))
            .Select(s => ExecutionSequenceProjection.Build(s, adapted, direction)).ToArray();
        var versions = graph.Versions.ToDictionary(v => v.Id);
        var ownerIds = diagrams.SelectMany(d => d.Nodes).DistinctBy(n => n.Id).ToDictionary(n => n.Id, n =>
            versions.TryGetValue(n.Id, out var version) && version.OwnerIdentityId is { } owner ?
                graph.Versions.FirstOrDefault(v => v.IdentityId == owner && v.RevisionSha == version.RevisionSha)?.Id ?? n.Id : n.Id);
        DiagramNode Participant(DiagramNode node)
        {
            var id = ownerIds[node.Id];
            return versions.TryGetValue(id, out var version) ? node with { Id = id, Label = version.QualifiedName,
                QualifiedName = version.QualifiedName } : node;
        }
        SequenceBlock Remap(SequenceBlock block) => block with { Children = block.Children.Select(Remap).ToArray(),
            ParticipantIds = block.ParticipantIds?.Select(id => ownerIds[id]).Distinct().ToArray() };
        DiagramEdge Event(DiagramEdge edge)
        {
            var original = graph.Edges.FirstOrDefault(e => e.Type == "calls" && e.EvidenceIds.Any(id => (edge.SourceFactIds ?? []).Contains(id)));
            return edge with { SourceId = ownerIds[edge.SourceId], TargetId = ownerIds[edge.TargetId],
                Context = original?.Context, ControlPath = original?.ControlPath,
                SourceFactIds = (edge.SourceFactIds ?? []).Concat(original is null ? [] : new[] { original.Id }).Distinct().ToArray() };
        }
        return new("sequence", title, diagrams.SelectMany(d => d.Nodes).Select(Participant).GroupBy(n => n.Id).Select(g => g.First() with
            { EvidenceIds = g.SelectMany(n => n.EvidenceIds).Distinct().ToArray(), SourceFactIds = g.SelectMany(n => n.SourceFactIds ?? []).Distinct().ToArray() }).ToArray(),
            diagrams.SelectMany(d => d.Edges).Select(Event).ToArray(), diagrams.SelectMany(d => d.Notes).Distinct().ToArray(),
            ["git", SourceGraphAnalyzer.IndexVersion, "execution-facts-v1"], direction, diagrams.SelectMany(d => d.SequenceBlocks ?? []).Select(Remap).ToArray());
    }
}
