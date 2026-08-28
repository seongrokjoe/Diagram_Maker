using System.Text.Json.Serialization;

namespace DiagramMaker.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisState
{
    Queued,
    Resolving,
    Indexing,
    Diffing,
    Graphing,
    Summarizing,
    Rendering,
    Completed,
    Partial,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind
{
    Added,
    Deleted,
    Modified,
    Renamed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SymbolChangeKind
{
    AddSymbol,
    RemoveSymbol,
    ModifyBody,
    ChangeSignature,
    MoveRename,
    ChangeDependency
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Confidence
{
    Exact,
    Inferred
}

public sealed record RepositoryDefinition(
    Guid Id,
    string Name,
    string LocalPath,
    string DefaultBranch,
    IReadOnlyList<string> AllowedRoles,
    DateTimeOffset CreatedAt);

public sealed record RegisterRepositoryRequest(
    string Name,
    string LocalPath,
    string? DefaultBranch,
    IReadOnlyList<string>? AllowedRoles);

public sealed record InspectRepositoryRequest(string LocalPath);

public sealed record GitRepositoryInspection(
    string NormalizedPath,
    bool IsBare,
    string DefaultBranch,
    string HeadSha,
    string HeadMessage,
    IReadOnlyList<string> Branches);

public sealed record AnalyzeRequest(
    Guid RepositoryId,
    string BaseRevision,
    string TargetRevision,
    string CompareMode = "direct",
    IReadOnlyList<string>? DiagramTypes = null,
    int CallerDepth = 2,
    int CalleeDepth = 1,
    bool IncludeLlmSummary = true,
    bool EnableThinking = false);

public sealed record AnalysisJob(
    Guid Id,
    AnalyzeRequest Request,
    AnalysisState State,
    string? BaseSha,
    string? TargetSha,
    int Progress,
    string StageMessage,
    AnalysisResult? Result,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LeaseUntil);

public sealed record ChangedFile(
    string Path,
    string? PreviousPath,
    ChangeKind ChangeKind,
    string? BeforeBlobOid,
    string? AfterBlobOid,
    IReadOnlyList<DiffHunk> Hunks,
    string? BeforeContent = null,
    string? AfterContent = null);

public sealed record DiffHunk(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    string Header);

public sealed record GitComparison(
    string BaseSha,
    string TargetSha,
    IReadOnlyList<ChangedFile> Files);

public sealed record SymbolIdentity(
    string Id,
    Guid RepositoryId,
    string Language,
    string Kind,
    string SemanticKey);

public sealed record SymbolVersion(
    string Id,
    string IdentityId,
    string RevisionSha,
    string QualifiedName,
    string Signature,
    string FilePath,
    int StartLine,
    int EndLine,
    string ContentFingerprint);

public sealed record EvidenceRef(
    string Id,
    string RevisionSha,
    string BlobOid,
    string FilePath,
    int StartLine,
    int EndLine,
    string Analyzer,
    Confidence Confidence);

public sealed record GraphEdge(
    string Id,
    string FromIdentityId,
    string ToIdentityId,
    string Type,
    string Label,
    Confidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    int? SequenceIndex = null);

public sealed record SymbolChange(
    string Id,
    SymbolChangeKind Type,
    string? BeforeSymbolVersionId,
    string? AfterSymbolVersionId,
    Confidence ContinuityConfidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record VersionedGraph(
    IReadOnlyList<SymbolIdentity> Identities,
    IReadOnlyList<SymbolVersion> Versions,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<EvidenceRef> Evidence,
    IReadOnlyList<SymbolChange> Changes);

public sealed record DiagramNode(
    string Id,
    string Label,
    string Kind,
    string? Group,
    string Status,
    Confidence Confidence,
    IReadOnlyList<string> EvidenceIds);

public sealed record DiagramEdge(
    string Id,
    string SourceId,
    string TargetId,
    string Type,
    string Label,
    string Status,
    Confidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    int? SequenceIndex = null);

public sealed record DiagramIr(
    string Type,
    string Title,
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Provenance);

public sealed record DiagramArtifact(
    Guid Id,
    string Type,
    int Version,
    DiagramIr Ir,
    string MermaidDsl,
    DateTimeOffset CreatedAt);

public sealed record RiskItem(
    string Severity,
    string Text,
    IReadOnlyList<string> EvidenceIds);

public sealed record ReviewNarrative(
    string Summary,
    string Intent,
    IReadOnlyList<RiskItem> Risks,
    IReadOnlyList<string> Warnings);

public sealed record AnalysisResult(
    IReadOnlyList<ChangedFile> ChangedFiles,
    VersionedGraph Graph,
    ReviewNarrative Narrative,
    IReadOnlyList<DiagramArtifact> Diagrams);

public sealed record NaturalDiagramRequest(
    string Prompt,
    string DiagramType = "auto",
    Guid? ParentDiagramId = null,
    bool EnableThinking = false);

public sealed record NaturalDiagramRecord(
    Guid Id,
    NaturalDiagramRequest Request,
    DiagramArtifact Diagram,
    DateTimeOffset CreatedAt);

public sealed record AuditEvent(
    Guid Id,
    string UserId,
    string Action,
    Guid? RepositoryId,
    string Outcome,
    DateTimeOffset CreatedAt);

public sealed record LlmConnectionTestResult(
    bool Success,
    long ElapsedMilliseconds,
    string FinishReason,
    int ResponseCharacters,
    int RequestedMaxOutputTokens,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);

public sealed record LlmContractTestResult(
    bool Success,
    int NodeCount,
    int EdgeCount,
    long ElapsedMilliseconds,
    string FinishReason,
    bool StructuredOutputApplied,
    bool StructuredOutputFallbackUsed,
    bool RepairUsed,
    bool ThinkingEnabled,
    int RequestedMaxOutputTokens,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);
