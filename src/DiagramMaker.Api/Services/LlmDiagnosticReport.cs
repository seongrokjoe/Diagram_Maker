using System.Text;
using System.Text.RegularExpressions;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static class LlmDiagnosticReport
{
    // An explicit projection: serializing LlmOptions would disclose the endpoint.
    public static object Settings(LlmOptions options) => new {
        protocol = SemanticExecution.SharedPolicyVersion, options.Enabled,
        options.DiagramOutputTokens, options.ReviewOutputTokens, options.ThinkingOutputTokens,
        options.OutputHardLimit, options.MaxInputTokens, options.MaxContextTokens,
        options.MaxInputCharacters, options.SemanticJobBudgetSeconds, options.RequestTimeoutSeconds,
        options.UseServerTokenization
    };

    public static string Text(string state, string? stopReason, SemanticProgress? progress,
        IReadOnlyList<LlmDiagnostic>? diagnostics)
    {
        var text = new StringBuilder("DiagramMaker generation diagnostics / " + SemanticExecution.SharedPolicyVersion + "\n");
        text.AppendLine($"State: {Code(state)}; stop: {Code(stopReason)}");
        if (progress is not null)
            text.AppendLine($"Requests: {progress.Requests}; HTTP: {progress.TransportRequests}; completed: {progress.CompletedUnits}; reused: {progress.ReusedUnits}; elapsed: {progress.TotalElapsedSeconds}s");
        foreach (var d in (diagnostics ?? []).TakeLast(500))
        {
            text.AppendLine($"{d.StartedAt:O} {Code(d.Purpose)} / {Code(d.Stage)}: {Code(d.State)}; {Code(d.ErrorCode)}; {Code(d.ValidationCode)}");
            text.AppendLine($"HTTP: {d.HttpStatus}; category: {Code(d.ServerErrorCategory)}; action: {Code(d.NextAction)}; recovery: {Code(d.RecoveryState)}");
            text.AppendLine($"Mode: {Code(d.OutputMode)}; compatible schema: {d.SchemaRelaxed}; finish: {Code(d.FinishReason)}; sent: {d.Sent}; attempts: {d.TransportAttempts}");
            text.AppendLine($"Characters: {d.InputCharacters}/{d.InputCharacterLimit}; input tokens: {d.InputTokens}/{d.InputTokenLimit}; estimated: {d.EstimatedInputTokens}; context: {d.ContextTokenLimit}; output requested: {d.OutputLimit}; output used: {d.CompletionTokens}");
            text.AppendLine($"Batch: {Code(d.RecoveryGroupId)}; parent: {Code(d.ParentGroupId)}; attempt: {d.Attempt}; protocol: {Code(d.ProtocolVersion)}; elapsed: {d.ElapsedMilliseconds}ms");
            if (d.ValidationDetails is { } v)
                text.AppendLine($"Items expected/received: {v.ExpectedItems}/{v.ReceivedItems}; missing: {v.MissingItems}; duplicate: {v.DuplicateItems}; unknown: {v.UnknownItems}; field: {Code(v.Field?.Replace('.', '-'))}; index: {v.ItemIndex}; length: {v.ActualLength}/{v.AllowedLength}; issues: {string.Join(',', (v.IssueCodes ?? []).Select(Code))}");
        }
        text.AppendLine("Source, prompts, model responses, endpoint and authentication values are excluded.");
        return text.ToString();
    }

    private static string Code(string? value) => value is { Length: > 0 and <= 80 } &&
        Regex.IsMatch(value, "^[A-Za-z0-9_-]+$") ? value : "unknown";
}
