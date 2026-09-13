using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

// Fixed inputs only; no workspace, repository or request body is read or saved.
public sealed class CodeDiagramSelfTest(CodeBlockAnalyzer analyzer, IInternalLlmClient llm, IOptions<LlmOptions> options)
{
    public async Task<CodeDiagramTestResult> RunAsync(CancellationToken ct)
    {
        var cases = new List<CodeDiagramTestCase>();
        using var execution = new SemanticExecution(options.Value, null, ct);
        string? stop = null;
        if (!llm.IsEnabled)
        {
            execution.StopRequests(new("LLM_DISABLED", "The internal LLM is disabled."));
            await execution.RecordAsync(new(Guid.NewGuid().ToString("N"), "preflight", "", "Failed", DateTimeOffset.UtcNow,
                ErrorCode: "LLM_DISABLED", Purpose: "generation", NextAction: "check-server-contract"));
        }
        foreach (var language in new[] { "csharp", "cpp" })
        {
            if (execution.RequestFailure is not null) { cases.Add(new(language, "Skipped", 0, 0)); continue; }
            try
            {
                var code = language == "csharp"
                    ? "class Sample { int Run(int n) { if (n < 0) return -1; while (n > 2) n = Save(n); return n; } int Save(int n) { return n - 1; } }"
                    : "int Save(int n) { return n - 1; } int Run(int n) { if (n < 0) return -1; while (n > 2) n = Save(n); return n; }";
                var input = new CodeBlockWorkspaceInput("고정 합성 코드 검사", [new("sample", language, "합성 코드", code)]);
                var graph = await analyzer.AnalyzeAsync(Guid.NewGuid(), input.Blocks, execution.Token);
                var result = await llm.PlanCodeBlockGroupAsync(input, graph, new("sample", "합성 검사", ["sample"]),
                    [new("flow", "flowchart", "balanced")], execution.Token);
                var pages = result?.Pages.Values.ToArray() ?? [];
                var semantic = pages.Count(p => p.Status == "Semantic");
                cases.Add(new(language, pages.Length > 0 && semantic == pages.Length ? "Completed" : "Partial", pages.Length, semantic));
            }
            catch (OperationCanceledException) when (execution.BudgetExpired)
            { stop = "budget"; cases.Add(new(language, "Partial", 0, 0)); break; }
            catch (Exception error) when (error is LlmClientException or DiagramGenerationException or DiagramValidationException)
            {
                stop = error is LlmClientException known ? known.Code : "CODE_ANALYSIS_FAILED";
                cases.Add(new(language, "Failed", 0, 0));
            }
        }
        var success = cases.Count == 2 && cases.All(c => c.State == "Completed");
        var settings = LlmDiagnosticReport.Settings(options.Value);
        var report = "Fixed synthetic C#/C++ code diagram test; Thinking OFF\n" +
            "Settings: " + JsonSerializer.Serialize(settings) + "\n" +
            string.Join("\n", cases.Select(c => $"{c.Language}: {c.State}; semantic pages: {c.SemanticPages}/{c.Pages}")) + "\n" +
            LlmDiagnosticReport.Text(success ? "Completed" : "Partial", stop, execution.Progress, execution.Diagnostics);
        return new(success, true, false, cases, settings, execution.Progress, execution.Diagnostics, report);
    }
}

public sealed record CodeDiagramTestCase(string Language, string State, int Pages, int SemanticPages);
public sealed record CodeDiagramTestResult(bool Success, bool SyntheticOnly, bool ThinkingEnabled,
    IReadOnlyList<CodeDiagramTestCase> Cases, object Settings, SemanticProgress Execution,
    IReadOnlyList<LlmDiagnostic> Diagnostics, string Report);
