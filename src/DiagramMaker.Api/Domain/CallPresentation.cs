namespace DiagramMaker.Domain;

public sealed record CallPresentation(string Target, IReadOnlyList<string> Arguments, string? AssignedTo,
    string? ReturnType, IReadOnlyList<CallOutput> Outputs, string Basis, string? ContractUrl = null);
public sealed record CallOutput(string Expression, string Mode, string Description, string Basis, IReadOnlyList<string> EvidenceIds);
