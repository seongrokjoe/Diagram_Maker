using System.Net;
using System.Text.RegularExpressions;
using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed partial class MermaidDslRevisionService(
    IAppStore store,
    DiagramValidator validator,
    MermaidCompiler compiler)
{
    private const int MaximumDslLength = 50_000;

    public async Task<NaturalDiagramRecord> SaveAsync(
        NaturalDiagramRecord parent,
        string mermaidDsl,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mermaidDsl)) throw new ArgumentException("Mermaid DSL is required.");
        if (mermaidDsl.Length > MaximumDslLength) throw new ArgumentException("Mermaid DSL exceeds the 50,000 character limit.");

        var ir = Parse(mermaidDsl, parent.Diagram.Ir);
        validator.Validate(ir);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var artifact = new DiagramArtifact(id, ir.Type, parent.Diagram.Version + 1, ir, compiler.Compile(ir), now);
        var views = parent.Views?.Select(view => view.Diagram?.Id == parent.Diagram.Id
            ? view with { Diagram = artifact, LastSuccessfulDiagram = artifact, State = "Completed", ErrorCode = null, ErrorMessage = null }
            : view).ToArray();
        var record = new NaturalDiagramRecord(
            id,
            parent.Request with { ParentDiagramId = parent.Id, ForceRegenerate = false },
            artifact,
            now,
            ownerUserId,
            parent.RootDiagramId ?? parent.Id,
            parent.Id,
            "manualDsl",
            NaturalDiagramService.GeneratorVersion,
            false,
            views,
            parent.Revision + 1);
        await store.SaveNaturalDiagramAsync(record, cancellationToken);
        return record;
    }

    public DiagramIr Parse(string mermaidDsl, DiagramIr parent)
    {
        EnsureSafeSource(mermaidDsl);
        var lines = mermaidDsl.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is 0 or > 600) throw new ArgumentException("Mermaid DSL has an invalid number of lines.");

        var type = ParseHeader(lines[0]);
        var direction = lines[0].Equals("flowchart TB", StringComparison.Ordinal) ? "TB" : parent.Direction;
        if (!string.Equals(type, parent.Type, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The diagram type cannot be changed from '{parent.Type}' to '{type}' in a revision.");

        var nodes = new List<DiagramNode>();
        var edges = new List<DiagramEdge>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (type == "flowchart" && (line.StartsWith("classDef ", StringComparison.Ordinal) || FlowStyle().IsMatch(line))) continue;
            if (type is "class" or "state" && line is "direction LR" or "direction TB")
            {
                direction = line.EndsWith("TB", StringComparison.Ordinal) ? "TB" : "LR";
                continue;
            }

            if (TryParseNode(type, line, out var alias, out var label))
            {
                var nodeId = DecodeAlias(alias);
                if (!aliases.TryAdd(alias, nodeId) || !nodeIds.Add(nodeId)) throw new ArgumentException($"Duplicate Mermaid node alias '{alias}'.");
                nodes.Add(new DiagramNode(nodeId, DecodeLabel(label), NodeKind(type), null, "unchanged", Confidence.Inferred, []));
                continue;
            }

            if (TryParseEdge(type, line, edges.Count + 1, out var edge))
            {
                edges.Add(edge);
                continue;
            }

            throw new ArgumentException($"Unsupported Mermaid statement on line {index + 1}.");
        }

        if (edges.Any(edge => !aliases.ContainsKey(edge.SourceId) || !aliases.ContainsKey(edge.TargetId)))
            throw new ArgumentException("A Mermaid edge references an unknown node.");
        edges = edges.Select(edge => edge with { SourceId = aliases[edge.SourceId], TargetId = aliases[edge.TargetId] }).ToList();

        return new DiagramIr(
            type,
            parent.Title,
            nodes,
            edges,
            parent.Notes.Concat(["Mermaid DSL에서 수동 편집됨."]).Distinct(StringComparer.Ordinal).ToArray(),
            parent.Provenance,
            direction);
    }

    private static void EnsureSafeSource(string source)
    {
        if (source.Contains("```", StringComparison.Ordinal) ||
            source.Contains("%%", StringComparison.Ordinal) ||
            source.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("click ", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Mermaid DSL contains a disallowed directive or external content.");
    }

    private static string ParseHeader(string header) => header switch
    {
        "sequenceDiagram" => "sequence",
        "classDiagram" => "class",
        "stateDiagram-v2" => "state",
        "flowchart LR" or "flowchart TB" => "flowchart",
        _ => throw new ArgumentException("Only flowchart LR/TB, sequenceDiagram, classDiagram, and stateDiagram-v2 are supported.")
    };

    private static bool TryParseNode(string type, string line, out string alias, out string label)
    {
        var match = type switch
        {
            "sequence" => SequenceNode().Match(line),
            "class" => ClassNode().Match(line),
            "state" => StateNode().Match(line),
            _ => FlowNode().Match(line)
        };
        alias = match.Success ? match.Groups["id"].Value : string.Empty;
        label = match.Success ? match.Groups["label"].Value : string.Empty;
        return match.Success;
    }

    private static bool TryParseEdge(string type, string line, int sequence, out DiagramEdge edge)
    {
        var match = type switch
        {
            "sequence" => SequenceEdge().Match(line),
            "class" => ClassEdge().Match(line),
            "state" => StateEdge().Match(line),
            _ => FlowEdge().Match(line)
        };
        if (!match.Success)
        {
            edge = null!;
            return false;
        }

        var left = match.Groups["left"].Value;
        var right = match.Groups["right"].Value;
        var label = DecodeLabel(match.Groups["label"].Value);
        var isClass = type == "class";
        var inherits = isClass && match.Groups["arrow"].Value == "<|--";
        var explicitEdgeId = match.Groups["edge"].Value;
        edge = new DiagramEdge(
            string.IsNullOrEmpty(explicitEdgeId) ? $"e{sequence}" : DecodeAlias(explicitEdgeId),
            isClass ? right : left,
            isClass ? left : right,
            inherits ? "inherits" : type switch { "sequence" => "message", "state" => "transition", "class" => "uses", _ => "flow" },
            label,
            "unchanged",
            Confidence.Inferred,
            [],
            type == "sequence" ? sequence : null);
        return true;
    }

    private static string DecodeLabel(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(decoded)) return string.Empty;
        return decoded.Length <= 240 ? decoded : decoded[..240];
    }

    private static string NodeKind(string type) => type == "sequence" ? "participant" : type == "class" ? "class" : type == "state" ? "state" : "component";
    private static string DecodeAlias(string alias) => alias.StartsWith("n_", StringComparison.Ordinal) && alias.Length > 2 ? alias[2..] : alias;

    private const string Alias = "(?<id>[A-Za-z][A-Za-z0-9_]{0,119})";

    [GeneratedRegex("^participant\\s+" + Alias + "\\s+as\\s+(?<label>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SequenceNode();
    [GeneratedRegex("^class\\s+" + Alias + "\\[\\\"(?<label>[^\\\"]+)\\\"\\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassNode();
    [GeneratedRegex("^state\\s+\\\"(?<label>[^\\\"]+)\\\"\\s+as\\s+" + Alias + "$", RegexOptions.CultureInvariant)]
    private static partial Regex StateNode();
    [GeneratedRegex("^" + Alias + "\\[\\\"(?<label>[^\\\"]+)\\\"\\]$", RegexOptions.CultureInvariant)]
    private static partial Regex FlowNode();
    [GeneratedRegex("^(?<left>[A-Za-z][A-Za-z0-9_]{0,119})->>(?<right>[A-Za-z][A-Za-z0-9_]{0,119}):\\s*(?<label>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SequenceEdge();
    [GeneratedRegex("^(?<left>[A-Za-z][A-Za-z0-9_]{0,119})\\s+(?<arrow><\\|--|-->)\\s+(?<right>[A-Za-z][A-Za-z0-9_]{0,119})(?:\\s*:\\s*(?<label>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ClassEdge();
    [GeneratedRegex("^(?<left>[A-Za-z][A-Za-z0-9_]{0,119})\\s+-->\\s+(?<right>[A-Za-z][A-Za-z0-9_]{0,119})(?:\\s*:\\s*(?<label>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex StateEdge();
    [GeneratedRegex("^(?<left>[A-Za-z][A-Za-z0-9_]{0,119})\\s+(?:(?<edge>[A-Za-z][A-Za-z0-9_]{0,119})@)?-->(?:\\|\\\"(?<label>[^\\\"]*)\\\"\\|)?\\s+(?<right>[A-Za-z][A-Za-z0-9_]{0,119})$", RegexOptions.CultureInvariant)]
    private static partial Regex FlowEdge();
    [GeneratedRegex("^class\\s+[A-Za-z][A-Za-z0-9_]{0,119}\\s+(added|modified|deleted|symbol|unchanged)$", RegexOptions.CultureInvariant)]
    private static partial Regex FlowStyle();
}
