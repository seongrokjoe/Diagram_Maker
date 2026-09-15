namespace DiagramMaker.Domain;

public sealed record NaturalRequirement(string Id, string Text, string Kind, string Origin, string SourceQuote);
public sealed record NaturalRequirements(string Title, IReadOnlyList<string> Entities, IReadOnlyList<NaturalRequirement> Requirements);
public sealed record NaturalParameter(string Name, string Type);
public sealed record NaturalMember(string Name, string Kind, string Visibility, string Type,
    IReadOnlyList<NaturalParameter> Parameters, IReadOnlyList<string> Preconditions);
public sealed record NaturalDesignNode(string Id, string Label, string Kind, string Shape,
    IReadOnlyList<NaturalMember> Members, IReadOnlyList<string> Details, IReadOnlyList<string> RequirementIds, bool Assumption);
public sealed record NaturalDesignEdge(string Id, string SourceId, string TargetId, string Type, string Label,
    string Event, string Guard, string Action, IReadOnlyList<ControlScope> ControlPath,
    IReadOnlyList<string> RequirementIds, bool Assumption);
public sealed record NaturalDesign(string Title, IReadOnlyList<NaturalDesignNode> Nodes,
    IReadOnlyList<NaturalDesignEdge> Edges, IReadOnlyList<string> Notes);
public sealed record NaturalDesignReview(bool Accepted, IReadOnlyList<string> ReviewedRequirementIds, IReadOnlyList<string> Issues);
public sealed record NaturalDesignQuality(string Protocol, string Status, IReadOnlyList<string> ReviewedRequirementIds,
    IReadOnlyList<string> AssumptionElementIds, bool RepairUsed,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? ElementRequirements = null);
public sealed record NaturalDesignedDiagram(DiagramIr Diagram, NaturalDesignQuality? Quality = null);
