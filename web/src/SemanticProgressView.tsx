import { useEffect, useState } from "react";

type FailureDiagnostic = {
  id: string; stage: string; errorCode?: string; validationCode?: string; purpose?: string; sent: boolean;
  inputCharacters?: number; inputCharacterLimit?: number; inputTokens?: number; inputTokenLimit?: number;
  validationDetails?: { expectedItems: number; receivedItems: number; missingItems: number; duplicateItems: number;
    unknownItems: number; field?: string; itemIndex?: number; actualLength?: number; allowedLength?: number };
};

export type SemanticProgress = {
  stage: string; completedUnits: number; reusedUnits: number; requests: number;
  elapsedSeconds: number; budgetSeconds: number; totalUnits?: number;
  attemptCompletedUnits?: number; attemptRequests?: number; totalElapsedSeconds?: number;
  attemptNumber?: number; startedAt?: string; transportRequests?: number; tokenizationRequests?: number;
  failedUnits?: number; rejectedBeforeSend?: number; attemptTransportRequests?: number;
  attemptTokenizationRequests?: number; waitMilliseconds?: number; attemptWaitMilliseconds?: number;
  waitingSince?: string; recentFailures?: FailureDiagnostic[];
};

export function SemanticProgressView({ value, running }: { value: SemanticProgress; running: boolean }) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);
  const elapsed = running && value.startedAt
    ? Math.max(value.elapsedSeconds, Math.floor((now - Date.parse(value.startedAt)) / 1000)) : value.elapsedSeconds;
  const total = (value.totalElapsedSeconds ?? value.elapsedSeconds) + elapsed - value.elapsedSeconds;
  const waiting = running && value.waitingSince ? Math.max(0, now - Date.parse(value.waitingSince)) : 0;
  return <div className="help semantic-progress" aria-label="전체 및 이번 실행 진척">
    <p>전체: 완료 {value.completedUnits}단위 · 요청 {value.requests}회 · 실제 전송 {value.transportRequests ?? value.requests}회 · 누적 {total}초</p>
    <p>이번 실행 {value.attemptNumber ?? 1}: 새 완료 {value.attemptCompletedUnits ?? value.completedUnits}단위 · 재사용 {value.reusedUnits}단위 · 요청 {value.attemptRequests ?? value.requests}회 · 실제 전송 {value.attemptTransportRequests ?? value.requests}회 · {elapsed} / {value.budgetSeconds}초</p>
    <p>LLM 대기: 전체 {Math.floor(((value.waitMilliseconds ?? 0) + waiting) / 1000)}초 · 이번 실행 {Math.floor(((value.attemptWaitMilliseconds ?? 0) + waiting) / 1000)}초
      {(value.failedUnits ?? 0) > 0 && ` · 실패 ${value.failedUnits}단위`}</p>
    {running && elapsed >= 300 && <p>5분이 지났습니다. 완료된 작업을 저장하면서 실행 한도까지 계속 생성합니다.</p>}
    {!!value.recentFailures?.length && <details open aria-label="요청 오류 진단"><summary>요청 오류 진단 · 최초 및 최근 기록</summary>
      {value.recentFailures.map((failure, index) => <div key={failure.id}>
        <p>{index === 0 ? "최초 기록" : "후속 기록"} · {failure.purpose === "review" ? "의미 검토" : failure.purpose === "repair" ? "응답 수정" : "의미 생성"} · {failure.sent ? "전송 후" : "전송 전"}: {failureDescription(failure.validationCode ?? failure.errorCode ?? "")}</p>
        <p style={{ overflowWrap: "anywhere" }}>{failure.errorCode}{failure.validationCode && ` / ${failure.validationCode}`}</p>
        {failure.inputCharacters != null && <p>요청 문자 수 {failure.inputCharacters.toLocaleString()}{failure.inputCharacterLimit != null && ` / 허용 ${failure.inputCharacterLimit.toLocaleString()}자`}</p>}
        {failure.inputTokens != null && <p>입력 토큰 {failure.inputTokens.toLocaleString()}{failure.inputTokenLimit != null && ` / 허용 ${failure.inputTokenLimit.toLocaleString()}`}</p>}
        {failure.validationDetails && <>
          <p>항목: 필요 {failure.validationDetails.expectedItems}개 · 응답 {failure.validationDetails.receivedItems}개 · 누락 {failure.validationDetails.missingItems}개 · 중복 {failure.validationDetails.duplicateItems}개 · 알 수 없는 ID {failure.validationDetails.unknownItems}개</p>
          {failure.validationDetails.field && <p>검사 필드: {failure.validationDetails.field}{failure.validationDetails.itemIndex != null && ` · 항목 ${failure.validationDetails.itemIndex + 1}`}
            {failure.validationDetails.actualLength != null && ` · 길이 ${failure.validationDetails.actualLength} / ${failure.validationDetails.allowedLength}자`}</p>}
        </>}
      </div>)}
      <p>처리 중 발생한 기록입니다. 재시도 후의 최종 성공 여부는 작업 상태에서 확인하세요.</p>
    </details>}
  </div>;
}

function failureDescription(code: string) {
  return ({ SharedSummaryInvalid: "전체 요약이 비었거나 너무 깁니다.", SharedRecommendedTypeInvalid: "추천한 다이어그램 형식이 허용 목록과 다릅니다.",
    SharedItemsInvalid: "응답 항목 또는 ID가 비어 있습니다.", SharedUnknownIds: "요청에 없는 ID가 응답에 포함됐습니다.",
    SharedDuplicateIds: "같은 ID가 응답에 반복됐습니다.", SharedMissingIds: "요청한 항목 일부가 응답에서 빠졌습니다.",
    SharedItemCountMismatch: "요청과 응답의 항목 수가 다릅니다.", SharedTextEmpty: "요약 또는 설명이 비어 있습니다.",
    SharedTextTooLong: "요약 또는 설명이 길이 제한을 넘었습니다.", SharedTextNotKorean: "요약 또는 설명에 한국어가 없습니다.",
    SharedTextCodeSyntax: "요약 또는 설명에 코드 연산자나 마크업이 포함됐습니다.", SharedSemanticCoverage: "이전 버전의 응답 항목·표현 검증에 실패했습니다.",
    LLM_INPUT_CHARACTERS: "요청의 문자 수 제한을 넘었습니다.", LLM_INPUT_LIMIT: "입력 토큰 또는 전체 문맥 한도를 넘었습니다.",
    LLM_CONTEXT_LIMIT: "서버에서 전체 문맥 한도를 초과했다고 응답했습니다.", LLM_RESPONSE_TRUNCATED: "출력 토큰 한도에서 응답이 잘렸습니다.",
    LLM_SEMANTIC_REVIEW: "생성한 설명이 원본 의미 검토를 통과하지 못했습니다." } as Record<string, string>)[code] ?? "요청 또는 응답 검증에 실패했습니다.";
}
