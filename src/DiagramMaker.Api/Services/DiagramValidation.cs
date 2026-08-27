using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed class DiagramValidationException(string message) : Exception(message);

public sealed class DiagramValidator
{
    public const int MaximumNodes = 500;
    public const int MaximumEdges = 500;

    public void Validate(DiagramIr diagram)
    {
        if (string.IsNullOrWhiteSpace(diagram.Title))
        {
            throw new DiagramValidationException("Diagram title is required.");
        }

        if (diagram.Nodes.Count > MaximumNodes || diagram.Edges.Count > MaximumEdges)
        {
            throw new DiagramValidationException("Diagram exceeds the 500 node/edge safety limit.");
        }

        var nodeIds = diagram.Nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (nodeIds.Count != diagram.Nodes.Count)
        {
            throw new DiagramValidationException("Diagram contains duplicate node IDs.");
        }

        foreach (var node in diagram.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Label.Length > 240)
            {
                throw new DiagramValidationException("Diagram contains an invalid node.");
            }
        }

        foreach (var edge in diagram.Edges)
        {
            if (!nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
            {
                throw new DiagramValidationException($"Edge '{edge.Id}' references an unknown node.");
            }
        }
    }
}
