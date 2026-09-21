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
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisPlanState
{
    Queued,
    Indexing,
    Grouping,
    Ready,
    Failed,
    Expired
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagramChangeKind
{
    Added,
    Modified,
    Deleted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagramChangePrecision
{
    Exact,
    Symbol
}

public sealed record RepositoryDefinition(
    Guid Id,
    string Name,
    string LocalPath,
    string DefaultBranch,
    IReadOnlyList<string> AllowedRoles,
    DateTimeOffset CreatedAt,
    RepositoryAnalysisRules? AnalysisRules = null);

public sealed record RepositoryAnalysisRules(
    int Revision,
    IReadOnlyList<IndirectCallRule> IndirectCalls);

public sealed record IndirectCallRule(
    string Id,
    string Name,
    bool Enabled,
    string ApiName,
    int TargetTypeArgumentIndex,
    int? TargetMethodArgumentIndex,
    IReadOnlyList<IndirectCallAlias> Aliases);

public sealed record IndirectCallAlias(string Expression, string TargetType);

public sealed record UpdateRepositoryAnalysisRulesRequest(
    int ExpectedRevision,
    IReadOnlyList<IndirectCallRule> IndirectCalls);

public sealed record RegisterRepositoryRequest(
    string Name,
    string LocalPath,
    string? DefaultBranch,
    IReadOnlyList<string>? AllowedRoles);

public sealed record InspectRepositoryRequest(string LocalPath);

public sealed record SampleGenerationMetadata(string Provider, string ScenarioId, string SampleVersion, string RefinementId);

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
    int CallerDepth = 1,
    int CalleeDepth = 1,
    bool IncludeLlmSummary = true,
    bool EnableThinking = false,
    Guid? AnalysisPlanId = null,
    IReadOnlyList<AnalysisGroupSelection>? Groups = null,
    Guid? SourceAnalysisId = null,
    IReadOnlyList<string>? RequestedViewIds = null,
    SampleGenerationMetadata? TestMetadata = null);

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
    DateTimeOffset? LeaseUntil,
    int Revision = 1, Guid? LeaseId = null, IReadOnlyList<SemanticCheckpoint>? Checkpoints = null,
    IReadOnlyList<LlmDiagnostic>? Diagnostics = null, SemanticProgress? Execution = null, string? StopReason = null,
    string? GenerationVersion = null);

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
    string Header,
    IReadOnlyList<DiffChangedRange>? ChangedRanges = null);

public sealed record DiffChangedRange(
    int? OldStartLine,
    int OldLineCount,
    int? NewStartLine,
    int NewLineCount);

public sealed record GitComparison(
    string BaseSha,
    string TargetSha,
    IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<RepositoryFileSnapshot>? ContextFiles = null,
    bool ContextFilesTruncated = false);

public sealed record GitCommitSummary(
    string Sha,
    IReadOnlyList<string> ParentShas,
    DateTimeOffset AuthoredAt,
    string Message,
    string AuthorName,
    string AuthorEmail);

public sealed record EvidenceSnippet(
    string RevisionSha,
    string BlobOid,
    string FilePath,
    int StartLine,
    int EndLine,
    string Content);

public sealed record CppCallFact(
    string Expression,
    string Name,
    int ArgumentCount,
    int Line,
    int Order,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyList<ControlScope>? ControlPath = null,
    int? EndLine = null,
    int? StartOffset = null,
    int? EndOffset = null);

public sealed record ControlScope(
    string Id,
    string Kind,
    string Label,
    string Branch);

public sealed record CppControlNodeFact(
    string Id,
    string Kind,
    string Label,
    int StartLine,
    int EndLine,
    int? CallOrder = null,
    string? TargetSemanticKey = null,
    bool IsIndirect = false,
    string? ViaApi = null);

public sealed record CppControlEdgeFact(
    string SourceId,
    string TargetId,
    string Type,
    string Label);

public sealed record CppSymbolFact(
    string SemanticKey,
    string QualifiedName,
    string SimpleName,
    string Kind,
    int? ParameterCount,
    string Signature,
    string FilePath,
    string? ProjectPath,
    int StartLine,
    int EndLine,
    string ContentFingerprint,
    IReadOnlyList<CppCallFact> Calls,
    IReadOnlyList<string> Bases,
    IReadOnlyList<CppControlNodeFact>? ControlNodes = null,
    IReadOnlyList<CppControlEdgeFact>? ControlEdges = null,
    IReadOnlyList<ClassMemberFact>? Members = null,
    string? OwnerSemanticKey = null,
    string? OwnerKind = null, IReadOnlyList<ExecutionFact>? Execution = null);

public sealed record CppEdgeFact(
    string SourceSemanticKey,
    string TargetSemanticKey,
    string Type,
    string Label,
    Confidence Confidence,
    string FilePath,
    int Line,
    int? SequenceIndex,
    bool IsIndirect = false,
    string? ViaApi = null,
    IReadOnlyList<ControlScope>? ControlPath = null,
    int? EndLine = null);

public sealed record ExcludedCallFact(
    string FilePath,
    int Line,
    string SourceSemanticKey,
    string Expression,
    string Reason,
    IReadOnlyList<string> CandidateTargets);

public sealed record CppSourceIndex(
    string ParserVersion,
    IReadOnlyList<CppSymbolFact> TargetSymbols,
    IReadOnlyList<CppEdgeFact> TargetEdges,
    IReadOnlyList<CppSymbolFact> BeforeChangedSymbols,
    IReadOnlyList<string> Diagnostics,
    int AmbiguousCallCount,
    int IndexedFileCount,
    long IndexedBytes,
    bool Truncated,
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<ExcludedCallFact>? ExcludedCalls = null,
    int ExcludedCallCount = 0,
    bool ExcludedCallsTruncated = false,
    IReadOnlyList<CppEdgeFact>? BaseEdges = null);

public sealed record PreparedRepositoryAnalysis(
    GitComparison Comparison,
    CppSourceIndex CppIndex);

public sealed record RepositoryFileSnapshot(
    string Path,
    string RevisionSha,
    string BlobOid,
    string Content);

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
    string ContentFingerprint,
    IReadOnlyList<ClassMemberFact>? Members = null,
    string? OwnerIdentityId = null);

public sealed record ClassMemberFact(
    string Name,
    string Kind,
    string Accessibility,
    string Signature,
    string? DeclaredType,
    bool IsStatic,
    int StartLine,
    int EndLine,
    IReadOnlyList<string>? ReferencedTypes = null);

public sealed record EvidenceRef(
    string Id,
    string RevisionSha,
    string BlobOid,
    string FilePath,
    int StartLine,
    int EndLine,
    string Analyzer,
    Confidence Confidence,
    int? StartOffset = null,
    int? EndOffset = null);

public sealed record GraphEdge(
    string Id,
    string FromIdentityId,
    string ToIdentityId,
    string Type,
    string Label,
    Confidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    int? SequenceIndex = null,
    bool IsIndirect = false,
    string? ViaApi = null,
    IReadOnlyList<ControlScope>? ControlPath = null,
    string? RevisionSha = null,
    string? FilePath = null,
    int? StartLine = null,
    int? EndLine = null,
    CodeContext? Context = null);

public sealed record ControlFlowNode(
    string Id,
    string Kind,
    string Label,
    int StartLine,
    int EndLine,
    IReadOnlyList<string> EvidenceIds,
    string? CallTargetIdentityId = null,
    bool IsIndirect = false,
    string? ViaApi = null,
    CodeContext? Context = null);

public sealed record CodeContext(string Statement, string? Target, string? Receiver,
    IReadOnlyList<string> Arguments, string? AssignedTo, string? CreatedType,
    IReadOnlyList<string> Initializers, IReadOnlyList<ControlScope> ControlPath,
    SourceSpan Span, string Purpose, IReadOnlyList<CodeDefinition>? Definitions = null);
public sealed record CodeDefinition(string Name, string Statement, SourceSpan Span);

public sealed record ControlFlowEdge(
    string SourceId,
    string TargetId,
    string Type,
    string Label);

public sealed record MethodControlFlow(
    string IdentityId,
    IReadOnlyList<ControlFlowNode> Nodes,
    IReadOnlyList<ControlFlowEdge> Edges,
    string? RevisionSha = null,
    string? FilePath = null);

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
    IReadOnlyList<SymbolChange> Changes,
    IReadOnlyList<MethodControlFlow>? ControlFlows = null,
    IReadOnlyList<MethodExecution>? Executions = null);

public sealed record DiagramNode(
    string Id,
    string Label,
    string Kind,
    string? Group,
    string Status,
    Confidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    string? Shape = null,
    IReadOnlyList<string>? Details = null,
    DiagramChangeMarker? ChangeMarker = null,
    IReadOnlyList<string>? SourceFactIds = null,
    string? DetailPageId = null,
    string? AbstractionKind = null,
    CodeContext? Context = null,
    string? QualifiedName = null,
    string? OriginalExpression = null);

public sealed record DiagramEdge(
    string Id,
    string SourceId,
    string TargetId,
    string Type,
    string Label,
    string Status,
    Confidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    int? SequenceIndex = null,
    bool IsIndirect = false,
    string? ViaApi = null,
    IReadOnlyList<ControlScope>? ControlPath = null,
    DiagramChangeMarker? ChangeMarker = null,
    IReadOnlyList<string>? SourceFactIds = null,
    CodeContext? Context = null,
    string? RelationOrigin = null, string? OriginalExpression = null, string? ReturnValue = null,
    string? TerminationTarget = null, CallPresentation? Call = null);

public sealed record DiagramChangeMarker(
    DiagramChangeKind Kind,
    DiagramChangePrecision Precision,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    IReadOnlyList<string> EvidenceIds);

public sealed record DiagramIr(
    string Type,
    string Title,
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Provenance,
    string? Direction = null,
    IReadOnlyList<SequenceBlock>? SequenceBlocks = null);

public sealed record SequenceBlock(string Id, string Kind, string Label,
    IReadOnlyList<SequenceBlock> Children, string? EdgeId = null,
    IReadOnlyList<string>? ParticipantIds = null, string? DetailPageId = null,
    IReadOnlyList<string>? EvidenceIds = null, IReadOnlyList<string>? SourceFactIds = null,
    string? OriginalExpression = null, string? TerminationTarget = null);

public sealed record DiagramPage(string Id, string Title, DiagramArtifact Diagram,
    string? Level = null, IReadOnlyList<string>? BlockIds = null, IReadOnlyList<string>? SymbolIds = null,
    string? ResultKind = null, DiagramArtifact? CodeDiagram = null, string? AiState = null);
public sealed record DiagramViewDocument(string OverviewPageId, IReadOnlyList<DiagramPage> Pages,
    IReadOnlyList<ChangeCoverage> Coverage);
public sealed record ChangeCoverage(string ChangeId, string State, IReadOnlyList<string> PageIds, string? Reason = null,
    IReadOnlyList<string>? MissingFactIds = null);
public sealed record SourceSpan(string RevisionSha, string BlobOid, string FilePath, int StartLine, int EndLine,
    int? StartOffset = null, int? EndOffset = null);
public sealed record SourceFact(string Id, string Kind, string Label, IReadOnlyList<string> ChangeIds,
    IReadOnlyList<string> EvidenceIds, SourceSpan? Span, string? Content = null,
    CodeContext? Context = null);
public sealed record EvidenceBundle(string Hash, string BaseSha, string TargetSha,
    IReadOnlyList<string> ChangeIds, IReadOnlyList<SourceFact> Facts, IReadOnlyList<string> Warnings,
    IReadOnlyList<ExecutionMeaningInput>? ExecutionInputs = null);
public sealed record ChangeUnderstanding(string Summary, IReadOnlyList<ChangeExplanation> Changes);
public sealed record ChangeExplanation(string ChangeId, string Summary, IReadOnlyList<string> FactIds);
public sealed record SemanticElement(string Id, string Summary, IReadOnlyList<string> NodeIds);
public sealed record SemanticMessage(string EdgeId, string Summary);
public sealed record DiagramPlan(string Summary, IReadOnlyList<SemanticElement> Elements,
    IReadOnlyList<SemanticMessage> Messages, IReadOnlyList<string> InstructionResults,
    IReadOnlyList<PageChangeExplanation>? Changes = null);
public sealed record PageChangeExplanation(string ChangeId, string Summary,
    IReadOnlyList<string> FactIds, IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);
public sealed record DiagramExplanation(string Summary, IReadOnlyList<PageChangeExplanation> Changes,
    IReadOnlyList<string> FactIds, IReadOnlyList<string> EvidenceIds, string Status,
    IReadOnlyList<string> Warnings, string Basis = "GeneratedSource",
    IReadOnlyList<CodeBlockBehavior>? Behaviors = null, SemanticCoverage? Coverage = null,
    IReadOnlyList<SemanticFailureDetail>? Failures = null);
public sealed record SemanticCoverage(int TotalUnits, int VerifiedUnits, int PendingUnits, int FailedUnits = 0);
public sealed record SemanticFailureDetail(IReadOnlyList<string> ItemIds, string Stage, string Code,
    IReadOnlyList<string> FactIds, IReadOnlyList<string> CorrectionInstructions,
    IReadOnlyList<string>? Fields = null, IReadOnlyList<string>? IssueCodes = null, string? Category = null);
public sealed record DiagramPlanReview(bool Accepted, IReadOnlyList<string> Issues);
public sealed record SemanticGeneration(DiagramIr Diagram, string Status, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> InstructionResults, int Attempts, DiagramExplanation? Explanation = null, string? FailureStage = null);

public sealed record DiagramArtifact(
    Guid Id,
    string Type,
    int Version,
    DiagramIr Ir,
    string MermaidDsl,
    DateTimeOffset CreatedAt,
    DiagramExplanation? Explanation = null);

public sealed record RiskItem(
    string Severity,
    string Text,
    IReadOnlyList<string> EvidenceIds);

public sealed record ReviewNarrative(
    string Summary,
    string Intent,
    IReadOnlyList<RiskItem> Risks,
    IReadOnlyList<string> Warnings);

public sealed record DiagramAvailability(
    string Type,
    bool Available,
    string? Reason);

public sealed record AnalysisResult(
    IReadOnlyList<ChangedFile> ChangedFiles,
    VersionedGraph Graph,
    ReviewNarrative Narrative,
    IReadOnlyList<DiagramArtifact> Diagrams,
    IReadOnlyList<DiagramAvailability> DiagramAvailability = null!,
    IReadOnlyList<AnalysisDiagramGroupResult>? DiagramGroups = null);

public sealed record AnalysisDiagramGroupResult(
    string GroupId,
    string Title,
    IReadOnlyList<string> ChangeIds,
    DiagramArtifact? Diagram,
    ReviewNarrative Narrative,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AnalysisDiagramViewResult>? Views = null,
    ChangeUnderstanding? Understanding = null,
    string? BundleHash = null);

public sealed record AnalysisDiagramViewResult(
    string ViewId,
    DiagramViewSelection Selection,
    DiagramArtifact? Diagram,
    IReadOnlyList<string> Warnings,
    string State = "Completed",
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Reused = false,
    DiagramGenerationMetadata? GenerationMetadata = null,
    DiagramViewDocument? Document = null,
    string? FailureStage = null);

public sealed record DiagramSourceRange(
    string FilePath,
    int StartLine,
    int EndLine);

public sealed record DiagramGenerationMetadata(
    IReadOnlyList<string> ChangeIds,
    IReadOnlyList<DiagramSourceRange> Sources,
    IReadOnlyList<string> EvidenceIds,
    string LlmStatus,
    string? RefinementInstruction,
    IReadOnlyList<string> Warnings,
    string? BundleHash = null,
    string? AnalyzerVersion = null,
    string? PromptVersion = null,
    DiagramStyleOverrides? EffectiveOptions = null,
    IReadOnlyList<string>? InstructionResults = null,
    int Attempts = 0);

public sealed record DiagramStyleOverrides(
    string? Direction = null,
    string? DetailLevel = null,
    int? CallerDepth = null,
    int? CalleeDepth = null,
    int? RelationDepth = null);

public sealed record DiagramViewSelection(
    string Id,
    string DiagramType,
    string PresetId,
    DiagramStyleOverrides? Overrides = null,
    bool FocusOnChanges = false,
    string? RefinementInstruction = null);

public sealed record AnalysisGroupSelection(
    string Id,
    string Title,
    IReadOnlyList<string> ChangeIds,
    string DiagramType,
    string PresetId,
    DiagramStyleOverrides? Overrides = null,
    IReadOnlyList<DiagramViewSelection>? Views = null);

public sealed record AnalysisPlanRequest(
    Guid RepositoryId,
    string TargetRevision,
    string? BaseRevision = null,
    bool UseLlmGrouping = true,
    bool EnableThinking = false);

public sealed record ChangeCandidate(
    string Id,
    string IdentityId,
    string QualifiedName,
    string Kind,
    SymbolChangeKind ChangeType,
    string FilePath,
    int StartLine,
    int EndLine,
    string Signature,
    Confidence Confidence,
    int CallerCount,
    int CalleeCount,
    IReadOnlyList<string> EvidenceIds);

public sealed record AnalysisGroupDraft(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> ChangeIds,
    string Source,
    Confidence Confidence,
    string SuggestedDiagramType);

public sealed record AnalysisPlan(
    Guid Id,
    string OwnerUserId,
    AnalysisPlanRequest Request,
    AnalysisPlanState State,
    string? BaseSha,
    string? TargetSha,
    int Progress,
    string StageMessage,
    GitComparison? Comparison,
    VersionedGraph? Graph,
    IReadOnlyList<ChangeCandidate> Candidates,
    IReadOnlyList<AnalysisGroupDraft> SuggestedGroups,
    IReadOnlyList<AnalysisGroupSelection> Selections,
    IReadOnlyList<string> Warnings,
    string? ErrorCode,
    string? ErrorMessage,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LeaseUntil,
    string? IndexVersion = null,
    AnalysisExclusionSummary? Exclusions = null,
    string? TargetCommitMessage = null,
    IReadOnlyList<AnalysisNotice>? Notices = null);

public sealed record AnalysisNotice(
    string Code,
    string Category,
    string Severity,
    string Message);

public sealed record AnalysisExclusionSummary(
    int TotalCount,
    int FileCount,
    bool Truncated,
    IReadOnlyList<ExcludedCallFact> Calls);

public sealed record UpdateAnalysisPlanSelectionRequest(
    int ExpectedRevision,
    IReadOnlyList<AnalysisGroupSelection> Groups);

public sealed record GenerateAnalysisPlanRequest(
    int ExpectedRevision,
    Guid? SourceAnalysisId = null,
    IReadOnlyList<string>? RequestedViewIds = null);

public sealed record DiagramPreset(
    string Id,
    string Type,
    string Name,
    string Description,
    string ThumbnailDsl,
    string Direction,
    string DetailLevel,
    int CallerDepth,
    int CalleeDepth,
    int RelationDepth,
    int MaximumNodes,
    int MaximumEdges);

public sealed record NaturalDiagramRequest(
    string Prompt,
    string DiagramType = "auto",
    Guid? ParentDiagramId = null,
    bool EnableThinking = false,
    bool ForceRegenerate = false,
    string PresetId = "balanced",
    DiagramStyleOverrides? Style = null,
    IReadOnlyList<DiagramViewSelection>? Views = null,
    SampleGenerationMetadata? TestMetadata = null);

public sealed record ReviseNaturalDiagramViewsRequest(
    IReadOnlyList<DiagramViewSelection> Views,
    IReadOnlyList<string>? RegenerateViewIds = null);

public sealed record NaturalDiagramViewResult(
    string ViewId,
    DiagramViewSelection Selection,
    DiagramArtifact? Diagram,
    string State = "Completed",
    string? ErrorCode = null,
    string? ErrorMessage = null,
    DiagramArtifact? LastSuccessfulDiagram = null,
    bool Reused = false,
    NaturalDesignQuality? DesignQuality = null,
    IReadOnlyList<NaturalDiagramPageResult>? Pages = null);

public sealed record NaturalDiagramPageResult(
    string Id,
    string ScenarioId,
    string Title,
    DiagramArtifact? Diagram,
    string State = "Completed",
    string? ErrorCode = null,
    string? ErrorMessage = null,
    DiagramArtifact? LastSuccessfulDiagram = null,
    bool Reused = false,
    NaturalDesignQuality? DesignQuality = null);

public sealed record NaturalDiagramRecord(
    Guid Id,
    NaturalDiagramRequest Request,
    DiagramArtifact Diagram,
    DateTimeOffset CreatedAt,
    string OwnerUserId = "",
    Guid? RootDiagramId = null,
    Guid? ParentDiagramId = null,
    string Source = "generated",
    string GeneratorVersion = "natural-v1",
    bool Reused = false,
    IReadOnlyList<NaturalDiagramViewResult>? Views = null,
    int Revision = 1,
    NaturalRequirements? Requirements = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NaturalDiagramRunState
{
    Queued,
    Generating,
    Completed,
    Partial,
    Failed,
    Cancelled,
    NeedsClarification
}

public sealed record NaturalDiagramRun(
    Guid Id,
    string OwnerUserId,
    NaturalDiagramRequest Request,
    NaturalDiagramRunState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Revision = 1,
    int Progress = 0,
    string StageMessage = "Queued",
    Guid? SourceDiagramId = null,
    IReadOnlyList<string>? RegenerateViewIds = null,
    IReadOnlyList<string>? RegeneratePageIds = null,
    NaturalRequirements? Requirements = null,
    IReadOnlyList<NaturalDiagramViewResult>? Views = null,
    Guid? ResultDiagramId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    Guid? LeaseId = null,
    DateTimeOffset? LeaseUntil = null,
    IReadOnlyList<SemanticCheckpoint>? Checkpoints = null,
    IReadOnlyList<LlmDiagnostic>? Diagnostics = null,
    SemanticProgress? Execution = null,
    IReadOnlyList<NaturalQuestion>? Questions = null,
    IReadOnlyList<NaturalAnswer>? Answers = null,
    int QuestionVersion = 0,
    int AnswerVersion = 0,
    string? InputFingerprint = null, bool? ResumeAllowed = null)
{
    public bool IsTerminal => State is NaturalDiagramRunState.Completed or NaturalDiagramRunState.Partial or
        NaturalDiagramRunState.Failed or NaturalDiagramRunState.Cancelled;
}

public sealed record CreateNaturalDiagramRunRequest(
    NaturalDiagramRequest Request,
    Guid? SourceDiagramId = null,
    IReadOnlyList<string>? RegenerateViewIds = null,
    IReadOnlyList<string>? RegeneratePageIds = null);

public sealed record NaturalDiagramRunActionRequest(int ExpectedRevision);

public sealed record NaturalGenerationProgress(
    NaturalRequirements? Requirements,
    IReadOnlyList<NaturalDiagramViewResult> Views,
    int CompletedUnits,
    int TotalUnits,
    string StageMessage);

public sealed record SaveDiagramDslRevisionRequest(string MermaidDsl);

public sealed record EditableDiagramNode(string Id, string Label, IReadOnlyList<string>? Details = null);

public sealed record EditableDiagramEdge(
    string Id,
    string SourceId,
    string TargetId,
    string Label,
    string? Type = null);

public sealed record EditableSequenceAnnotation(string Id, string Kind, string Label);

public sealed record DiagramEditDocument(
    string Title,
    string? Direction,
    IReadOnlyList<EditableDiagramNode> Nodes,
    IReadOnlyList<EditableDiagramEdge> Edges,
    IReadOnlyList<EditableSequenceAnnotation>? SequenceAnnotations = null);

public sealed record SaveDiagramEditRequest(
    Guid RootArtifactId,
    Guid? ParentRevisionId,
    int ExpectedVersion,
    DiagramEditDocument Document);

public sealed record DiagramEditPreviewResponse(
    int Version,
    DiagramIr Ir,
    string MermaidDsl);

public sealed record AnalysisHistorySummary(
    Guid Id,
    AnalysisState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? BaseSha,
    string? TargetSha,
    bool HasResult,
    int TotalGroups,
    int SuccessfulGroups,
    int TotalViews,
    int SuccessfulViews);

public sealed record DiagramRevisionRecord(
    Guid Id,
    Guid RootArtifactId,
    Guid SourceArtifactId,
    Guid? ParentRevisionId,
    string OwnerUserId,
    string SourceKind,
    Guid SourceId,
    string? GroupId,
    string ViewId,
    int Version,
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

public sealed record LlmThinkingContractTestResult(
    bool Success,
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
