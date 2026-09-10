namespace DiagramMaker.Domain;

// Diagnostics contain metadata only. Source, prompts, model responses and server
// addresses never belong in this contract or the downloadable diagnostic report.
public sealed record LlmDiagnostic(string Id, string Stage, string UnitId, string State,
    DateTimeOffset StartedAt, long ElapsedMilliseconds = 0, bool Sent = false,
    int? InputTokens = null, bool EstimatedInputTokens = true, int? OutputLimit = null,
    int? PromptTokens = null, int? CompletionTokens = null, string? FinishReason = null,
    string? ErrorCode = null, string? ValidationCode = null, string? OutputMode = null, int Retries = 0);
public sealed record SemanticCheckpoint(string Key, string Stage, string ValueJson);
public sealed record SemanticProgress(string Stage, string UnitId, int CompletedUnits, int ReusedUnits,
    int Requests, long ElapsedSeconds, int BudgetSeconds);
public sealed record ResumeSemanticRequest(int ExpectedRevision);
