using System.Text;
using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class MermaidCompiler(DiagramValidator validator)
{
    public string Compile(DiagramIr diagram)
    {
        validator.Validate(diagram);

        return diagram.Type.ToLowerInvariant() switch
        {
            "sequence" => CompileSequence(diagram),
            "class" => CompileClass(diagram),
            "state" => CompileState(diagram),
            _ => CompileFlowchart(diagram)
        };
    }

    private static string CompileFlowchart(DiagramIr diagram)
    {
        var direction = diagram.Direction?.Equals("TB", StringComparison.OrdinalIgnoreCase) == true ? "TB" : "LR";
        var builder = new StringBuilder($"flowchart {direction}\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));

        var grouped = diagram.Nodes.Where(static node => !string.IsNullOrWhiteSpace(node.Group))
            .GroupBy(static node => node.Group!, StringComparer.Ordinal).ToArray();
        var groupedIds = grouped.SelectMany(static group => group).Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in grouped)
        {
            builder.Append("    subgraph g_").Append(Alias(group.Key)).Append("[\"").Append(Escape(group.Key)).Append("\"]\n");
            builder.Append("        direction ").Append(direction).Append('\n');
            foreach (var node in group) AppendFlowNode(builder, node, aliases[node.Id], "        ");
            builder.AppendLine("    end");
        }
        foreach (var node in diagram.Nodes.Where(node => !groupedIds.Contains(node.Id)))
        {
            AppendFlowNode(builder, node, aliases[node.Id], "    ");
        }

        foreach (var edge in diagram.Edges)
        {
            var dotted = edge.IsIndirect || edge.Type.Equals("loopBack", StringComparison.OrdinalIgnoreCase);
            builder.Append("    ").Append(aliases[edge.SourceId]).Append(' ').Append(Alias(edge.Id)).Append('@');
            if (dotted)
            {
                var label = DisplayLabel(edge.Label, edge.ChangeMarker);
                if (string.IsNullOrWhiteSpace(label)) builder.Append("-.-> ");
                else builder.Append("-. ").Append(EscapeDottedEdgeLabel(label)).Append(" .-> ");
            }
            else
            {
                builder.Append("-->");
                var label = DisplayLabel(edge.Label, edge.ChangeMarker);
                if (!string.IsNullOrWhiteSpace(label))
                    builder.Append("|\"").Append(Escape(label)).Append("\"|");
                builder.Append(' ');
            }
            builder.Append(aliases[edge.TargetId]).Append('\n');
        }

        AppendStyles(builder, diagram, aliases);
        if (diagram.Nodes.Any(static node => node.Shape is not null))
            builder.AppendLine("    linkStyle default stroke:#365f91,stroke-width:2px");
        foreach (var (edge, index) in diagram.Edges.Select(static (edge, index) => (edge, index)).Where(static item => item.edge.ChangeMarker is not null))
        {
            var dash = edge.ChangeMarker!.Kind == DiagramChangeKind.Deleted || edge.ChangeMarker.Precision == DiagramChangePrecision.Symbol
                ? ",stroke-dasharray:6 4" : string.Empty;
            builder.Append("    linkStyle ").Append(index).Append(" stroke:#dc2626,stroke-width:3px,color:#991b1b")
                .Append(dash).Append('\n');
        }
        return builder.ToString();
    }

    private static void AppendFlowNode(StringBuilder builder, DiagramNode node, string alias, string indent)
    {
        var label = Escape(DisplayLabel(node.Label, node.ChangeMarker));
        builder.Append(indent).Append(alias);
        builder.Append(node.Shape?.ToLowerInvariant() switch
        {
            "terminal" => $"([\"{label}\"])",
            "decision" => $"{{\"{label}\"}}",
            "call" => $"[[\"{label}\"]]",
            "return" => $"([\"{label}\"])",
            "type" => $"[\"클래스\\n{label}\"]",
            "method" => $"[\"메서드\\n{label}\"]",
            _ => $"[\"{label}\"]"
        }).Append('\n');
    }

    private static string CompileSequence(DiagramIr diagram)
    {
        var builder = new StringBuilder("sequenceDiagram\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            builder.Append("    participant ").Append(aliases[node.Id]).Append(" as ").Append(EscapeSequence(DisplayLabel(node.Label, node.ChangeMarker))).Append('\n');
        }

        foreach (var edge in diagram.Edges.OrderBy(static edge => edge.SequenceIndex ?? int.MaxValue))
        {
            var scopes = edge.ControlPath ?? [];
            foreach (var scope in scopes)
            {
                if (scope.Kind.Equals("loop", StringComparison.OrdinalIgnoreCase))
                    builder.Append("    loop ").Append(EscapeSequence(scope.Label)).Append('\n');
                else
                {
                    builder.Append("    alt ").Append(EscapeSequence(scope.Label)).Append('\n');
                    if (scope.Branch.Equals("else", StringComparison.OrdinalIgnoreCase)) builder.AppendLine("    else 그 외");
                }
            }
            var advanced = edge.ControlPath is not null;
            builder.Append("    ").Append(aliases[edge.SourceId]).Append(edge.IsIndirect ? "-->>+" : advanced ? "->>+" : "->>")
                .Append(aliases[edge.TargetId]).Append(": ").Append(EscapeSequence(DisplayLabel(edge.IsIndirect ? $"간접 API: {edge.ViaApi} · {edge.Label}" : edge.Label, edge.ChangeMarker))).Append('\n');
            if (advanced)
                builder.Append("    ").Append(aliases[edge.TargetId]).Append("-->>-")
                    .Append(aliases[edge.SourceId]).AppendLine(": return");
            for (var index = scopes.Count - 1; index >= 0; index--) builder.AppendLine("    end");
        }

        return builder.ToString();
    }

    private static string CompileClass(DiagramIr diagram)
    {
        var direction = diagram.Direction?.Equals("TB", StringComparison.OrdinalIgnoreCase) == true ? "TB" : "LR";
        var builder = new StringBuilder($"classDiagram\n    direction {direction}\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            builder.Append("    class ").Append(aliases[node.Id]).Append("[\"").Append(Escape(DisplayLabel(node.Label, node.ChangeMarker))).Append("\"]\n");
        }

        foreach (var edge in diagram.Edges)
        {
            if (edge.Type.Equals("inherits", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.TargetId]).Append(" <|-- ").Append(aliases[edge.SourceId]);
            else
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(edge.IsIndirect ? " ..> " : " --> ").Append(aliases[edge.TargetId]);
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append(" : ").Append(Escape(DisplayLabel(edge.Label, edge.ChangeMarker)));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string CompileState(DiagramIr diagram)
    {
        var direction = diagram.Direction?.Equals("LR", StringComparison.OrdinalIgnoreCase) == true ? "LR" : "TB";
        var builder = new StringBuilder($"stateDiagram-v2\n    direction {direction}\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            builder.Append("    state \"").Append(Escape(DisplayLabel(node.Label, node.ChangeMarker))).Append("\" as ").Append(aliases[node.Id]).Append('\n');
        }

        foreach (var edge in diagram.Edges)
        {
            builder.Append("    ").Append(aliases[edge.SourceId]).Append(" --> ").Append(aliases[edge.TargetId]);
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append(" : ").Append(Escape(DisplayLabel(edge.Label, edge.ChangeMarker)));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendStyles(StringBuilder builder, DiagramIr diagram, IReadOnlyDictionary<string, string> aliases)
    {
        var markerAware = diagram.Nodes.Any(static node => node.ChangeMarker is not null) || diagram.Edges.Any(static edge => edge.ChangeMarker is not null);
        builder.AppendLine(markerAware
            ? "    classDef added fill:#fee2e2,stroke:#dc2626,stroke-width:3px,color:#991b1b"
            : "    classDef added fill:#dcfce7,stroke:#16a34a,color:#14532d");
        builder.AppendLine(markerAware
            ? "    classDef modified fill:#fee2e2,stroke:#dc2626,stroke-width:3px,color:#991b1b"
            : "    classDef modified fill:#fef3c7,stroke:#d97706,color:#78350f");
        builder.AppendLine("    classDef deleted fill:#fee2e2,stroke:#dc2626,stroke-width:3px,stroke-dasharray:6 4,color:#991b1b");
        builder.AppendLine("    classDef symbol fill:#fff1f2,stroke:#dc2626,stroke-width:3px,stroke-dasharray:5 3,color:#991b1b");
        builder.AppendLine("    classDef unchanged fill:#eff6ff,stroke:#3b82f6,color:#1e3a8a");
        foreach (var node in diagram.Nodes)
        {
            var style = node.ChangeMarker?.Precision == DiagramChangePrecision.Symbol ? "symbol" : node.ChangeMarker?.Kind switch
            {
                DiagramChangeKind.Added => "added",
                DiagramChangeKind.Modified => "modified",
                DiagramChangeKind.Deleted => "deleted",
                _ when markerAware => "unchanged",
                _ => node.Status.ToLowerInvariant() switch
                {
                    "added" => "added",
                    "modified" => "modified",
                    "deleted" => "deleted",
                    _ => "unchanged"
                }
            };
            builder.Append("    class ").Append(aliases[node.Id]).Append(' ').Append(style).Append('\n');
        }
    }

    private static string DisplayLabel(string value, DiagramChangeMarker? marker) => marker is null
        ? value
        : $"{value} · {MarkerBadge(marker)}";

    private static string MarkerBadge(DiagramChangeMarker marker)
    {
        var kind = marker.Kind switch
        {
            DiagramChangeKind.Added => "+ 추가",
            DiagramChangeKind.Modified => "~ 수정",
            _ => "− 삭제"
        };
        return marker.Precision == DiagramChangePrecision.Symbol ? $"{kind} · 심볼 수준" : kind;
    }

    private static string Escape(string value) => value
        .Replace("%%", string.Empty, StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "'", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();

    private static string EscapeSequence(string value) => value
        .Replace("%%", string.Empty, StringComparison.Ordinal)
        .Replace("\"", "'", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();

    private static string EscapeDottedEdgeLabel(string value) => Escape(value)
        .Replace(".", "·", StringComparison.Ordinal)
        .Replace("-", "–", StringComparison.Ordinal);

    private static string Alias(string id) => "n_" + InvalidAliasCharacters().Replace(id, "_");

    [GeneratedRegex("[^a-zA-Z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidAliasCharacters();
}
