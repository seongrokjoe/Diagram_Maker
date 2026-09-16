import type { DiagramArtifact, DiagramExplanation } from "./types";
import { EvidenceBrowser, ResultNotices } from "./ResultDetails";
import { ExecutionEvidence } from "./ExecutionEvidence";

export function DiagramExplanationPanel({ explanation, edited, onEvidence, diagram }: {
  explanation?: DiagramExplanation | null;
  edited: boolean;
  onEvidence?: (id: string) => void;
  diagram?: DiagramArtifact["ir"];
}) {
  const coverage = explanation?.coverage;
  const partial = explanation?.status === "Incomplete" && (coverage?.verifiedUnits ?? 0) > 0;
  return <section className="diagram-explanation" aria-label="현재 페이지 설명">
    {!explanation || explanation.status !== "Semantic" ? <div className="semantic-incomplete" role="status">
      <strong>{partial ? "AI 의미 설명 부분 완료" : "의미 설명 미완료"}</strong>
      {!explanation && <p>이전 생성 이력에는 페이지별 설명이 없습니다. 다시 그리기를 실행하면 현재 코드 분석으로 생성합니다.</p>}
      {explanation && <p>{explanation.warnings[0] || "코드 구조만 표시합니다. 의미 설명을 완성하려면 다시 생성하세요."}</p>}
      {coverage && <p>검토 통과 {coverage.verifiedUnits} / {coverage.totalUnits}개 · 실패 {coverage.failedUnits}개 · 원식 유지 {coverage.pendingUnits}개</p>}
      {explanation && <ResultNotices notices={explanation.warnings.slice(1)} title="추가 분석 안내" />}
    </div> : <p className="help">{explanation.basis === "ExecutionFactsAndReviewedPlan"
      ? "실행 구조 검증과 함수 전체의 의미 검토를 통과했습니다." : "이 페이지의 코드 의미 검토를 통과했습니다."}</p>}
    {(edited || explanation?.basis === "GeneratedSourceBeforeManualEdit") && <p className="warning explanation-edit-notice">
      아래 설명과 코드 근거는 생성 당시 코드 기준입니다. 수동 편집한 그림의 의미와 변경 커버리지는 다시 검증하지 않았습니다.
    </p>}
    {explanation?.basis === "ReviewedNameInterpretation" && <p className="help">일부 역할 설명은 이름을 바탕으로 해석했습니다. 호출 대상의 내부 구현은 확인되지 않았습니다.</p>}
    {explanation && <>
      <h4>동작 설명</h4><p className="page-behavior">{explanation.summary}</p>
      {explanation.behaviors != null ? <><h4>핵심 동작</h4><ul>{explanation.behaviors.map((behavior, index) => <li key={`${behavior.id}-${index}`}>{behavior.summary}
        {onEvidence && [...new Set([...(diagram?.nodes.filter(n => behavior.nodeIds.includes(n.id)).flatMap(n => n.evidenceIds) ?? []),
          ...(diagram?.edges.filter(e => behavior.edgeIds.includes(e.id)).flatMap(e => e.evidenceIds) ?? [])])].map((id, i) =>
          <button className="text-button" key={id} onClick={() => onEvidence(id)}>원본 근거 {i + 1}</button>)}
      </li>)}</ul></> : <><h4>핵심 변경</h4>
      {explanation.changes.length > 0 ? <ul>{explanation.changes.map((change, index) =>
        <li key={`${change.changeId}-${index}`} data-change-id={change.changeId}>{change.summary}</li>)}</ul> :
        <p className="help">{explanation.status === "Semantic" ? "이 페이지에 연결된 변경 설명이 없습니다." : "변경 전후의 의미 비교를 완료하지 못했습니다."}</p>}</>}
      {(explanation.failures?.length ?? 0) > 0 && <details className="semantic-failure-details"><summary>AI 실패 항목 {explanation.failures!.length}개</summary>
        <ul>{explanation.failures!.map((failure, index) => <li key={`${failure.stage}-${failure.code}-${index}`}>
          <strong>{failure.stage} · {failure.code}</strong>
          {failure.fields?.length ? <p>실패 필드: {failure.fields.join(", ")}</p> : null}
          {failure.issueCodes?.length ? <p>검토 사유: {failure.issueCodes.join(", ")}</p> : null}
          {failure.factIds.length ? <p>연결된 코드 사실 {failure.factIds.length}개</p> : null}
          {failure.correctionInstructions.map((instruction, correction) => <p key={correction}>보정 지시: {instruction}</p>)}
        </li>)}</ul>
      </details>}
      {explanation.status === "Semantic" && <ResultNotices notices={explanation.warnings} title="페이지 분석 안내" />}
      {onEvidence && <div className="page-evidence"><EvidenceBrowser evidenceIds={explanation.evidenceIds} diagram={diagram} onEvidence={onEvidence} /></div>}
    </>}
    <ExecutionEvidence diagram={diagram} onEvidence={onEvidence} />
  </section>;
}
