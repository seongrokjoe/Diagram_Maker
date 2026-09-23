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

    internal const string BufferPrompt = "Load Buffer, Dipping Buffer, WTR이 있다.\n" +
        "Load Buffer에 Wafer가 모두 적재되면,\n" +
        "스케쥴러는 Load Buffer에게 Transfer 명령을 전달한다.\n" +
        "Load Buffer는 Trasnfer 명령을 받으면 WTR Unit과 투입 Handshake를 시작하고,\n" +
        "완료되면 WTR은 LoadBuffer의 Wafer를 가져간다.\n" +
        "이후 DippingBuffer에 작업중인 Wafer가 없으면,\n" +
        "WTR은 Dipping Buffer에게 배출 Handshake를 요청하고,\n" +
        "배출 Handshake가 완료되면 Dipping Buffer에 Wafer를 가져다 놓는다.";
    public static bool ValidSelection(string? caseId, string? diagramType) =>
        (caseId is null or "short-approval" or "table-interlock" or "buffer-handshake") &&
        (diagramType is null or "flowchart" or "sequence" or "state" or "class") &&
        (caseId != "short-approval" || diagramType is null or "flowchart") &&
        (caseId != "buffer-handshake" || diagramType is null or "sequence");

    public async Task<NaturalDiagramTestResult> RunAsync(CancellationToken ct, string? caseId = null,
        string? diagramType = null, Action<NaturalDiagramTestEvent>? reportProgress = null)
    {
        if (!ValidSelection(caseId, diagramType)) throw new ArgumentException("Invalid synthetic test selection.");
        // A test cannot silently expand the administrator's configured budget.
        var testOptions = JsonSerializer.Deserialize<LlmOptions>(JsonSerializer.Serialize(options.Value))!;
        testOptions.SemanticJobBudgetSeconds = Math.Min(900, testOptions.SemanticJobBudgetSeconds);
        SemanticExecution? active = null;
        string? activeCase = null;
        var completedCases = new List<NaturalDiagramTestCase>();
        NaturalGenerationProgress? latest = null;
        void Report(string kind, NaturalDiagramTestCase? completed = null)
        {
            if (active is null) return;
            reportProgress?.Invoke(new(kind, activeCase, latest?.StageMessage ?? active.Stage,
                active.Progress, completed, CompletedPages: latest?.Views.Sum(v => v.Pages?.Count(p => p.State == "Completed") ?? 0) ?? 0,
                TotalUnits: latest?.TotalUnits ?? 0));
        }
        using var execution = new SemanticExecution(testOptions, null, ct, () => { Report("progress"); return Task.CompletedTask; });
        active = execution;
        await using var store = new InMemoryAppStore();
        using var cache = new NaturalDiagramSessionCache();
        var service = new NaturalDiagramService(llm, compiler, store, cache, presets, Options.Create(testOptions), environment);
        var cases = completedCases;
        var inputs = new[] { (Id: "short-approval", Prompt: ShortPrompt, Types: new[] { "flowchart" }),
            (Id: "table-interlock", Prompt: TablePrompt, Types: new[] { "flowchart", "sequence", "state", "class" }),
            (Id: "buffer-handshake", Prompt: BufferPrompt, Types: new[] { "sequence" }) };
        foreach (var input in inputs)
        {
            if (caseId is not null && caseId != input.Id || diagramType is not null && !input.Types.Contains(diagramType)) continue;
            var types = diagramType is null ? input.Types : [diagramType];
            activeCase = input.Id;
            latest = null;
            var remaining = Math.Max(0, execution.BudgetSeconds - execution.Progress.ElapsedSeconds);
            if (execution.RequestFailure is not null || execution.Token.IsCancellationRequested)
            {
                cases.Add(new(input.Id, "Skipped", 0, 0, execution.RequestFailure?.Code ?? (ct.IsCancellationRequested ? "NATURAL_CANCELLED" : "NATURAL_EXECUTION_BUDGET"),
                    RemainingSecondsAtStart: remaining, Types: types));
                Report("case-completed", cases[^1]); continue;
            }
            Report("progress");
            var diagnosticStart = execution.Diagnostics.Count;
            var formatStart = execution.Progress.RepairBudgets?.Sum(b => b.FormatUsed) ?? 0;
            var contentStart = execution.Progress.RepairBudgets?.Sum(b => b.ContentUsed) ?? 0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!llm.IsEnabled) throw new LlmClientException("LLM_DISABLED", "The internal LLM is disabled.");
                var record = await service.GenerateAsync(new(input.Prompt, Views: types.Select((type, index) =>
                    new DiagramViewSelection("test-" + index, type, "balanced")).ToArray()), "synthetic-natural-test", execution.Token,
                    (progress, token) => { token.ThrowIfCancellationRequested(); latest = progress; Report("progress"); return Task.CompletedTask; });
                execution.Token.ThrowIfCancellationRequested();
                var pages = (record.Views ?? []).SelectMany(view => view.Pages ?? []).ToArray();
                var reviewed = pages.Count(page => page.State == "Completed" && page.DesignQuality?.Status == "Reviewed");
                var success = record.Views?.Count == types.Length && record.Views.All(view => view.State == "Completed") &&
                    pages.Length > 0 && reviewed == pages.Length;
                cases.Add(new(input.Id, success ? "Completed" : "Partial", pages.Length, reviewed,
                    record.Views?.FirstOrDefault(view => view.ErrorCode is not null)?.ErrorCode));
            }
            catch (Exception error) when (error is LlmClientException or OperationCanceledException or DiagramValidationException or InvalidOperationException)
            {
                var code = error is LlmClientException known ? known.Code : error is OperationCanceledException
                    ? ct.IsCancellationRequested ? "NATURAL_CANCELLED" : "NATURAL_EXECUTION_BUDGET" : "NATURAL_INTERNAL_ERROR";
                if (error is LlmClientException operational && LlmFailure.StopsRequests(operational)) execution.StopRequests(operational);
                var pages = latest?.Views.SelectMany(v => v.Pages ?? []).ToArray() ?? [];
                cases.Add(new(input.Id, code == "NATURAL_CANCELLED" ? "Cancelled" : "Failed", pages.Length,
                    pages.Count(p => p.State == "Completed" && p.DesignQuality?.Status == "Reviewed"), code));
                if (!execution.Diagnostics.Any(d => d.ErrorCode == code))
                    await execution.RecordAsync(new(Guid.NewGuid().ToString("N"), "natural-test", input.Id, "Failed", DateTimeOffset.UtcNow,
                        ErrorCode: code, Purpose: "natural-test", ProtocolVersion: NaturalDesignValidation.Protocol,
                        RecoveryState: code == "LLM_DISABLED" ? "RequiresAction" : "Interrupted", NextAction: "CheckSettings", Kind: "Terminal"));
            }
            cases[^1] = DescribeCase(cases[^1] with { RemainingSecondsAtStart = remaining, Types = types,
                FormatRepairs = (execution.Progress.RepairBudgets?.Sum(b => b.FormatUsed) ?? 0) - formatStart,
                ContentRepairs = (execution.Progress.RepairBudgets?.Sum(b => b.ContentUsed) ?? 0) - contentStart,
                ElapsedMilliseconds = started.ElapsedMilliseconds }, execution.Diagnostics.Skip(diagnosticStart).ToArray());
            Report("case-completed", cases[^1]);
        }
        var successAll = cases.All(item => item.State == "Completed");
        await execution.FinishNaturalAsync(successAll ? "Recovered" : "Exhausted");
        var settings = LlmDiagnosticReport.Settings(testOptions);
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(NaturalDiagramSelfTest).Assembly)?.InformationalVersion;
        var summary = $"Build: {version}; generator: {NaturalDiagramService.GeneratorVersion}; protocol: {NaturalDesignValidation.Protocol}\n" +
            string.Join("\n", cases.Select(item => $"{item.Id}: {item.State}; stage={item.FailureStage ?? "none"}; reason={item.ValidationCode ?? item.ErrorCode ?? "none"}; " +
                $"types={string.Join(',', item.Types ?? [])}; confirm={((item.PhaseRequests ?? new Dictionary<string, int>()).GetValueOrDefault("scenario-review-confirm"))}; " +
                $"extract={item.Extraction?.ExtractionAttempts ?? 0}; review={item.Extraction?.ReviewAttempts ?? 0}; " +
                $"grounded={item.Extraction?.Grounded?.ToString() ?? "not-recorded"}; replaced={item.Extraction?.Replaced?.ToString() ?? "not-recorded"}; " +
                $"root={item.RootFailureCode ?? "none"}; last={item.LastFailureCode ?? "none"}; stop={item.ErrorCode ?? "none"}; " +
                $"remainingAtStart={item.RemainingSecondsAtStart}s; elapsed={item.ElapsedMilliseconds}ms; http={item.HttpRequests}; " +
                $"repairs(format/content)={item.FormatRepairs}/{item.ContentRepairs}; " +
                $"phases={string.Join(',', (item.PhaseRequests ?? new Dictionary<string, int>()).Select(p => p.Key + ":" + p.Value))}; " +
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
        var causes = diagnostics.Where(d => d.ErrorCode is not null && d.Kind != "Terminal" && d.Purpose != "natural-test").ToArray();
        var failure = item.State == "Completed" ? null : causes.LastOrDefault(d => d.RecoveryState != "Recovered") ?? causes.LastOrDefault() ??
            diagnostics.LastOrDefault(d => d.Kind == "Terminal");
        var metrics = diagnostics.LastOrDefault(d => d.Extraction is not null)?.Extraction;
        if (metrics is not null) metrics = metrics with {
            ExtractionAttempts = diagnostics.Count(d => d.Kind == "Request" && d.Purpose is "requirements" or "requirements-repair"),
            ReviewAttempts = diagnostics.Count(d => d.Kind == "Request" && d.Purpose == "requirements-review") };
        return item with { FailureStage = failure?.Purpose ?? failure?.Stage, ValidationCode = failure?.ValidationCode is { } code ? NaturalDesignValidation.DiagnosticCode(code) : null,
            RootFailureCode = causes.FirstOrDefault()?.ValidationCode ?? causes.FirstOrDefault()?.ErrorCode,
            LastFailureCode = causes.LastOrDefault()?.ValidationCode ?? causes.LastOrDefault()?.ErrorCode,
            HttpRequests = diagnostics.Sum(d => d.TransportAttempts > 0 ? d.TransportAttempts : d.Sent ? 1 + d.Retries : 0),
            PhaseRequests = diagnostics.Where(d => d.Kind == "Request").GroupBy(d => d.Purpose ?? d.Stage)
                .ToDictionary(g => g.Key, g => g.Count()),
            Extraction = metrics, IssueCounts = diagnostics.Where(d => d.ErrorCode is not null && d.Kind != "Terminal" && d.ValidationCode is not null)
                .GroupBy(d => NaturalDesignValidation.DiagnosticCode(d.ValidationCode)).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()) };
    }
}

public sealed record NaturalDiagramTestCase(string Id, string State, int Pages, int ReviewedPages, string? ErrorCode = null,
    string? FailureStage = null, string? ValidationCode = null, NaturalExtractionMetrics? Extraction = null,
    IReadOnlyDictionary<string, int>? IssueCounts = null, string? RootFailureCode = null, string? LastFailureCode = null,
    long RemainingSecondsAtStart = 0, long ElapsedMilliseconds = 0, int HttpRequests = 0,
    IReadOnlyDictionary<string, int>? PhaseRequests = null, IReadOnlyList<string>? Types = null, int FormatRepairs = 0, int ContentRepairs = 0);
public sealed record NaturalDiagramTestEvent(string Type, string? CaseId, string Stage, SemanticProgress Execution,
    NaturalDiagramTestCase? Case = null, NaturalDiagramTestResult? Result = null, int CompletedPages = 0, int TotalUnits = 0);
public sealed record NaturalDiagramTestResult(bool Success, bool SyntheticOnly, bool ThinkingEnabled,
    IReadOnlyList<NaturalDiagramTestCase> Cases, object Settings, SemanticProgress Execution, IReadOnlyList<LlmDiagnostic> Diagnostics, string Report,
    string? Summary = null);
