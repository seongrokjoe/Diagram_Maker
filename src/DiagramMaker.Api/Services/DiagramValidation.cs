using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed class DiagramValidationException(string message) : Exception(message);

public sealed class DiagramGenerationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class DiagramValidator
{
    public const int MaximumNodes = 500;
    public const int MaximumEdges = 500;
    public const int MaximumNodeLabelLength = 1000;

    public void Validate(DiagramIr diagram)
    {
        var failure = FindFailure(diagram);
        if (failure is not null) throw new DiagramValidationException(failure.Message);
    }

    public string? GetFailureKind(DiagramIr diagram) => FindFailure(diagram)?.Kind;

    private static ValidationFailure? FindFailure(DiagramIr diagram)
    {
        if (string.IsNullOrWhiteSpace(diagram.Title))
            return new ValidationFailure("MissingTitle", "Diagram title is required.");
        if (diagram.Nodes.Count == 0)
            return new ValidationFailure("NoNodes", "Diagram requires at least one node.");
        if (diagram.Nodes.Count > MaximumNodes || diagram.Edges.Count > MaximumEdges)
            return new ValidationFailure("TooManyItems", "Diagram exceeds the 500 node/edge safety limit.");

        var nodeIds = diagram.Nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (nodeIds.Count != diagram.Nodes.Count)
            return new ValidationFailure("DuplicateNodeId", "Diagram contains duplicate node IDs.");
        if (diagram.Nodes.Any(static node => string.IsNullOrWhiteSpace(node.Id) ||
                                             string.IsNullOrWhiteSpace(node.Label) ||
                                             node.Id.Length > 120 || node.Label.Length > MaximumNodeLabelLength))
            return new ValidationFailure("InvalidNode", "Diagram contains an invalid node.");
        if (diagram.Edges.Any(edge => string.IsNullOrWhiteSpace(edge.Id) || edge.Id.Length > 120 ||
                                      !nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId)))
            return new ValidationFailure("UnknownEdgeNode", "Diagram contains an invalid edge or an edge that references an unknown node.");

        return null;
    }

    private sealed record ValidationFailure(string Kind, string Message);
}
