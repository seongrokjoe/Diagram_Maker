import { useEffect, useRef, useState } from "react";
import { readNaturalTestStream } from "./naturalTestStream";

type TestCase = { id: string; state: string; pages: number; reviewedPages: number; errorCode?: string; types?: string[] };
type TestResult = { success: boolean; report: string; summary?: string; cases: TestCase[] };
type TestEvent = { type: string; caseId?: string; stage: string; completedPages: number; totalUnits: number;
  execution: { elapsedSeconds: number; budgetSeconds: number; transportRequests: number; lastRequest?: { purpose?: string } };
  case?: TestCase; result?: TestResult };
const stages: Record<string, string> = { preparing: "검사 준비", requirements: "요구사항 추출", "requirements-review": "원문 의미 검토",
  "scenario-design": "시나리오 설계", "scenario-review": "시나리오 의미 검토", "scenario-review-confirm": "조건 변경 재확인", "scenario-repair": "시나리오 보정",
  "natural-final-review": "형식 간 일관성 검토", "scenario-mapping": "시나리오 연결 보정", "schema-repair": "응답 형식 보정" };
const names: Record<string, string> = { "short-approval": "짧은 승인 흐름", "table-interlock": "표·공통 조건", "buffer-handshake": "Buffer Handshake",
  flowchart: "흐름도", sequence: "시퀀스", state: "상태도", class: "클래스" };

export function NaturalDiagramTest() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);
  const [progress, setProgress] = useState<TestEvent | null>(null);
  const [cases, setCases] = useState<TestCase[]>([]);
  const [caseId, setCaseId] = useState("");
  const [diagramType, setDiagramType] = useState("");
  const [elapsed, setElapsed] = useState(0);
  const [error, setError] = useState("");
  const [copyStatus, setCopyStatus] = useState("");
  const controller = useRef<AbortController | null>(null);
  const startedAt = useRef(0);
  useEffect(() => () => controller.current?.abort(), []);
  useEffect(() => {
    if (!busy) return;
    const timer = window.setInterval(() => setElapsed(Math.floor((Date.now() - startedAt.current) / 1000)), 1000);
    return () => window.clearInterval(timer);
  }, [busy]);
  async function run() {
    const active = new AbortController(); controller.current = active;
    startedAt.current = Date.now();
    setBusy(true); setResult(null); setProgress(null); setCases([]); setError(""); setCopyStatus(""); setElapsed(0);
    let receivedResult = false;
    try {
      const query = new URLSearchParams({ format: "ndjson" });
      if (caseId) query.set("caseId", caseId);
      if (diagramType) query.set("diagramType", diagramType);
      const response = await fetch(`/api/v1/llm/tests/natural-diagram-contract?${query}`, { method: "POST", signal: active.signal });
      if (!response.ok || !response.body) throw new Error("connection");
      await readNaturalTestStream<TestEvent>(response.body, event => {
        if (active.signal.aborted) return;
        setProgress(event);
        if (event.case) setCases(previous => [...previous.filter(item => item.id !== event.case!.id), event.case!]);
        if (event.result) { receivedResult = true; setResult(event.result); setCases(event.result.cases); }
      });
      if (!receivedResult) throw new Error("incomplete");
    } catch {
      setError(active.signal.aborted ? "검사를 취소했습니다. 수신한 진행 내용과 완료 사례는 보존했습니다."
        : "검사 연결이 종료되었습니다. 수신한 진행 내용을 보존했습니다. 서버 상태와 관리자 권한을 확인하세요.");
    } finally { if (controller.current === active) { controller.current = null; setBusy(false); } }
  }
  const summary = result?.summary ?? (progress ? `검사 ${busy ? "진행 중" : "중단"}; 경과=${elapsed}s; 단계=${progress.stage}; HTTP=${progress.execution.transportRequests}\n` +
    cases.map(item => `${item.id}: ${item.state}; reviewed=${item.reviewedPages}/${item.pages}; stop=${item.errorCode ?? "none"}`).join("\n") : "");
  const phase = progress?.execution.lastRequest?.purpose ?? progress?.stage ?? "preparing";
  return <article className="llm-result-card natural-diagram-test"><h3>자연어 다이어그램 검사</h3>
    <p className="help">고정 합성 문장을 실제 생성 경로로 검사합니다. Thinking은 꺼져 있습니다. 전체 선택 사례가 하나의 시간 한도(최대 15분)를 공유합니다.</p>
    <label>검사 문장 <select disabled={busy} value={caseId} onChange={event => {
      setCaseId(event.target.value); if (event.target.value === "short-approval" || event.target.value === "buffer-handshake") setDiagramType("");
    }}><option value="">전체</option><option value="short-approval">짧은 승인 흐름</option><option value="table-interlock">표·공통 조건</option><option value="buffer-handshake">Buffer Handshake</option></select></label>{" "}
    <label>검사 형식 <select disabled={busy} value={diagramType} onChange={event => setDiagramType(event.target.value)}>
      <option value="">기본 형식 전체</option>{(caseId === "short-approval" ? ["flowchart"] : caseId === "buffer-handshake" ? ["sequence"] : ["flowchart", "sequence", "state", "class"]).map(type => <option key={type} value={type}>{names[type]}</option>)}
    </select></label>{" "}
    <button type="button" className="secondary" disabled={busy} onClick={() => void run()}>{busy ? "자연어 생성 검사 중…" : "자연어 생성 검사"}</button>
    {busy && <button type="button" className="secondary" onClick={() => controller.current?.abort()}>검사 취소</button>}
    {progress && <p role="status">{names[progress.caseId ?? ""] ?? "전체 검사"} · {stages[phase] ?? progress.stage} · 경과 {elapsed}초 / 한도 {progress.execution.budgetSeconds}초 · 남은 시간 {Math.max(0, progress.execution.budgetSeconds - elapsed)}초 · HTTP {progress.execution.transportRequests}회 · 완료 페이지 {progress.completedPages}/{progress.totalUnits}</p>}
    {error && <p role="alert">{error}</p>}
    {result && <p role="status">{result.success ? "검사 통과" : "검사 미완료 · 진단 보고서를 확인하세요."}</p>}
    {cases.length > 0 && <ul>{cases.map(item => <li key={item.id}>{names[item.id]}{item.types && ` (${item.types.map(type => names[type]).join(", ")})`}: 검토 완료 {item.reviewedPages}/{item.pages} 페이지 · {item.state}{item.errorCode && ` · ${item.errorCode}`}</li>)}</ul>}
    {summary && <div className="natural-test-summary"><label>전달용 검사 요약<textarea readOnly rows={7} value={summary} /></label>
      <button type="button" className="secondary" onClick={async () => {
        try { await navigator.clipboard.writeText(summary); setCopyStatus("검사 요약을 복사했습니다."); }
        catch { setCopyStatus("요약 내용을 선택해 복사하거나 필요한 줄을 옮겨 적어주세요."); }
      }}>검사 요약 복사</button><p role="status">{copyStatus}</p>
      <p className="help">원문과 모델 응답은 보고서에 포함하지 않습니다. 취소·연결 종료 보고서는 수신한 내용까지만 포함합니다.</p>
      <button type="button" className="secondary" onClick={() => {
        const url = URL.createObjectURL(new Blob([result?.report ?? summary], { type: "text/plain;charset=utf-8" }));
        const link = document.createElement("a"); link.href = url; link.download = "natural-diagram-test.txt"; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }}>자연어 검사 보고서 다운로드</button></div>}
  </article>;
}
