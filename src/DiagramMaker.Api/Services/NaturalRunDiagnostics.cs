using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static class NaturalRunDiagnostics
{
    internal static int Unresolved(NaturalDiagramRun run) => Math.Max(Unresolved(run.Diagnostics ?? []),
        (run.Views ?? []).Sum(view => view.Pages is { Count: > 0 } pages
            ? pages.Count(page => page.State is "Failed" or "Partial") : view.State is "Failed" or "Partial" ? 1 : 0));

    internal static int Unresolved(IReadOnlyList<LlmDiagnostic> records)
    {
        var pending = records.Where(d => d.ErrorCode is not null && d.RecoveryState != "Recovered").ToArray();
        return pending.Where(d => d.Kind != "Terminal" || d.RootDiagnosticId is null || !pending.Any(p => p.Id == d.RootDiagnosticId))
            .Select(d => d.RootDiagnosticId ?? string.Join(':', d.PageId ?? d.RecoveryGroupId ?? d.Id,
                d.ValidationDetails?.TargetId ?? "", d.ValidationDetails?.Field ?? "", d.ValidationCode ?? d.ErrorCode))
            .Distinct(StringComparer.Ordinal).Count();
    }

    internal static string Summary(NaturalDiagramRun run)
    {
        var diagnostics = run.Diagnostics ?? [];
        var failures = diagnostics.Where(d => d.ErrorCode is not null && d.RecoveryState != "Recovered").ToArray();
        var first = failures.FirstOrDefault(d => d.Kind != "Terminal") ?? failures.FirstOrDefault();
        var last = failures.LastOrDefault(d => d.Kind == "Terminal") ?? failures.LastOrDefault();
        var output = diagnostics.LastOrDefault(d => d.FinishReason == "length") ?? diagnostics.LastOrDefault(d => d.OutputLimit is not null);
        var pages = (run.Views ?? []).SelectMany(v => v.Pages ?? []).ToArray();
        return $"DiagramMaker {NaturalDiagramService.GeneratorVersion} / {NaturalDesignValidation.Protocol}\n" +
            $"실행: {run.Id}; Thinking: {(run.Request.EnableThinking ? "ON" : "OFF")}; 상태: {run.State}\n" +
            $"완료 {pages.Count(p => p.State == "Completed")} / 부분 {pages.Count(p => p.State == "Partial")} / 실패 {pages.Count(p => p.State == "Failed")}; 미해결 {Unresolved(run)}\n" +
            $"최초: {first?.ErrorCode ?? "없음"} / {first?.ValidationCode ?? "없음"}; 종료: {last?.ErrorCode ?? run.ErrorCode ?? "없음"} / {last?.ValidationCode ?? "없음"}\n" +
            $"출력 한도/사용: {output?.OutputLimit?.ToString() ?? "미기록"}/{output?.CompletionTokens?.ToString() ?? "미기록"}; 단계: {last?.Purpose ?? "완료"}; 예외: {last?.ExceptionKind ?? "없음"}\n" +
            $"경과: {run.Execution?.TotalElapsedSeconds ?? 0}초; LLM 요청: {diagnostics.Count(d => d.Kind == "Request")}";
    }
}
