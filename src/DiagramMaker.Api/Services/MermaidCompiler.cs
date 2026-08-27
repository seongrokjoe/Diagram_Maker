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
        var builder = new StringBuilder("flowchart LR\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));

        foreach (var node in diagram.Nodes)
        {
            builder.Append("    ").Append(aliases[node.Id]).Append("[\"").Append(Escape(node.Label)).Append("\"]\n");
        }

        foreach (var edge in diagram.Edges)
        {
            builder.Append("    ").Append(aliases[edge.SourceId]).Append(" -->");
            if (!string.IsNullOrWhiteSpace(edge.Label))
            {
                builder.Append("|\"").Append(Escape(edge.Label)).Append("\"|");
            }

            builder.Append(' ').Append(aliases[edge.TargetId]).Append('\n');
        }

        AppendStyles(builder, diagram, aliases);
        return builder.ToString();
    }

    private static string CompileSequence(DiagramIr diagram)
    {
        var builder = new StringBuilder("sequenceDiagram\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            builder.Append("    participant ").Append(aliases[node.Id]).Append(" as ").Append(Escape(node.Label)).Append('\n');
        }

        foreach (var edge in diagram.Edges.OrderBy(static edge => edge.SequenceIndex ?? int.MaxValue))
        {
            builder.Append("    ").Append(aliases[edge.SourceId]).Append("->>")
                .Append(aliases[edge.TargetId]).Append(": ").Append(Escape(edge.Label)).Append('\n');
        }

        return builder.ToString();
    }

    private static string CompileClass(DiagramIr diagram)
    {
        var builder = new StringBuilder("classDiagram\n");
        var aliases = diagram.Nodes.ToDictionary(static node => node.Id, static node => Alias(node.Id));
        foreach (var node in diagram.Nodes)
        {
            builder.Append("    class ").Append(aliases[node.Id]).Append("[\"").Append(Escape(node.Label)).Append("\"]\n");
        }

        foreach (var edge in diagram.Edges)
        {
            var arrow = edge.Type.Equals("inherits", StringComparison.OrdinalIgnoreCase) ? " <|-- " : " --> ";
            builder.Append("    ").Append(aliases[edge.TargetId]).Append(arrow).Append(aliases[edge.SourceId]);
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
        var builder = new StringBuilder("stateDiagram-v2\n");
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

    private static string Alias(string id) => "n_" + InvalidAliasCharacters().Replace(id, "_");

    [GeneratedRegex("[^a-zA-Z0-9_]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidAliasCharacters();
}
