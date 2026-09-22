import { useState } from "react";
import { request } from "./api";

type TestResult = { success: boolean; report: string; summary?: string;
  cases: Array<{ id: string; state: string; pages: number; reviewedPages: number; errorCode?: string }> };

export function NaturalDiagramTest() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);
  const [error, setError] = useState("");
  const [copyStatus, setCopyStatus] = useState("");
  async function run() {
    setBusy(true); setResult(null); setError(""); setCopyStatus("");
    try { setResult(await request<TestResult>("/api/v1/llm/tests/natural-diagram-contract", { method: "POST" })); }
    catch { setError("자연어 검사에 연결하지 못했습니다. 서버 상태와 관리자 권한을 확인하세요."); }
    finally { setBusy(false); }
  }
  return <article className="llm-result-card natural-diagram-test"><h3>자연어 다이어그램 검사</h3>
    <p className="help">고정 합성 문장과 조건·오류·표를 실제 생성 경로로 검사합니다. Thinking은 꺼져 있으며 최대 15분이 걸립니다.</p>
    <button type="button" className="secondary" disabled={busy} onClick={() => void run()}>{busy ? "자연어 생성 검사 중…" : "자연어 생성 검사"}</button>
    {error && <p role="alert">{error}</p>}
    {result && <><p role="status">{result.success ? "검사 통과" : "검사 미완료 · 진단 보고서를 확인하세요."}</p>
      <ul>{result.cases.map(item => <li key={item.id}>{item.id === "short-approval" ? "짧은 승인 흐름" : "표·공통 조건 네 형식"}: 검토 완료 {item.reviewedPages}/{item.pages} 페이지 · {item.state}{item.errorCode && ` · ${item.errorCode}`}</li>)}</ul>
      {result.summary && <div className="natural-test-summary"><label>전달용 검사 요약<textarea readOnly rows={7} value={result.summary} /></label>
        <button type="button" className="secondary" onClick={async () => {
          try { await navigator.clipboard.writeText(result.summary!); setCopyStatus("검사 요약을 복사했습니다."); }
          catch { setCopyStatus("요약 내용을 선택해 복사하거나 필요한 줄을 옮겨 적어주세요."); }
        }}>검사 요약 복사</button><p role="status">{copyStatus}</p>
        <p className="help">파일 전달이 어려우면 이 요약만 옮겨 적어주세요. 원문과 모델 응답은 포함하지 않습니다.</p></div>}
      <button type="button" className="secondary" onClick={() => {
        const url = URL.createObjectURL(new Blob([result.report], { type: "text/plain;charset=utf-8" }));
        const link = document.createElement("a"); link.href = url; link.download = "natural-diagram-test.txt"; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }}>자연어 검사 보고서 다운로드</button></>}
  </article>;
}
