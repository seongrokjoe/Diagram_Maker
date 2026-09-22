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
        var protocol = (diagnostics ?? []).LastOrDefault(d => d.ProtocolVersion is not null)?.ProtocolVersion ?? SemanticExecution.SharedPolicyVersion;
        var text = new StringBuilder("DiagramMaker generation diagnostics / " + Code(protocol) + "\n");
        text.AppendLine($"State: {Code(state)}; stop: {Code(stopReason)}");
        if (progress is not null)
            text.AppendLine($"Diagnostic events: {progress.Requests}; LLM requests: {(diagnostics ?? []).Count(d => d.Kind == "Request" || d.Kind is null && d.Sent)}; HTTP attempts: {progress.TransportRequests}; completed checkpoints: {progress.CompletedUnits}; reused: {progress.ReusedUnits}; elapsed: {progress.TotalElapsedSeconds}s");
        var terminal = (diagnostics ?? []).LastOrDefault(d => d.Kind == "Terminal" && d.RecoveryState != "Recovered") ??
            (diagnostics ?? []).LastOrDefault(d => d.ErrorCode is not null && d.RecoveryState != "Recovered");
        if (terminal is not null)
            text.AppendLine($"Final failure: {Code(terminal.Purpose)} / {Code(terminal.ErrorCode)} / {Code(terminal.ValidationCode)}; field: {Code(terminal.ValidationDetails?.Field?.Replace('.', '-'))}; attempt: {terminal.Attempt}; action: {Code(terminal.NextAction)}; recovery: {Code(terminal.RecoveryState)}");
        foreach (var d in (diagnostics ?? []).TakeLast(500))
        {
            text.AppendLine($"{d.StartedAt:O} {Code(d.Purpose)} / {Code(d.Stage)}: {Code(d.State)}; {Code(d.ErrorCode)}; {Code(d.ValidationCode)}");
            var missing = d.Kind is null ? "not-recorded" : "not-applicable";
            text.AppendLine($"Kind: {Code(d.Kind, "not-recorded")}; request: {Code(d.RequestId ?? (d.Kind == "Request" ? d.Id : null), missing)}");
            text.AppendLine($"HTTP: {d.HttpStatus?.ToString() ?? missing}; category: {Code(d.ServerErrorCategory, missing)}; action: {Code(d.NextAction, missing)}; recovery: {Code(d.RecoveryState, missing)}");
            text.AppendLine($"Mode: {Code(d.OutputMode)}; compatible schema: {d.SchemaRelaxed}; finish: {Code(d.FinishReason)}; sent: {d.Sent}; attempts: {d.TransportAttempts}");
            text.AppendLine($"Characters: {d.InputCharacters}/{d.InputCharacterLimit}; input tokens: {d.InputTokens}/{d.InputTokenLimit}; estimated: {d.EstimatedInputTokens}; context: {d.ContextTokenLimit}; output requested: {d.OutputLimit}; output used: {d.CompletionTokens}");
            text.AppendLine($"Batch: {Code(d.RecoveryGroupId)}; parent: {Code(d.ParentGroupId)}; attempt: {d.Attempt}; protocol: {Code(d.ProtocolVersion)}; elapsed: {d.ElapsedMilliseconds}ms");
            if (d.ValidationDetails is { } v)
                text.AppendLine($"Items expected/received: {v.ExpectedItems}/{v.ReceivedItems}; missing: {v.MissingItems}; duplicate: {v.DuplicateItems}; unknown: {v.UnknownItems}; field: {Code(v.Field?.Replace('.', '-'))}; index: {v.ItemIndex}; length: {v.ActualLength}/{v.AllowedLength}; issues: {string.Join(',', (v.IssueCodes ?? []).Select(code => Code(code)))}");
            if (d.Extraction is { } m)
                text.AppendLine($"Extraction: source={m.SourceRanges}; received={Count(m.Received)}; grounded={Count(m.Grounded)}; preserved={Count(m.Preserved)}; replaced={Count(m.Replaced)}; rejected={Count(m.Rejected)}; extraction attempts={m.ExtractionAttempts}; review attempts={m.ReviewAttempts}");
        }
        text.AppendLine("Source, prompts, model responses, endpoint and authentication values are excluded.");
        return text.ToString();
    }

    private static string Code(string? value, string missing = "not-applicable") => value is null ? missing :
        value is { Length: > 0 and <= 80 } && Regex.IsMatch(value, "^[A-Za-z0-9_-]+$") ? value : "unrecognized";
    private static string Count(int? value) => value?.ToString() ?? "not-recorded";
}
