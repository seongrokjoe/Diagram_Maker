using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed record NaturalRelation(string Source, string Target, string Label, string? Type = null);
public sealed record SequenceDiagramIntent(string Title, IReadOnlyList<string> Participants, IReadOnlyList<NaturalRelation> Messages, IReadOnlyList<string> Notes);
public sealed record FlowDiagramIntent(string Title, IReadOnlyList<string> Nodes, IReadOnlyList<NaturalRelation> Flows, IReadOnlyList<string> Notes);
public sealed record ClassDiagramIntent(string Title, IReadOnlyList<string> Classes, IReadOnlyList<NaturalRelation> Relations, IReadOnlyList<string> Notes);
public sealed record StateDiagramIntent(string Title, string InitialState, IReadOnlyList<string> States, IReadOnlyList<NaturalRelation> Transitions, IReadOnlyList<string> Notes);

public static partial class NaturalDiagramTypeResolver
{
    public static string Resolve(string requestedType, string prompt)
    {
        var requested = requestedType.Trim().ToLowerInvariant();
        if (requested is not "" and not "auto") return Normalize(requested);
        if (Contains(prompt, "sequence", "시퀀스", "호출 순서", "처리 순서", "메시지 흐름")) return "sequence";
        if (Contains(prompt, "class", "클래스", "상속", "타입 관계")) return "class";
        if (Contains(prompt, "state", "상태", "전이", "lifecycle", "라이프사이클")) return "state";
        return "flowchart";
    }

    public static string Normalize(string type) => type.Trim().ToLowerInvariant() switch
    {
        "flow" or "component" or "dependency" => "flowchart",
        "classdiagram" => "class",
        "statediagram" => "state",
        "sequencediagram" => "sequence",
        var value when value is "flowchart" or "class" or "state" or "sequence" => value,
        _ => throw new ArgumentException("DiagramType must be auto, flowchart, sequence, class, or state.")
    };

    private static bool Contains(string prompt, params string[] values) => values.Any(value => prompt.Contains(value, StringComparison.OrdinalIgnoreCase));
}

public static partial class NaturalDiagramIntentNormalizer
{
    public static DiagramIr Normalize(SequenceDiagramIntent intent) => Build(
        "sequence", intent.Title, OrderByAppearance(intent.Participants, intent.Messages), intent.Messages, intent.Notes, "message");

    public static DiagramIr Normalize(FlowDiagramIntent intent) => Build(
        "flowchart", intent.Title, OrderByAppearance(intent.Nodes, intent.Flows), intent.Flows, intent.Notes, "flow");

    public static DiagramIr Normalize(ClassDiagramIntent intent) => Build(
        "class", intent.Title, intent.Classes.Select(Clean).Where(NotEmpty).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        intent.Relations
            .Select(relation => relation with { Type = IsInheritance(relation.Type) ? "inherits" : "uses" })
            .OrderBy(relation => Clean(relation.Source), StringComparer.Ordinal)
            .ThenBy(relation => Clean(relation.Target), StringComparer.Ordinal)
            .ToArray(), intent.Notes, "uses");

    public static DiagramIr Normalize(StateDiagramIntent intent)
    {
        var initial = Clean(intent.InitialState);
        var states = intent.States.Select(Clean).Where(NotEmpty).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (NotEmpty(initial))
        {
            states.Remove(initial);
            states.Insert(0, initial);
        }
        return Build("state", intent.Title, states, intent.Transitions, intent.Notes, "transition");
    }

    public static string? Validate(SequenceDiagramIntent intent) => ValidateIntent(intent.Title, intent.Participants, intent.Messages, 12, 30);
    public static string? Validate(FlowDiagramIntent intent) => ValidateIntent(intent.Title, intent.Nodes, intent.Flows, 30, 50);
    public static string? Validate(ClassDiagramIntent intent) => ValidateIntent(intent.Title, intent.Classes, intent.Relations, 20, 40);
    public static string? Validate(StateDiagramIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.InitialState)) return "MissingInitialState";
        var failure = ValidateIntent(intent.Title, intent.States, intent.Transitions, 20, 40);
        if (failure is not null) return failure;
        return intent.States.Select(Clean).Contains(Clean(intent.InitialState), StringComparer.Ordinal) ? null : "UnknownInitialState";
    }

    private static string? ValidateIntent(string title, IReadOnlyList<string> nodes, IReadOnlyList<NaturalRelation> relations, int maxNodes, int maxRelations)
    {
        if (string.IsNullOrWhiteSpace(title)) return "MissingTitle";
        var names = nodes.Select(Clean).Where(NotEmpty).ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0) return "NoNodes";
        if (names.Count > maxNodes || relations.Count > maxRelations) return "TooManyItems";
        return relations.Any(relation => !names.Contains(Clean(relation.Source)) || !names.Contains(Clean(relation.Target)) || string.IsNullOrWhiteSpace(relation.Label))
            ? "UnknownRelationNode"
            : null;
    }

    private static DiagramIr Build(string type, string title, IEnumerable<string> rawNodes, IEnumerable<NaturalRelation> rawRelations, IEnumerable<string> notes, string defaultRelationType)
    {
        var labels = rawNodes.Select(Clean).Where(NotEmpty).Distinct(StringComparer.Ordinal).ToArray();
        var ids = labels.Select((label, index) => (label, id: type == "sequence" ? $"p{index + 1}" : $"n{index + 1}"))
            .ToDictionary(item => item.label, item => item.id, StringComparer.Ordinal);
        var nodes = labels.Select(label => new DiagramNode(ids[label], label, type == "sequence" ? "participant" : "component", null, "unchanged", Confidence.Inferred, [])).ToArray();
        var edges = rawRelations
            .Select(relation => new { Source = Clean(relation.Source), Target = Clean(relation.Target), Label = Clean(relation.Label), Type = Clean(relation.Type ?? defaultRelationType) })
            .Where(relation => ids.ContainsKey(relation.Source) && ids.ContainsKey(relation.Target) && NotEmpty(relation.Label))
            .DistinctBy(relation => $"{relation.Source}\0{relation.Target}\0{relation.Label}\0{relation.Type}")
            .Select((relation, index) => new DiagramEdge($"e{index + 1}", ids[relation.Source], ids[relation.Target], NotEmpty(relation.Type) ? relation.Type : defaultRelationType,
                relation.Label, "unchanged", Confidence.Inferred, [], type == "sequence" ? index + 1 : null))
            .ToArray();
        return new DiagramIr(type, Clean(title), nodes, edges, notes.Select(Clean).Where(NotEmpty).Distinct(StringComparer.Ordinal).Take(20).ToArray(), []);
    }

    private static IReadOnlyList<string> OrderByAppearance(IReadOnlyList<string> rawNodes, IReadOnlyList<NaturalRelation> relations)
    {
        var allowed = rawNodes.Select(Clean).Where(NotEmpty).ToHashSet(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var relation in relations)
        {
            foreach (var candidate in new[] { Clean(relation.Source), Clean(relation.Target) })
                if (allowed.Contains(candidate) && !ordered.Contains(candidate, StringComparer.Ordinal)) ordered.Add(candidate);
        }
        ordered.AddRange(allowed.Where(candidate => !ordered.Contains(candidate, StringComparer.Ordinal)).Order(StringComparer.Ordinal));
        return ordered;
    }

    private static string Clean(string value)
    {
        var clean = Whitespace().Replace(value ?? string.Empty, " ").Trim();
        return clean.Length <= 80 ? clean : clean[..80];
    }
    private static bool NotEmpty(string value) => !string.IsNullOrWhiteSpace(value);
    private static bool IsInheritance(string? value)
    {
        var clean = Clean(value ?? string.Empty);
        return clean.Contains("inherit", StringComparison.OrdinalIgnoreCase) || clean.Contains("상속", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
