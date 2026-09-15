using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class SequenceStructure
{
    public static bool HasVisibleContent(SequenceBlock block) => block.Kind is "message" or "note" ||
        block.Children.Any(HasVisibleContent);

    public static IEnumerable<SequenceBlock> AnnotatedBlocks(IEnumerable<SequenceBlock> blocks) => blocks.SelectMany(b =>
        (b.Kind is "alt" or "loop" or "break" or "opt" or "note" ? new[] { b } : []).Concat(AnnotatedBlocks(b.Children)));
    public static IEnumerable<string> MessageIds(IEnumerable<SequenceBlock> blocks) => blocks.SelectMany(block =>
        (block.Kind == "message" && block.EdgeId is not null ? new[] { block.EdgeId } : []).Concat(MessageIds(block.Children)));

    public static string? Validate(IReadOnlyList<SequenceBlock> blocks, IReadOnlySet<string> edgeIds, IReadOnlySet<string> nodeIds)
    {
        var ids = new HashSet<string>();
        var messages = new HashSet<string>();
        string? Visit(IReadOnlyList<SequenceBlock> items, int depth)
        {
            if (depth > 64) return "Sequence nesting exceeds the safety limit.";
            foreach (var block in items)
            {
                if (string.IsNullOrWhiteSpace(block.Id) || !ids.Add(block.Id) || block.Children is null ||
                    block.Kind is not ("message" or "sequence" or "scenario" or "alt" or "branch" or "loop" or "note" or "unordered" or "break" or "opt"))
                    return "Invalid or duplicate sequence block.";
                if (block.ParticipantIds?.Any(id => !nodeIds.Contains(id)) == true) return "Unknown note participant.";
                if (block.Kind == "message" && (block.EdgeId is null || !edgeIds.Contains(block.EdgeId) ||
                    !messages.Add(block.EdgeId) || block.Children.Count > 0)) return "Unknown or duplicate message.";
                if (block.Kind != "message" && block.EdgeId is not null) return "Only messages can reference events.";
                if (block.Kind == "alt" && block.Children.Any(child => child.Kind != "branch")) return "Alternative requires branch blocks.";
                var failure = Visit(block.Children, depth + 1);
                if (failure is not null) return failure;
            }
            return null;
        }
        return Visit(blocks, 0) ?? (messages.SetEquals(edgeIds) ? null : "Sequence tree omits one or more events.");
    }

    public static IReadOnlyList<SequenceBlock> ApplyEdit(IReadOnlyList<SequenceBlock> blocks, IReadOnlyList<DiagramEdge> edges,
        IReadOnlySet<string> nodeIds)
    {
        var retained = Prune(blocks, edges.Select(edge => edge.Id).ToHashSet(), nodeIds);
        var known = MessageIds(retained).ToHashSet();
        // New manual events have no source control scope. Keep them explicitly
        // separate instead of silently dropping them or inventing a branch.
        var added = edges.Where(edge => !known.Contains(edge.Id)).ToArray();
        return added.Length == 0 ? retained : retained.Append(new SequenceBlock(
            StableIds.Create("manual-scenario", string.Join("|", added.Select(edge => edge.Id))),
            "scenario", "사용자가 추가한 호출 (원본 제어 흐름과 별도)", FromEdges(added))).ToArray();
    }

    public static IReadOnlyList<SequenceBlock> FromEdges(IReadOnlyList<DiagramEdge> edges)
    {
        return Build(edges, 0);
        static IReadOnlyList<SequenceBlock> Build(IReadOnlyList<DiagramEdge> values, int depth)
        {
            var blocks = new List<SequenceBlock>();
            for (var index = 0; index < values.Count;)
            {
                var edge = values[index];
                var scope = edge.ControlPath?.ElementAtOrDefault(depth);
                if (scope is null)
                {
                    blocks.Add(new SequenceBlock(edge.Id, "message", edge.Label, [], edge.Id));
                    index++;
                    continue;
                }
                var end = index + 1;
                while (end < values.Count && values[end].ControlPath?.ElementAtOrDefault(depth)?.Id == scope.Id) end++;
                var region = values.Skip(index).Take(end - index).ToArray();
                var children = new List<SequenceBlock>();
                foreach (var branch in region.GroupBy(item => item.ControlPath![depth].Branch))
                    children.Add(new SequenceBlock(StableIds.Create(scope.Id, branch.Key, edge.Id), "branch", branch.Key,
                        Build(branch.ToArray(), depth + 1)));
                blocks.Add(new SequenceBlock(StableIds.Create(scope.Id, edge.Id), scope.Kind, scope.Label, children));
                index = end;
            }
            return blocks;
        }
    }

    public static IReadOnlyList<SequenceBlock> Prune(IReadOnlyList<SequenceBlock> blocks, IReadOnlySet<string> edgeIds,
        IReadOnlySet<string> nodeIds) => blocks.Select(block => block with
        {
            Children = Prune(block.Children, edgeIds, nodeIds),
            ParticipantIds = block.ParticipantIds?.Where(nodeIds.Contains).ToArray()
        }).Where(block => block.Kind == "message" ? block.EdgeId is not null && edgeIds.Contains(block.EdgeId) :
            block.Children.Count > 0 || block.Kind == "note" && block.ParticipantIds is { Count: > 0 }).ToArray();
}
