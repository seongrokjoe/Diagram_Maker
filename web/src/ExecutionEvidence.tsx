import type { DiagramArtifact, SequenceBlock } from "./types";
import { EvidenceBrowser } from "./ResultDetails";

export function ExecutionEvidence({ diagram, onEvidence }: {
  diagram?: DiagramArtifact["ir"]; onEvidence?: (id: string) => void;
}) {
  if (!diagram) return null;
  const events = new Map(diagram.edges.map(edge => [edge.id, edge]));
  const rows: Array<{ id: string; kind: string; label: string; expression: string; value?: string; evidence: string[] }> = [];
  const visited = new Set<string>();
  function visit(blocks: SequenceBlock[]) {
    for (const block of blocks) {
      const edge = block.edgeId ? events.get(block.edgeId) : undefined;
      if (edge?.originalExpression) {
        visited.add(edge.id);
        rows.push({ id: edge.id, kind: edge.type, label: edge.label, expression: edge.originalExpression,
          value: edge.returnValue, evidence: edge.evidenceIds });
      } else if (block.originalExpression && block.evidenceIds?.length) {
        rows.push({ id: block.id, kind: block.kind, label: block.label, expression: block.originalExpression, evidence: block.evidenceIds });
      }
      visit(block.children);
    }
  }
  visit(diagram.sequenceBlocks ?? []);
  for (const edge of diagram.edges) if (edge.originalExpression && !visited.has(edge.id)) {
    rows.push({ id: edge.id, kind: edge.type, label: edge.label, expression: edge.originalExpression,
      value: edge.returnValue, evidence: edge.evidenceIds });
  }
  if (!rows.length) return null;
  const kind = (value: string) => ({ message: "호출", response: "호출 결과", return: "반환", throw: "예외",
    alt: "조건", break: "종료 조건", loop: "반복", transition: "상태 전이", opt: "실행 경로" }[value] ?? "처리");
  return <details className="execution-evidence"><summary>호출·조건·반환과 원본 근거</summary>
    {rows.map(row => <div className="node-evidence-row" key={row.id}>
      <strong>{kind(row.kind)} · {row.label}</strong>
      <code className="execution-expression">{row.expression}</code>
      {row.value && <span>반환값: {row.value === "unknown" ? "미확인" : row.value}</span>}
      {events.get(row.id)?.call && (() => { const call = events.get(row.id)!.call!; return <div className="call-presentation">
        <p>호출: {call.target} · 인자: {call.arguments.join(", ") || "없음"}</p>
        {call.assignedTo && <p>반환 대상: {call.assignedTo}{call.returnType && ` (${call.returnType})`}</p>}
        {call.basis === "api-contract" && <p>동봉 Win32 API 계약 · 입력 코드의 구현 근거와 구분됩니다.</p>}
        {call.outputs.map((output, index) => <div key={index}><p>{output.expression}: {output.description} · {output.basis === "api-contract" ? "API 계약" : output.basis === "code" ? "코드 근거" : "구문상 참조 전달"}</p>
          {onEvidence && output.evidenceIds.length > 0 && <EvidenceBrowser evidenceIds={output.evidenceIds} onEvidence={onEvidence} />}</div>)}
      </div>; })()}
      {onEvidence && row.evidence.length > 0 && <EvidenceBrowser evidenceIds={row.evidence} onEvidence={onEvidence} />}
    </div>)}
  </details>;
}
