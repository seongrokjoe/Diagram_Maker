namespace DiagramMaker.Domain;

// Source facts and topology remain server-owned. The model supplies one semantic
// annotation for each shared item, regardless of how many pages display it.
public sealed record SharedSemanticItem(string Id, string Kind, string Label,
    IReadOnlyList<string> FactIds, IReadOnlyList<CodeContext> Contexts,
    IReadOnlyList<string> Details, IReadOnlyList<string> ChangeIds, SourceSpan? Location = null,
    string? RefinementInstruction = null);
public sealed record SharedSemanticAnnotation(string Id, string Summary, string Description);
public sealed record SharedSemanticResponse(string Summary, string RecommendedType,
    IReadOnlyList<SharedSemanticAnnotation> Items,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SharedSemanticFailure>? Failures = null);
public sealed record SharedSemanticFailure(IReadOnlyList<string> ItemIds, string Stage, string Code,
    string? Category = null);
public sealed record SharedDiagramInput(string Key, DiagramIr Diagram, DiagramViewSelection Selection);
public sealed record SharedDiagramGroup(string Summary, string RecommendedType,
    IReadOnlyDictionary<string, SemanticGeneration> Pages, ChangeUnderstanding? Understanding = null);
