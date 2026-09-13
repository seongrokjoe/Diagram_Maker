namespace DiagramMaker.Domain;

// A syntax region, not an enumerated execution path. A call site appears once,
// even when multiple paths reach it. Optional fields keep stored documents readable.
public sealed record ExecutionFact(string Id, string Kind, string Expression, int StartOffset, int EndOffset,
    IReadOnlyList<ExecutionFact> Children, IReadOnlyList<ExecutionFact> Alternative,
    IReadOnlyList<ExecutionFact> Evaluation, string? Variable = null, string? Value = null,
    string? TerminationTarget = null, string? CallSiteId = null, IReadOnlyList<string>? EvidenceIds = null);

public sealed record MethodExecution(string IdentityId, string RevisionSha, string FilePath,
    IReadOnlyList<ExecutionFact> Events, SourceSpan? Span = null);

public sealed record ExecutionMeaningStep(string Id, string Kind, string Expression, string? Value,
    IReadOnlyList<string> EvaluationIds, IReadOnlyList<string> ChildIds, IReadOnlyList<string> AlternativeIds,
    string? TerminationTarget = null);
public sealed record ExecutionMeaningUnit(string Summary, string Description, IReadOnlyList<string> EventIds);
public sealed record ExecutionMeaningPlan(string Summary, string Basis, IReadOnlyList<ExecutionMeaningStep> Steps,
    IReadOnlyList<ExecutionMeaningUnit> Units);
public sealed record ExecutionMeaningInput(string Id, string Name, SourceSpan Span, string Source,
    IReadOnlyList<ExecutionFact> Events);
public sealed record ExecutionMeaningResult(ExecutionMeaningInput Input, ExecutionMeaningPlan? Plan, string? FailureCode = null);
