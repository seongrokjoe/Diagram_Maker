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
        if (diagram.Type.Equals("code-relation", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("    classDef codeRelation font-weight:400");
            if (diagram.Nodes.Count > 0)
                builder.Append("    class ").Append(string.Join(',', diagram.Nodes.Select(node => aliases[node.Id])))
                    .AppendLine(" codeRelation");
        }
        if (diagram.Nodes.Any(static node => node.Shape is not null))
            builder.AppendLine("    linkStyle default stroke:#365f91,stroke-width:2px");
        foreach (var (edge, index) in diagram.Edges.Select(static (edge, index) => (edge, index)).Where(static item => item.edge.ChangeMarker is not null))
        {
            var (stroke, color) = MarkerColors(edge.ChangeMarker!.Kind);
            builder.Append("    linkStyle ").Append(index).Append(" stroke:").Append(stroke)
                .Append(",stroke-width:3px,color:").Append(color).Append('\n');
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

        var edgeMap = diagram.Edges.ToDictionary(edge => edge.Id);
        var blocks = diagram.SequenceBlocks ?? SequenceStructure.FromEdges(diagram.Edges.OrderBy(edge => edge.SequenceIndex ?? int.MaxValue).ToArray());
        foreach (var block in blocks) Append(block);
        return builder.ToString();

        void Append(SequenceBlock block)
        {
            // Keep the source-owned tree (and its evidence) intact. Mermaid cannot
            // lay out empty control fragments: their width collapses to zero.
            if (!SequenceStructure.HasVisibleContent(block)) return;
            if (block.Kind == "message")
            {
                if (block.EdgeId is null || !edgeMap.TryGetValue(block.EdgeId, out var edge))
                    throw new DiagramValidationException("Sequence block references an unknown event.");
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(edge.Type is "response" or "return" or "throw" || edge.IsIndirect ? "-->>" : "->>")
                    .Append(aliases[edge.TargetId]).Append(": ").Append(EscapeSequence(edge.IsIndirect ? $"간접 API: {edge.ViaApi} · {edge.Label}" : edge.Label)).Append('\n');
                return;
            }
            if (block.Kind is "note" or "scenario" or "unordered")
            {
                var participants = block.ParticipantIds?.Where(aliases.ContainsKey).ToArray() ?? diagram.Nodes.Take(2).Select(node => node.Id).ToArray();
                if (participants.Length > 0)
                    builder.Append("    Note over ").Append(aliases[participants[0]])
                        .Append(participants.Length > 1 ? "," + aliases[participants[^1]] : "")
                        .Append(": ").Append(EscapeSequence(block.Label)).Append('\n');
            }
            if (block.Kind == "alt")
            {
                var visible = block.Children.Where(SequenceStructure.HasVisibleContent).ToArray();
                if (block.Children.Count == 2 && visible.Length == 1 &&
                    block.Children[0].Label == "then" && block.Children[1].Label == "else")
                {
                    var condition = visible[0] == block.Children[0] ? block.Label : "!(" + block.Label + ")";
                    builder.Append("    opt ").Append(EscapeSequence(condition)).Append('\n');
                    foreach (var child in visible[0].Children) Append(child);
                    builder.AppendLine("    end");
                    return;
                }
                foreach (var (branch, index) in block.Children.Select((item, index) => (item, index)))
                {
                    var branchLabel = branch.Label switch { "then" => "참일 때", "else" => "거짓일 때", _ => branch.Label };
                    builder.Append(index == 0 ? "    alt " : "    else ")
                        .Append(EscapeSequence(index == 0 ? $"{block.Label}: {branchLabel}" : branchLabel)).Append('\n');
                    foreach (var child in branch.Children) Append(child);
                }
                if (block.Children.Count > 0) builder.AppendLine("    end");
                return;
            }
            if (block.Kind == "unordered")
            {
                foreach (var branch in block.Children.Where(SequenceStructure.HasVisibleContent))
                {
                    foreach (var child in branch.Children) Append(child);
                }
                return;
            }
            if (block.Kind is "loop" or "break" or "opt") builder.Append("    ").Append(block.Kind).Append(' ').Append(EscapeSequence(block.Label)).Append('\n');
            foreach (var child in block.Children) Append(child);
            if (block.Kind is "loop" or "break" or "opt") builder.AppendLine("    end");
        }
    }

    private static string CompileClass(DiagramIr diagram)
    {
        var direction = diagram.Direction?.Equals("TB", StringComparison.OrdinalIgnoreCase) == true ? "TB" : "LR";
        var builder = new StringBuilder($"classDiagram\n    direction {direction}\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            builder.Append("    class ").Append(aliases[node.Id]).Append("[\"").Append(EscapeClassNodeLabel(node.Label)).Append("\"]\n");
            foreach (var member in node.Details ?? [])
            {
                builder.Append("    ").Append(aliases[node.Id]).Append(" : ")
                    .Append(EscapeClassMember(member)).Append('\n');
            }
        }

        foreach (var edge in diagram.Edges)
        {
            if (edge.Type.Equals("inherits", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.TargetId]).Append(" <|-- ").Append(aliases[edge.SourceId]);
            else if (edge.Type.Equals("association", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(" --> ").Append(aliases[edge.TargetId]);
            else if (edge.Type.Equals("implements", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.TargetId]).Append(" <|.. ").Append(aliases[edge.SourceId]);
            else if (edge.Type.Equals("depends", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(" ..> ").Append(aliases[edge.TargetId]);
            else if (edge.Type.Equals("composition", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(" *-- ").Append(aliases[edge.TargetId]);
            else if (edge.Type.Equals("aggregation", StringComparison.OrdinalIgnoreCase))
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(" o-- ").Append(aliases[edge.TargetId]);
            else
                builder.Append("    ").Append(aliases[edge.SourceId]).Append(edge.IsIndirect ? " ..> " : " --> ").Append(aliases[edge.TargetId]);
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append(" : ").Append(EscapeClassRelationLabel(edge.Label));
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string CompileState(DiagramIr diagram)
    {
        var direction = diagram.Direction?.Equals("LR", StringComparison.OrdinalIgnoreCase) == true ? "LR" : "TB";
        var builder = new StringBuilder($"stateDiagram-v2\n    direction {direction}\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id,
            static node => node.Kind is "initial" or "final" ? "[*]" : Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            if (node.Kind is "initial" or "final") continue;
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
        builder.AppendLine("    classDef added fill:#dbeafe,stroke:#2563eb,stroke-width:3px,color:#1e3a8a");
        builder.AppendLine("    classDef modified fill:#dcfce7,stroke:#16a34a,stroke-width:3px,color:#14532d");
        builder.AppendLine("    classDef deleted fill:#fee2e2,stroke:#dc2626,stroke-width:3px,color:#991b1b");
        builder.AppendLine("    classDef unchanged fill:#f8fafc,stroke:#94a3b8,color:#334155");
        foreach (var node in diagram.Nodes)
        {
            var style = node.ChangeMarker?.Kind switch
            {
                DiagramChangeKind.Added => "added",
                DiagramChangeKind.Modified => "modified",
                DiagramChangeKind.Deleted => "deleted",
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

    private static string DisplayLabel(string value, DiagramChangeMarker? marker) => value;

    private static string Escape(string value) => value
        .Replace("%%", string.Empty, StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "'", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Trim();

    private static string EscapeClassNodeLabel(string value) => value
        .Replace("%%", string.Empty, StringComparison.Ordinal)
        .Replace("<", "‹", StringComparison.Ordinal)
        .Replace(">", "›", StringComparison.Ordinal)
        .Replace("\"", "'", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Trim();

    private static string EscapeClassRelationLabel(string value) => EscapeClassNodeLabel(value)
        .Replace(":", "∶", StringComparison.Ordinal)
        .Replace(";", "；", StringComparison.Ordinal);

    private static string EscapeClassMember(string value) => EscapeClassNodeLabel(value)
        .Replace(":", "∶", StringComparison.Ordinal)
        .Replace(";", string.Empty, StringComparison.Ordinal)
        .Replace("{", "‹", StringComparison.Ordinal)
        .Replace("}", "›", StringComparison.Ordinal);

    private static (string Stroke, string Color) MarkerColors(DiagramChangeKind kind) => kind switch
    {
        DiagramChangeKind.Added => ("#2563eb", "#1e3a8a"),
        DiagramChangeKind.Modified => ("#16a34a", "#14532d"),
        _ => ("#dc2626", "#991b1b")
    };

    private static string EscapeSequence(string value) => value
        .Replace("%%", string.Empty, StringComparison.Ordinal)
        .Replace(";", "；", StringComparison.Ordinal)
        .Replace("<", "‹", StringComparison.Ordinal)
        .Replace(">", "›", StringComparison.Ordinal)
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
