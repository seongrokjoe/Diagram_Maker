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
            builder.Append("    ").Append(aliases[edge.SourceId]);
            if (dotted)
            {
                if (string.IsNullOrWhiteSpace(edge.Label)) builder.Append(" -.-> ");
                else builder.Append(" -. ").Append(EscapeDottedEdgeLabel(edge.Label)).Append(" .-> ");
            }
            else
            {
                builder.Append(" -->");
                if (!string.IsNullOrWhiteSpace(edge.Label))
                    builder.Append("|\"").Append(Escape(edge.Label)).Append("\"|");
                builder.Append(' ');
            }
            builder.Append(aliases[edge.TargetId]).Append('\n');
        }

        AppendStyles(builder, diagram, aliases);
        if (diagram.Nodes.Any(static node => node.Shape is not null))
            builder.AppendLine("    linkStyle default stroke:#365f91,stroke-width:2px");
        return builder.ToString();
    }

    private static void AppendFlowNode(StringBuilder builder, DiagramNode node, string alias, string indent)
    {
        var label = Escape(node.Label);
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
            builder.Append("    participant ").Append(aliases[node.Id]).Append(" as ").Append(EscapeSequence(node.Label)).Append('\n');
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
                .Append(aliases[edge.TargetId]).Append(": ").Append(EscapeSequence(edge.IsIndirect ? $"간접 API: {edge.ViaApi} · {edge.Label}" : edge.Label)).Append('\n');
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
            builder.Append("    class ").Append(aliases[node.Id]).Append("[\"").Append(Escape(node.Label)).Append("\"]\n");
        }

        foreach (var edge in diagram.Edges)
        {
            if (edge.Type.Equals("inherits", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.TargetId]).Append(" <|-- ").Append(aliases[edge.SourceId]);
            else
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(edge.IsIndirect ? " ..> " : " --> ").Append(aliases[edge.TargetId]);
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append(" : ").Append(Escape(edge.Label));
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
            builder.Append("    state \"").Append(Escape(node.Label)).Append("\" as ").Append(aliases[node.Id]).Append('\n');
        }

        foreach (var edge in diagram.Edges)
        {
            builder.Append("    ").Append(aliases[edge.SourceId]).Append(" --> ").Append(aliases[edge.TargetId]);
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append(" : ").Append(Escape(edge.Label));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static void AppendStyles(StringBuilder builder, DiagramIr diagram, IReadOnlyDictionary<string, string> aliases)
    {
        builder.AppendLine("    classDef added fill:#dcfce7,stroke:#16a34a,color:#14532d");
        builder.AppendLine("    classDef modified fill:#fef3c7,stroke:#d97706,color:#78350f");
        builder.AppendLine("    classDef deleted fill:#fee2e2,stroke:#dc2626,color:#7f1d1d");
        builder.AppendLine("    classDef unchanged fill:#eff6ff,stroke:#3b82f6,color:#1e3a8a");
        foreach (var node in diagram.Nodes)
        {
            var style = node.Status.ToLowerInvariant() switch
            {
                "added" => "added",
                "modified" => "modified",
                "deleted" => "deleted",
                _ => "unchanged"
            };
            builder.Append("    class ").Append(aliases[node.Id]).Append(' ').Append(style).Append('\n');
        }
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
