using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

// Use the production generation service with an isolated, disposable store.
// The endpoint accepts no user prompt and never saves test data to the app store.
public sealed class NaturalDiagramSelfTest(IInternalLlmClient llm, MermaidCompiler compiler,
    DiagramPresetCatalog presets, IOptions<LlmOptions> options, IWebHostEnvironment environment)
{
    internal const string ShortPrompt = "사용자가 승인을 요청한다. 담당자가 승인하면 요청을 완료하고, 거절하면 거절 결과를 사용자에게 알리고 종료한다.";
    internal const string TablePrompt = "장비 제어 시스템은 작업자, 제어기, 장비로 구성된다. 제어기는 요청을 검증하고 장비를 제어한다.\n" +
        "| 현재 상태 | 사건 | 조건 | 처리 | 다음 상태 |\n" +
        "| 대기 | 시작 요청 | 문이 닫힘 | 장비를 시작하고 작업자에게 알림 | 운전 |\n" +
        "| 대기 | 시작 요청 | 문이 열림 | 시작을 차단하고 오류 응답 | 대기 |\n" +
        "| 운전 | 정지 요청 | 항상 | 장비를 정지하고 결과 알림 | 대기 |\n" +
        "운전 중 오류가 발생하면 장비를 정지하고 오류 상태로 전환한다. 작업자가 오류를 확인하고 초기화하면 대기로 복귀한다.\n" +
        "문이 열려 있으면 모든 시작 경로에서 운전을 차단한다. 제어기는 현재 상태와 문 열림 여부를 보관한다.";

    public async Task<NaturalDiagramTestResult> RunAsync(CancellationToken ct)
    {
        // A test cannot silently expand the administrator's configured budget.
        var testOptions = JsonSerializer.Deserialize<LlmOptions>(JsonSerializer.Serialize(options.Value))!;
        testOptions.SemanticJobBudgetSeconds = Math.Min(900, testOptions.SemanticJobBudgetSeconds);
        using var execution = new SemanticExecution(testOptions, null, ct);
        await using var store = new InMemoryAppStore();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, compiler, store, cache, presets, Options.Create(testOptions), environment);
        var cases = new List<NaturalDiagramTestCase>();
        var inputs = new[] { (Id: "short-approval", Prompt: ShortPrompt, Types: new[] { "flowchart" }),
            (Id: "table-interlock", Prompt: TablePrompt, Types: new[] { "flowchart", "sequence", "state", "class" }) };
        foreach (var input in inputs)
        {
            if (execution.RequestFailure is not null || execution.Token.IsCancellationRequested)
            { cases.Add(new(input.Id, "Skipped", 0, 0, execution.RequestFailure?.Code ?? "NATURAL_EXECUTION_BUDGET")); continue; }
            var diagnosticStart = execution.Diagnostics.Count;
            try
            {
                if (!llm.IsEnabled) throw new LlmClientException("LLM_DISABLED", "The internal LLM is disabled.");
                var record = await service.GenerateAsync(new(input.Prompt, Views: input.Types.Select((type, index) =>
                    new DiagramViewSelection("test-" + index, type, "balanced")).ToArray()), "synthetic-natural-test", execution.Token);
                var pages = (record.Views ?? []).SelectMany(view => view.Pages ?? []).ToArray();
                var reviewed = pages.Count(page => page.State == "Completed" && page.DesignQuality?.Status == "Reviewed");
                var success = record.Views?.Count == input.Types.Length && record.Views.All(view => view.State == "Completed") &&
                    pages.Length > 0 && reviewed == pages.Length;
                cases.Add(new(input.Id, success ? "Completed" : "Partial", pages.Length, reviewed,
                    record.Views?.FirstOrDefault(view => view.ErrorCode is not null)?.ErrorCode));
            }
            catch (Exception error) when (error is LlmClientException or OperationCanceledException or DiagramValidationException or InvalidOperationException)
            {
                var code = error is LlmClientException known ? known.Code : error is OperationCanceledException ? "NATURAL_EXECUTION_BUDGET" : "NATURAL_INTERNAL_ERROR";
                if (error is LlmClientException operational && LlmFailure.StopsRequests(operational)) execution.StopRequests(operational);
                cases.Add(new(input.Id, "Failed", 0, 0, code));
                if (!execution.Diagnostics.Any(d => d.ErrorCode == code))
                    await execution.RecordAsync(new(Guid.NewGuid().ToString("N"), "natural-test", input.Id, "Failed", DateTimeOffset.UtcNow,
                        ErrorCode: code, Purpose: "natural-test", ProtocolVersion: NaturalDesignValidation.Protocol,
                        RecoveryState: code == "LLM_DISABLED" ? "RequiresAction" : "Interrupted", NextAction: "CheckSettings", Kind: "Terminal"));
            }
            cases[^1] = DescribeCase(cases[^1], execution.Diagnostics.Skip(diagnosticStart).ToArray());
        }
        var successAll = cases.All(item => item.State == "Completed");
        await execution.FinishNaturalAsync(successAll ? "Recovered" : "Exhausted");
        var settings = LlmDiagnosticReport.Settings(testOptions);
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(NaturalDiagramSelfTest).Assembly)?.InformationalVersion;
        var summary = $"Build: {version}; generator: {NaturalDiagramService.GeneratorVersion}; protocol: {NaturalDesignValidation.Protocol}\n" +
            string.Join("\n", cases.Select(item => $"{item.Id}: {item.State}; stage={item.FailureStage ?? "none"}; reason={item.ValidationCode ?? item.ErrorCode ?? "none"}; " +
                $"extract={item.Extraction?.ExtractionAttempts ?? 0}; review={item.Extraction?.ReviewAttempts ?? 0}; " +
                $"grounded={item.Extraction?.Grounded?.ToString() ?? "not-recorded"}; replaced={item.Extraction?.Replaced?.ToString() ?? "not-recorded"}; " +
                $"issues={string.Join(',', (item.IssueCounts ?? new Dictionary<string, int>()).Select(pair => pair.Key + ":" + pair.Value))}"));
        var report = $"Fixed synthetic natural diagram test; Thinking OFF\nBuild: {version}; generator: {NaturalDiagramService.GeneratorVersion}; protocol: {NaturalDesignValidation.Protocol}\n" +
            "Settings: " + JsonSerializer.Serialize(settings) + "\n" + string.Join("\n", cases.Select(item =>
                $"{item.Id}: {item.State}; reviewed pages: {item.ReviewedPages}/{item.Pages}; stop: {item.ErrorCode ?? "not-applicable"}")) + "\n" +
            "Copy summary:\n" + summary + "\n" + LlmDiagnosticReport.Text(successAll ? "Completed" : "Failed", cases.FirstOrDefault(item => item.ErrorCode is not null)?.ErrorCode,
                execution.Progress, execution.Diagnostics);
        return new(successAll, true, false, cases, settings, execution.Progress, execution.Diagnostics, report, summary);
    }

    internal static NaturalDiagramTestCase DescribeCase(NaturalDiagramTestCase item, IReadOnlyList<LlmDiagnostic> diagnostics)
    {
        var failure = item.State == "Completed" ? null : diagnostics.LastOrDefault(d => d.Kind == "Terminal") ??
            diagnostics.LastOrDefault(d => d.ErrorCode is not null && d.RecoveryState != "Recovered");
        var metrics = diagnostics.LastOrDefault(d => d.Extraction is not null)?.Extraction;
        if (metrics is not null) metrics = metrics with {
            ExtractionAttempts = diagnostics.Count(d => d.Kind == "Request" && d.Purpose is "requirements" or "requirements-repair"),
            ReviewAttempts = diagnostics.Count(d => d.Kind == "Request" && d.Purpose == "requirements-review") };
        return item with { FailureStage = failure?.Purpose, ValidationCode = failure?.ValidationCode is { } code ? NaturalDesignValidation.DiagnosticCode(code) : null,
            Extraction = metrics, IssueCounts = diagnostics.Where(d => d.ErrorCode is not null && d.Kind != "Terminal" && d.ValidationCode is not null)
                .GroupBy(d => NaturalDesignValidation.DiagnosticCode(d.ValidationCode)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()) };
    }
}

public sealed record NaturalDiagramTestCase(string Id, string State, int Pages, int ReviewedPages, string? ErrorCode = null,
    string? FailureStage = null, string? ValidationCode = null, NaturalExtractionMetrics? Extraction = null,
    IReadOnlyDictionary<string, int>? IssueCounts = null);
public sealed record NaturalDiagramTestResult(bool Success, bool SyntheticOnly, bool ThinkingEnabled,
    IReadOnlyList<NaturalDiagramTestCase> Cases, object Settings, SemanticProgress Execution, IReadOnlyList<LlmDiagnostic> Diagnostics, string Report,
    string? Summary = null);
