using System.Text.Json.Serialization;

namespace DiagramMaker.Domain;

public sealed record CodeBlockInput(string Id, string Language, string Title, string Code, string? Description = null);
public sealed record CodeBlockGroupSelection(string Id, string Title, IReadOnlyList<string> BlockIds,
    IReadOnlyList<DiagramViewSelection>? Views = null, bool? EnableThinking = null, bool? EnableUserRelations = null);
public sealed record CodeBlockRelation(string Id, string FromBlockId, string ToBlockId, string Kind,
    string Origin, string Description, string? FromSymbolId = null, string? ToSymbolId = null,
    IReadOnlyList<string>? EvidenceIds = null, string? CallSiteId = null);
public sealed record CodeBlockWorkspaceInput(string Title, IReadOnlyList<CodeBlockInput> Blocks,
    IReadOnlyList<CodeBlockGroupSelection>? Groups = null, IReadOnlyList<CodeBlockRelation>? Relations = null,
    bool EnableThinking = false);
public sealed record SaveCodeBlockWorkspaceRequest(int ExpectedRevision, CodeBlockWorkspaceInput Input);
public sealed record StartCodeBlockRunRequest(int ExpectedRevision, IReadOnlyList<string>? RegenerateViewIds = null);
public sealed record CodeBlockWorkspace(Guid Id, string OwnerUserId, int Revision, CodeBlockWorkspaceInput Input,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CodeBlockWorkspaceSummary(Guid Id, int Revision, string Title, int BlockCount,
    DateTimeOffset UpdatedAt);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeBlockRunState { Queued, Indexing, NeedsClarification, Generating, Completed, Partial, Failed, Cancelled }

public sealed record CodeBlockLocation(string BlockId, int StartLine, int EndLine, int StartOffset, int EndOffset);
public sealed record CodeBlockEvidence(string Id, string BlockId, string ContentHash, CodeBlockLocation Location);
public sealed record CodeBlockStep(string Id, string Kind, string Label, string Statement, CodeBlockLocation Location,
    IReadOnlyList<string> EvidenceIds, IReadOnlyList<ControlScope> ControlPath,
    string? Target = null, string? Receiver = null, IReadOnlyList<string>? Arguments = null,
    string? AssignedTo = null, IReadOnlyList<string>? Definitions = null, string Purpose = "operation");
public sealed record CodeBlockCall(string Id, string SymbolId, string Name, int ArgumentCount,
    CodeBlockLocation Location, IReadOnlyList<ControlScope> ControlPath, int Order,
    string? TargetSymbolId = null, IReadOnlyList<string>? CandidateSymbolIds = null,
    string? Receiver = null, string? Statement = null, bool OrderUncertain = false);
public sealed record CodeBlockSymbol(string Id, string BlockId, string Name, string Kind, string Signature,
    CodeBlockLocation Location, IReadOnlyList<string> EvidenceIds, IReadOnlyList<CodeBlockStep> Steps,
    IReadOnlyList<ControlFlowEdge> FlowEdges, IReadOnlyList<CodeBlockCall> Calls,
    IReadOnlyList<ClassMemberFact> Members, IReadOnlyList<string> BaseTypes, string? OwnerId = null,
    bool IsFragment = false, int? ParameterCount = null);
public sealed record CodeBlockStateTransition(string Id, string SymbolId, string Variable, string From, string To,
    string Condition, IReadOnlyList<string> EvidenceIds);
public sealed record CodeBlockGraph(IReadOnlyList<CodeBlockSymbol> Symbols, IReadOnlyList<CodeBlockRelation> Relations,
    IReadOnlyList<CodeBlockEvidence> Evidence, IReadOnlyList<CodeBlockStateTransition> Transitions,
    IReadOnlyList<string> Warnings, string AnalyzerVersion);
public sealed record CodeBlockQuestionOption(string Id, string Label, string? TargetSymbolId = null);
public sealed record CodeBlockQuestion(string Id, string Prompt, string FromBlockId, string FromSymbolId,
    string CallSiteId, IReadOnlyList<string> EvidenceIds, IReadOnlyList<CodeBlockQuestionOption> Options);
public sealed record CodeBlockAnswer(string QuestionId, string OptionId, bool MergeGroups = false);
public sealed record AnswerCodeBlockQuestionsRequest(int ExpectedInputRevision, int ExpectedRunRevision,
    IReadOnlyList<CodeBlockAnswer> Answers, bool SkipRemaining = false);
public sealed record CodeBlockBehavior(string Id, string Summary, IReadOnlyList<string> FactIds,
    IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);
public sealed record CodeBlockUnderstanding(string Summary, string RecommendedType, IReadOnlyList<CodeBlockBehavior> Behaviors,
    IReadOnlyList<string>? RequestedSymbolIds = null);
public sealed record CodeBlockSemanticElement(string Id, string Summary, IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> FactIds, string Condition, string Outcome);
public sealed record CodeBlockControlLabel(string Id, string Label);
public sealed record CodeBlockSemanticPlan(string Summary, IReadOnlyList<CodeBlockSemanticElement> Elements,
    IReadOnlyList<SemanticMessage> Messages, IReadOnlyList<CodeBlockBehavior> Behaviors,
    IReadOnlyList<CodeBlockControlLabel>? Controls = null);
public sealed record CodeBlockViewResult(string ViewId, DiagramViewSelection Selection, string State,
    IReadOnlyList<DiagramPage> Pages, IReadOnlyList<string> Warnings, string? ErrorMessage = null,
    bool Reused = false, string? CacheKey = null, string LlmStatus = "Deterministic",
    string? FailureStage = null);
public sealed record CodeBlockGroupResult(string GroupId, string Title, IReadOnlyList<string> BlockIds,
    IReadOnlyList<CodeBlockViewResult> Views, IReadOnlyList<DiagramAvailability> Availability);
public sealed record CodeBlockRun(Guid Id, Guid WorkspaceId, string OwnerUserId, int InputRevision, int Revision,
    CodeBlockWorkspaceInput Snapshot, CodeBlockRunState State, int Progress, string StageMessage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LeaseUntil = null, Guid? LeaseId = null,
    CodeBlockGraph? Graph = null, IReadOnlyList<CodeBlockGroupSelection>? Groups = null,
    IReadOnlyList<CodeBlockRelation>? Relations = null, IReadOnlyList<CodeBlockQuestion>? Questions = null,
    IReadOnlyList<CodeBlockAnswer>? Answers = null, IReadOnlyList<CodeBlockGroupResult>? Results = null,
    IReadOnlyList<string>? Warnings = null, string? ErrorCode = null, string? ErrorMessage = null,
    IReadOnlyList<string>? RegenerateViewIds = null, bool QuestionsResolved = false,
    IReadOnlyList<SemanticCheckpoint>? Checkpoints = null, IReadOnlyList<LlmDiagnostic>? Diagnostics = null,
    SemanticProgress? Execution = null, string? StopReason = null, Guid? SourceRunId = null,
    string? GenerationVersion = null)
{
    [JsonIgnore] public bool IsTerminal => State is CodeBlockRunState.Completed or CodeBlockRunState.Partial
        or CodeBlockRunState.Failed or CodeBlockRunState.Cancelled;
}
public sealed record CodeBlockRunSummary(Guid Id, Guid WorkspaceId, int InputRevision, int Revision,
    CodeBlockRunState State, int Progress, string StageMessage, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, IReadOnlyList<CodeBlockGroupSelection> Groups,
    IReadOnlyList<CodeBlockQuestion> Questions, IReadOnlyList<CodeBlockGroupSummary> Results,
    IReadOnlyList<string> Warnings, string? ErrorCode, string? ErrorMessage,
    SemanticProgress? Execution = null, string? StopReason = null, bool CanResume = false);
public sealed record CodeBlockPageSummary(string Id, string Title, Guid ArtifactId,
    string? Level = null, IReadOnlyList<string>? BlockIds = null, IReadOnlyList<string>? SymbolIds = null,
    string? ResultKind = null);
public sealed record CodeBlockViewSummary(string ViewId, DiagramViewSelection Selection, string State,
    IReadOnlyList<CodeBlockPageSummary> Pages, IReadOnlyList<string> Warnings, string? ErrorMessage,
    bool Reused, string LlmStatus, string? FailureStage = null);
public sealed record CodeBlockGroupSummary(string GroupId, string Title, IReadOnlyList<string> BlockIds,
    IReadOnlyList<CodeBlockViewSummary> Views, IReadOnlyList<DiagramAvailability> Availability);
public sealed record CodeBlockEvidenceSnippet(string BlockId, string BlockTitle, int StartLine, int EndLine,
    string Content, string ContentHash);
