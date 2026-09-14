import { useEffect, useState } from "react";

export type FailureDiagnostic = {
  startedAt?: string; elapsedMilliseconds?: number;
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

export function SemanticProgressView({ value, running, diagnosticsUrl }: { value: SemanticProgress; running: boolean; diagnosticsUrl?: string }) {
  const [now, setNow] = useState(Date.now);
  const [records, setRecords] = useState<FailureDiagnostic[]>([]);
  const [loadError, setLoadError] = useState(false);
  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);
  useEffect(() => {
    setRecords([]); setLoadError(false);
    if (!diagnosticsUrl) return;
    const controller = new AbortController(); let pending = false;
    async function load() {
      if (pending) return; pending = true;
      try {
        const response = await fetch(diagnosticsUrl!, { signal: controller.signal });
        if (!response.ok) throw new Error("diagnostics");
        const report = await response.json() as { diagnostics: FailureDiagnostic[] };
        if (!controller.signal.aborted) { setRecords(report.diagnostics.filter(d => d.errorCode)); setLoadError(false); }
      } catch { if (!controller.signal.aborted) setLoadError(true); }
      finally { pending = false; }
    }
    void load(); const timer = running ? window.setInterval(() => void load(), 2000) : undefined;
    return () => { controller.abort(); if (timer) window.clearInterval(timer); };
  }, [diagnosticsUrl, running]);
  const elapsed = running && value.startedAt ? Math.max(value.elapsedSeconds, Math.floor((now - Date.parse(value.startedAt)) / 1000)) : value.elapsedSeconds;
  const failures = diagnosticsUrl ? records : value.recentFailures ?? [];
  return <div className="help semantic-progress" aria-label="전체 및 이번 실행 진척">
    <div className="semantic-progress-current"><p role="status">{running ? "생성 중" : "실행 종료"} · {value.lastRequest ? purpose(value.lastRequest) : "코드 구조 준비"}
      {value.coverage && " · 의미 설명 검토 " + value.coverage.verifiedUnits + " / " + value.coverage.totalUnits + "개"}</p>
      <p>경과 {Math.floor(elapsed / 60)}분 {elapsed % 60}초</p></div>
    {value.coverage && <p>검토 통과 {value.coverage.verifiedUnits}개 · 대기 {Math.max(0, value.coverage.totalUnits - value.coverage.verifiedUnits - value.coverage.failedUnits)}개 · 실패 {value.coverage.failedUnits}개</p>}
    <p>LLM 실제 전송 {value.transportRequests ?? value.requests}회 · 이번 실행 {value.attemptTransportRequests ?? value.attemptRequests ?? value.requests}회</p>
    {value.lastProgressAt && <p>최근 진행 <time dateTime={value.lastProgressAt}>{new Date(value.lastProgressAt).toLocaleTimeString()}</time></p>}
    {running && elapsed >= 300 && <p>5분이 지났습니다. 완료된 작업을 저장하면서 실행 한도까지 계속 생성합니다.</p>}
    {value.protocolUpgraded && <p>생성 정책이 갱신되어 필요한 요청을 다시 수행합니다. 이전 결과는 이력에 보존됩니다.</p>}
    <details><summary>실행 기술 정보</summary>
      <p>전체 내부 처리 완료 {value.completedUnits}단위 · 이번 실행 새 완료 {value.attemptCompletedUnits ?? value.completedUnits}단위 · 재사용 {value.reusedUnits}단위</p>
      <p>내부 처리 단위는 다이어그램 수나 요청 제한이 아닙니다. 실행 한도 {value.budgetSeconds}초 · 누적 {value.totalElapsedSeconds ?? elapsed}초</p>
      {value.lastRequest && <OutputDiagnostic value={value.lastRequest} />}
    </details>
    {loadError && <p>전체 오류 기록을 불러오지 못했습니다. 진단 다운로드로 확인할 수 있습니다.</p>}
    {!!failures.length && <DiagnosticGrid records={failures} running={running} />}
  </div>;
}

function recoveryLabel(failure: FailureDiagnostic, running: boolean) {
  return ({ Recovered: "복구 완료", PartiallyRecovered: "일부 복구", Exhausted: "복구 실패 · Code 확인",
    RequiresAction: "설정 확인 필요", Retrying: running ? "복구 중" : "재개 대기", Interrupted: "재개 대기" } as Record<string, string>)[failure.recoveryState ?? ""] ?? "복구 상태 미확인";
}

function DiagnosticGrid({ records, running }: { records: FailureDiagnostic[]; running: boolean }) {
  const [selectedId, setSelectedId] = useState("");
  const selected = records.find(d => d.id === selectedId) ?? records[0];
  return <section aria-label="요청 오류 진단" className="diagnostic-panel">
    <strong>요청 오류 진단 · {records.length}건</strong>
    <div className="diagnostic-layout"><div className="diagnostic-table-scroll" tabIndex={0} aria-label="오류 목록 스크롤">
      <table className="diagnostic-table"><thead><tr><th>시각</th><th>단계</th><th>오류 요약</th><th>복구 상태</th></tr></thead>
        <tbody>{records.map(record => <tr key={record.id} className={selected.id === record.id ? "selected" : ""} aria-selected={selected.id === record.id}>
          <td>{record.startedAt ? new Date(record.startedAt).toLocaleTimeString() : "—"}</td><td>{purpose(record)}</td>
          <td><button type="button" onClick={() => setSelectedId(record.id)}>{failureDescription(record.validationCode, record.errorCode)}</button></td>
          <td><span className={record.recoveryState === "Recovered" ? "recovery-ok" : ""}>{recoveryLabel(record, running)}</span></td>
        </tr>)}</tbody></table></div>
      <div className="diagnostic-detail" aria-label="선택 오류 상세">
        <strong>{failureDescription(selected.validationCode, selected.errorCode)}</strong>
        <p>{purpose(selected)} · {selected.sent ? "응답 수신·검증 단계" : "전송 전 검사"}</p>
        <p>복구 결과: {recoveryLabel(selected, running)} · 시도 {selected.attempt ?? 1}</p>
        {!!selected.validationDetails?.issueCodes?.length && <p>{selected.validationDetails.issueCodes.map(issueDescription).join(" · ")}</p>}
        {selected.recoveryState === "Recovered" ? <p>후속 보정이 완료되었습니다. 최초 오류 기록은 보존됩니다.</p> : selected.recoveryState === "RequiresAction" ? <p>{serverFailure(selected.serverErrorCategory)}</p> : <p>완료된 AI/Code 다이어그램은 계속 확인할 수 있습니다. 재개 대기는 이어서 생성으로, 복구 실패는 해당 결과 재생성으로 다시 시도합니다.</p>}
        <details><summary>기술 상세</summary><p>{selected.errorCode} / {selected.validationCode}</p><OutputDiagnostic value={selected} />
          {selected.httpStatus && <p>HTTP {selected.httpStatus}</p>}
          <p>내부 묶음 {selected.recoveryGroupId ?? "미기록"} · 요청 {selected.id}</p>
          {selected.inputCharacters != null && <p>입력 {selected.inputCharacters.toLocaleString()}자 / {selected.inputCharacterLimit?.toLocaleString() ?? "미기록"}</p>}
          {selected.validationDetails && <p>필요 {selected.validationDetails.expectedItems} · 응답 {selected.validationDetails.receivedItems} · 누락 {selected.validationDetails.missingItems} · 중복 {selected.validationDetails.duplicateItems}{selected.validationDetails.field && " · 필드 " + selected.validationDetails.field}</p>}
        </details>
      </div></div>
  </section>;
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
    SharedTextCodeSyntax: "설명에 허용되지 않는 마크업이 포함됐습니다.", SharedSemanticCoverage: "이전 버전의 응답 항목·표현 검증에 실패했습니다.",
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
