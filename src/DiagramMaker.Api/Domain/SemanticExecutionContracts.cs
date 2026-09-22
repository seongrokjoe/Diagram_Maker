namespace DiagramMaker.Domain;

// Diagnostics contain metadata only. Source, prompts, model responses and server
// addresses never belong in this contract or the downloadable diagnostic report.
public sealed record LlmDiagnostic(string Id, string Stage, string UnitId, string State,
    DateTimeOffset StartedAt, long ElapsedMilliseconds = 0, bool Sent = false,
    int? InputTokens = null, bool EstimatedInputTokens = true, int? OutputLimit = null,
    int? PromptTokens = null, int? CompletionTokens = null, string? FinishReason = null,
    string? ErrorCode = null, string? ValidationCode = null, string? OutputMode = null, int Retries = 0,
    int TransportAttempts = 0, int TokenizationRequests = 0, string? Purpose = null,
    int? InputCharacters = null, int? InputCharacterLimit = null, int? InputTokenLimit = null,
    int? ContextTokenLimit = null, LlmValidationDetails? ValidationDetails = null,
    string? ProtocolVersion = null, string? RecoveryGroupId = null, string? ParentGroupId = null,
    int? Attempt = null, string? RecoveryState = null, int? RequiredOutputTokens = null,
    IReadOnlyList<string>? AncestorGroupIds = null, int? HttpStatus = null,
    string? ServerErrorCategory = null, string? NextAction = null, bool SchemaRelaxed = false,
    string? Kind = null, string? RequestId = null, NaturalExtractionMetrics? Extraction = null);
public sealed record NaturalExtractionMetrics(int SourceRanges, int? Received, int? Grounded, int? Preserved,
    int? Replaced, int? Rejected, int ExtractionAttempts, int ReviewAttempts);
public sealed record LlmValidationDetails(int ExpectedItems, int ReceivedItems,
    int MissingItems = 0, int DuplicateItems = 0, int UnknownItems = 0,
    string? Field = null, int? ItemIndex = null, int? ActualLength = null, int? AllowedLength = null,
    int? NonTargetItems = null, int? UnknownAliases = null, IReadOnlyList<string>? IssueCodes = null);
public sealed record SemanticCheckpoint(string Key, string Stage, string ValueJson,
    string State = "Completed", string? ErrorCode = null, string? FailureKind = null, string? RejectedContent = null,
    IReadOnlyList<string>? Dependencies = null, bool WasSplit = false, LlmValidationDetails? ValidationDetails = null);
public sealed record SemanticProgress(string Stage, string UnitId, int CompletedUnits, int ReusedUnits,
    int Requests, long ElapsedSeconds, int BudgetSeconds, int TotalUnits = 0,
    int AttemptCompletedUnits = 0, int AttemptRequests = 0, long TotalElapsedSeconds = 0,
    int AttemptNumber = 1, DateTimeOffset? StartedAt = null, int TransportRequests = 0,
    int TokenizationRequests = 0, int FailedUnits = 0, int RejectedBeforeSend = 0,
    int AttemptTransportRequests = 0, int AttemptTokenizationRequests = 0,
    long WaitMilliseconds = 0, long AttemptWaitMilliseconds = 0, DateTimeOffset? WaitingSince = null,
    IReadOnlyList<LlmDiagnostic>? RecentFailures = null, LlmDiagnostic? LastRequest = null,
    bool ProtocolUpgraded = false, DateTimeOffset? LastProgressAt = null, SemanticCoverage? Coverage = null,
    IReadOnlyDictionary<string, long>? StageMilliseconds = null, int PreflightTokenizationRequests = 0);
public sealed record ResumeSemanticRequest(int ExpectedRevision);
