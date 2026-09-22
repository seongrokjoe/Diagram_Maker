namespace DiagramMaker.Domain;

// Model-local keys are never persisted as diagram IDs. Requirement ownership is
// supplied by the server, not repeated on every model-generated element.
public sealed record NaturalConcept(string Key, string Label, string Kind, bool Assumption,
    string Shape = "", IReadOnlyList<NaturalMember>? Members = null, IReadOnlyList<string>? Details = null);
public sealed record NaturalConnection(string Source, string Target, string Type, string Label, bool Assumption,
    string Event = "", string Guard = "", string Action = "", IReadOnlyList<ControlScope>? ControlPath = null);
public sealed record NaturalSemanticUnit(IReadOnlyList<NaturalConcept> Concepts, IReadOnlyList<NaturalConnection> Connections);
public sealed record NaturalSemanticUnitResult(string RequirementId, NaturalSemanticUnit Meaning, bool RepairUsed);
public sealed record NaturalSemanticPlan(string Type, NaturalRequirements Requirements, IReadOnlyList<NaturalSemanticUnitResult> Units);

// Integration may connect existing concepts but cannot invent/replace them.
public sealed record NaturalIntegrationLink(string Source, string Target, string Type, string Label,
    IReadOnlyList<string> RequirementIds, bool Assumption, string Event = "", string Guard = "", string Action = "",
    IReadOnlyList<ControlScope>? ControlPath = null);
public sealed record NaturalIntegration(IReadOnlyList<NaturalIntegrationLink> Connections);
