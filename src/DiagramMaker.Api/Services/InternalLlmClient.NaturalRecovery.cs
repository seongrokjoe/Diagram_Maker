using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    private static LlmClientException NaturalFailure(string code, string? validation, LlmValidationDetails? details = null) =>
        new(code, $"{NaturalFailureLabel(code)} 보정 한도를 소진했습니다. 마지막 정상 결과는 보존됩니다. " +
            $"진단: {NaturalFailureReason(validation)} ({NaturalDesignValidation.DiagnosticCode(validation)}). 검사 요약으로 실패 원인을 확인할 수 있습니다.",
            failureKind: NaturalDesignValidation.DiagnosticCode(validation), validationDetails: details);

    internal static string NaturalFailureReason(string? code) => code switch {
        "NaturalRequirementOmitted" => "원문 요구사항 누락",
        "NaturalConditionChanged" => "원문 조건 또는 전이 변경",
        "NaturalUnsupportedClaim" => "원문에 없는 요구사항 추가",
        "NaturalEvidenceMismatch" => "요구사항과 원문 근거 불일치",
        "NaturalEntityMismatch" => "원문 엔터티 불일치",
        "NaturalScenarioMismatch" => "시나리오 연결 불일치",
        "NaturalRepairNoProgress" => "보정 응답에 수정 내용이 반영되지 않음",
        "NaturalExplicitRequirementsMissing" => "원문에 연결된 요구사항 없음",
        "NaturalFieldUnexpected" => "응답에 계약 밖의 필드 포함",
        _ => "요구사항 또는 검토 응답 검증 실패"
    };

    private static string NaturalFailureLabel(string code) => code switch {
        "NATURAL_REQUIREMENTS_INVALID" => "요구사항의 응답 형식 또는 원문 근거 검증에 실패했습니다.",
        "NATURAL_PLAN_INVALID" => "요구사항과 시나리오의 연결 검증에 실패했습니다.",
        "NATURAL_REQUIREMENTS_REVIEW_INVALID" => "요구사항 검토 응답의 형식 또는 검토 범위가 잘못되었습니다.",
        "NATURAL_REQUIREMENTS_REJECTED" => "추출한 요구사항이 원문 의미 검토를 통과하지 못했습니다.",
        "NATURAL_DESIGN_REVIEW_INVALID" => "다이어그램 검토 응답의 형식 또는 검토 범위가 잘못되었습니다.",
        "NATURAL_DESIGN_REJECTED" => "다이어그램이 요구사항 의미 검토를 통과하지 못했습니다.",
        "NATURAL_CROSS_VIEW_REVIEW" => "형식 간 누락·모순 검토를 통과하지 못했습니다.",
        _ => "다이어그램 구조 검증에 실패했습니다."
    };

    private static async Task<T> NaturalOperation<T>(string stage, string key, Func<Task<T>> work)
    {
        var execution = SemanticExecution.Current;
        if (execution is null) return await work();
        var group = SemanticExecution.Hash(NaturalDesignValidation.Protocol + stage + key);
        using var scope = execution.BeginRequestScope(group, null, 1, NaturalDesignValidation.Protocol);
        try
        {
            var value = await work();
            await execution.SetRecoveryAsync(group, "Recovered", descendants: true);
            return value;
        }
        catch (Exception exception)
        {
            var error = exception as LlmClientException;
            var interrupted = exception is OperationCanceledException || error?.Code is "LLM_REQUEST_TIMEOUT" or "LLM_NO_RESPONSE_TIMEOUT" or "LLM_TRANSPORT" ||
                error?.ServerErrorCategory is "server" or "capacity";
            var state = interrupted ? "Interrupted" : error is not null && LlmFailure.StopsRequests(error) ? "RequiresAction" : "Exhausted";
            await execution.SetRecoveryAsync(group, state, descendants: true);
            var last = execution.Diagnostics.LastOrDefault(d => d.ErrorCode is not null && d.Kind != "Terminal" &&
                (d.RecoveryGroupId == group || d.ParentGroupId == group || d.AncestorGroupIds?.Contains(group) == true));
            var id = SemanticExecution.Hash(group + ":terminal:" + execution.Progress.AttemptNumber);
            await execution.RecordAsync(new(id, "natural-" + stage, last?.UnitId ?? execution.UnitId, "Failed", DateTimeOffset.UtcNow,
                ErrorCode: error?.Code ?? (interrupted ? "NATURAL_INTERRUPTED" : "NATURAL_INTERNAL_ERROR"),
                ValidationCode: error?.FailureKind ?? last?.ValidationCode, Purpose: stage + "-result",
                ValidationDetails: error?.ValidationDetails ?? last?.ValidationDetails,
                Attempt: last?.Attempt,
                RecoveryState: state, NextAction: state == "Interrupted" ? "Resume" : state == "RequiresAction" ? "CheckSettings" : "StartNewRun",
                Kind: "Terminal", RequestId: last?.RequestId ?? last?.Id,
                Extraction: execution.Diagnostics.LastOrDefault(d => d.Extraction is not null &&
                    (d.RecoveryGroupId == group || d.AncestorGroupIds?.Contains(group) == true))?.Extraction));
            throw;
        }
    }
}
