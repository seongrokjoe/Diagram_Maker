import { useEffect, useState } from "react";

type FailureDiagnostic = {
  id: string; stage: string; errorCode?: string; validationCode?: string; purpose?: string; sent: boolean;
  inputCharacters?: number; inputCharacterLimit?: number; inputTokens?: number; inputTokenLimit?: number;
  outputLimit?: number; completionTokens?: number; finishReason?: string; outputMode?: string;
  protocolVersion?: string; recoveryGroupId?: string; parentGroupId?: string; attempt?: number;
  recoveryState?: string; requiredOutputTokens?: number; state?: string; estimatedInputTokens?: boolean;
  httpStatus?: number; serverErrorCategory?: string; nextAction?: string; schemaRelaxed?: boolean;
  validationDetails?: { expectedItems: number; receivedItems: number; missingItems: number; duplicateItems: number;
    unknownItems: number; field?: string; itemIndex?: number; actualLength?: number; allowedLength?: number;
    nonTargetItems?: number; unknownAliases?: number; issueCodes?: string[] };
};

export type SemanticProgress = {
  stage: string; completedUnits: number; reusedUnits: number; requests: number;
  elapsedSeconds: number; budgetSeconds: number; totalUnits?: number;
  attemptCompletedUnits?: number; attemptRequests?: number; totalElapsedSeconds?: number;
  attemptNumber?: number; startedAt?: string; transportRequests?: number; tokenizationRequests?: number;
  failedUnits?: number; rejectedBeforeSend?: number; attemptTransportRequests?: number;
  attemptTokenizationRequests?: number; waitMilliseconds?: number; attemptWaitMilliseconds?: number;
  waitingSince?: string; recentFailures?: FailureDiagnostic[];
  lastRequest?: FailureDiagnostic; protocolUpgraded?: boolean;
  lastProgressAt?: string; coverage?: { totalUnits: number; verifiedUnits: number; pendingUnits: number; failedUnits: number };
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
  const failures = value.recentFailures ?? [];
  const renderFailures = (records: FailureDiagnostic[]) => records.map(failure => <div key={failure.id}>
    <p>{failures[0]?.id === failure.id ? "최초 기록" : "후속 기록"} · {purpose(failure)} · {failure.sent ? "전송 후" : "전송 전"}: {failureDescription(failure.validationCode, failure.errorCode)}</p>
    <p style={{ overflowWrap: "anywhere" }}>{failure.errorCode}{failure.validationCode && ` / ${failure.validationCode}`}</p>
    {failure.recoveryGroupId && <p style={{ overflowWrap: "anywhere" }}>묶음 {failure.recoveryGroupId}{failure.parentGroupId && ` · 상위 ${failure.parentGroupId}`} · 시도 {failure.attempt ?? 1}
      {failure.recoveryState === "Retrying" ? (running ? " · 복구 중" : " · 복구 대기") : failure.recoveryState === "Exhausted" ? " · 복구 한도 소진" : failure.recoveryState === "RequiresAction" ? " · 서버·설정 확인 필요" : failure.recoveryState === "Recovered" ? " · 복구 완료" : " · 상태 미확인"}</p>}
    <OutputDiagnostic value={failure} />
    {failure.inputCharacters != null && <p>요청 문자 수 {failure.inputCharacters.toLocaleString()}{failure.inputCharacterLimit != null && ` / 허용 ${failure.inputCharacterLimit.toLocaleString()}자`}</p>}
    {failure.inputTokens != null && <p>입력 토큰 {failure.inputTokens.toLocaleString()}{failure.estimatedInputTokens ? " (추정·예약 포함)" : ""}{failure.inputTokenLimit != null && ` / 허용 ${failure.inputTokenLimit.toLocaleString()}`}</p>}
    {failure.httpStatus != null && <p>서버 응답 HTTP {failure.httpStatus} · {serverFailure(failure.serverErrorCategory)}</p>}
    {failure.validationDetails && <>
      <p>항목: 필요 {failure.validationDetails.expectedItems}개 · 응답 {failure.validationDetails.receivedItems}개 · 누락 {failure.validationDetails.missingItems}개 · 중복 {failure.validationDetails.duplicateItems}개 · 알 수 없는 ID {failure.validationDetails.unknownItems}개</p>
      {failure.validationDetails.nonTargetItems != null && <p>다른 근거의 ID {failure.validationDetails.nonTargetItems}개 · 알 수 없는 별칭 {failure.validationDetails.unknownAliases ?? 0}개</p>}
      {failure.validationDetails.field && <p>검사 필드: {failure.validationDetails.field}{failure.validationDetails.itemIndex != null && ` · 항목 ${failure.validationDetails.itemIndex + 1}`}
        {failure.validationDetails.actualLength != null && ` · 길이 ${failure.validationDetails.actualLength} / ${failure.validationDetails.allowedLength}자`}</p>}
      {!!failure.validationDetails.issueCodes?.length && <p>검토 사유: {failure.validationDetails.issueCodes.map(issueDescription).join(", ")}</p>}
    </>}
  </div>);
  return <div className="help semantic-progress" aria-label="전체 및 이번 실행 진척">
    <div className="semantic-progress-current"><p role="status">{running ? "생성 중" : "실행 종료"} · {value.lastRequest ? purpose(value.lastRequest) : "코드 구조 준비"}
      {value.coverage && ` · 의미 설명 검토 ${value.coverage.verifiedUnits} / ${value.coverage.totalUnits}개`}</p>
      <p>경과 {Math.floor(elapsed / 60)}분 {elapsed % 60}초</p></div>
    {value.lastProgressAt && <p>최근 진행 <time dateTime={value.lastProgressAt}>{new Date(value.lastProgressAt).toLocaleTimeString()}</time>
      {value.coverage && ` · 남은 설명 ${value.coverage.pendingUnits}개 · 미완료 ${value.coverage.failedUnits}개`}</p>}
    <p>전체: 완료 {value.completedUnits}단위 · 요청 {value.requests}회 · 실제 전송 {value.transportRequests ?? value.requests}회 · 누적 {total}초</p>
    <p>이번 실행 {value.attemptNumber ?? 1}: 새 완료 {value.attemptCompletedUnits ?? value.completedUnits}단위 · 재사용 {value.reusedUnits}단위 · 요청 {value.attemptRequests ?? value.requests}회 · 실제 전송 {value.attemptTransportRequests ?? value.requests}회 · {elapsed} / {value.budgetSeconds}초</p>
    <p>LLM 대기: 전체 {Math.floor(((value.waitMilliseconds ?? 0) + waiting) / 1000)}초 · 이번 실행 {Math.floor(((value.attemptWaitMilliseconds ?? 0) + waiting) / 1000)}초
      {(value.failedUnits ?? 0) > 0 && ` · 실패 ${value.failedUnits}단위`}</p>
    {running && elapsed >= 300 && <p>5분이 지났습니다. 완료된 작업을 저장하면서 실행 한도까지 계속 생성합니다.</p>}
    {value.protocolUpgraded && <p>생성·검토 정책이 갱신되어 이전 실행의 일부 요청을 다시 수행합니다. 저장된 결과와 근거는 유지됩니다.</p>}
    {value.lastRequest && <div aria-label="최근 요청 출력 설정"><p>최근 요청 · {purpose(value.lastRequest)}</p><OutputDiagnostic value={value.lastRequest} /></div>}
    {!!failures.length && <details open aria-label="요청 오류 진단"><summary>요청 오류 진단 · 최초 및 최근 기록</summary>
      {!!failures.some(f => f.recoveryState && f.recoveryState !== "Recovered") && <div aria-label="미해결 기록"><p>미해결 기록</p>{renderFailures(failures.filter(f => f.recoveryState && f.recoveryState !== "Recovered"))}</div>}
      {!!failures.some(f => f.recoveryState === "Recovered") && <details aria-label="복구 완료 기록"><summary>복구 완료 기록</summary>{renderFailures(failures.filter(f => f.recoveryState === "Recovered"))}</details>}
      {!!failures.some(f => !f.recoveryState) && <div aria-label="과거 기록"><p>과거 기록 · 복구 상태 미확인</p>{renderFailures(failures.filter(f => !f.recoveryState))}</div>}
      <p>서로 다른 묶음의 기록이 포함될 수 있습니다. 표시된 기록은 일부이며, 최종 성공 여부는 작업 상태에서 확인하세요.</p>
    </details>}
  </div>;
}

function purpose(value: FailureDiagnostic) { return value.purpose === "execution-plan" ? "함수 실행 의미 계획" :
  value.purpose === "execution-review" ? "함수 전체 의미 검토" : value.purpose === "review" ? "의미 검토" : value.purpose === "repair" ? "응답 수정" : "의미 생성"; }

function OutputDiagnostic({ value }: { value: FailureDiagnostic }) {
  return <>{value.outputLimit != null && <p>출력 한도 {value.outputLimit.toLocaleString()}토큰 · 사용 {value.completionTokens?.toLocaleString() ?? "미확인"}토큰
    {value.requiredOutputTokens != null && ` · 필요한 보수적 예산 ${value.requiredOutputTokens.toLocaleString()}토큰`}</p>}
    {(value.finishReason || value.outputMode) && <p style={{ overflowWrap: "anywhere" }}>종료 사유 {value.finishReason ?? (value.errorCode || value.state === "Failed" ? "응답 없음" : value.state === "Completed" ? "미제공" : "대기 중")} · 출력 방식 {value.outputMode ?? "미확인"}{value.schemaRelaxed && " · 호환 스키마"}</p>}</>;
}

function serverFailure(category?: string) {
  return ({ authentication: "서버 접근 권한을 확인하세요.", model: "설정한 모델이 서버에서 제공되는지 확인하세요.",
    context: "서버 문맥 한도와 입력·출력 예약을 확인하세요.", "output-limit": "서버의 최대 출력 토큰 설정을 확인하세요.",
    "schema-constraint": "서버가 JSON 스키마 제약을 지원하지 않습니다. 서버 구조화 출력 설정을 확인하세요.",
    "output-field": "서버가 출력 방식 필드를 지원하지 않습니다. API 호환 설정을 확인하세요.",
    template: "서버 채팅 템플릿과 Thinking 지원을 확인하세요.", server: "서버 상태를 확인한 뒤 재개하세요.",
    capacity: "서버 대기열·사용량을 확인한 뒤 재개하세요." } as Record<string, string>)[category ?? ""] ??
    "오류 유형을 확인하지 못했습니다. 사내 서버에서 해당 시각의 오류 사유를 확인하세요.";
}

function issueDescription(code: string) {
  return ({ missing_action: "핵심 동작 누락", incorrect_outcome: "인수·결과 설명 오류", reversed_condition: "조건 반전",
    invented_call: "근거 없는 호출·순서", unsupported_role: "근거 없는 역할", mixed_scope: "함수 범위 혼합",
    incorrect_change: "변경 전후 설명 오류", insufficient_evidence: "근거 부족" } as Record<string, string>)[code] ?? "확인되지 않은 검토 사유";
}

function failureDescription(validation?: string, error?: string) {
  const descriptions: Record<string, string> = { SharedSummaryInvalid: "전체 요약이 비었거나 너무 깁니다.", SharedRecommendedTypeInvalid: "추천한 다이어그램 형식이 허용 목록과 다릅니다.",
    SharedItemsInvalid: "응답 항목 또는 ID가 비어 있습니다.", SharedUnknownIds: "요청에 없는 ID가 응답에 포함됐습니다.",
    SharedDuplicateIds: "같은 ID가 응답에 반복됐습니다.", SharedMissingIds: "요청한 항목 일부가 응답에서 빠졌습니다.",
    SharedItemCountMismatch: "요청과 응답의 항목 수가 다릅니다.", SharedTextEmpty: "요약 또는 설명이 비어 있습니다.",
    SharedTextTooLong: "요약 또는 설명이 길이 제한을 넘었습니다.", SharedTextNotKorean: "요약 또는 설명에 한국어가 없습니다.",
    SharedTextCodeSyntax: "요약 또는 설명에 코드 연산자나 마크업이 포함됐습니다.", SharedSemanticCoverage: "이전 버전의 응답 항목·표현 검증에 실패했습니다.",
    LLM_INPUT_CHARACTERS: "요청의 문자 수 제한을 넘었습니다.", LLM_INPUT_LIMIT: "입력 토큰 또는 전체 문맥 한도를 넘었습니다.",
    LLM_CONTEXT_LIMIT: "서버에서 전체 문맥 한도를 초과했다고 응답했습니다.", LLM_RESPONSE_TRUNCATED: "출력 토큰 한도에서 응답이 잘렸습니다.",
    LLM_OUTPUT_BUDGET: "한 항목의 검토에도 출력 예산이 부족합니다. 검토 출력 설정을 확인하세요.",
    SemanticReviewRejected: "생성한 설명의 일부 항목이 원본 의미 검토를 통과하지 못했습니다.",
    InvalidReview: "검토 응답의 승인 값과 문제 목록이 모순되거나 형식이 잘못됐습니다.",
    SharedReviewFieldsInvalid: "항목별 검토 응답의 필수 필드나 형식이 잘못됐습니다.",
    SharedReviewUnknownIds: "검토 응답에 요청하지 않은 항목 ID가 있습니다.", SharedReviewMissingIds: "일부 항목의 검토 결과가 빠졌습니다.",
    SharedReviewDuplicateIds: "동일 항목의 검토 결과가 반복됐습니다.", SharedReviewIssuesInvalid: "검토 문제 코드가 허용 목록이나 개수 제한을 벗어났습니다.",
    LLM_SCHEMA_REVIEW: "생성한 설명이 원본 의미 검토를 통과하지 못했습니다.",
    LLM_SEMANTIC_REVIEW: "생성한 설명이 원본 의미 검토를 통과하지 못했습니다." };
  return descriptions[validation ?? ""] ?? descriptions[error ?? ""] ?? "요청 또는 응답 검증에 실패했습니다.";
}
