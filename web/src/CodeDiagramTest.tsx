import { useState } from "react";
import { request } from "./api";

type TestResult = {
  success: boolean;
  cases: Array<{ language: string; state: string; pages: number; semanticPages: number }>;
  report: string;
};

export function CodeDiagramTest() {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<TestResult | null>(null);
  const [error, setError] = useState("");
  async function run() {
    setBusy(true); setResult(null); setError("");
    try { setResult(await request<TestResult>("/api/v1/llm/tests/code-diagram-contract", { method: "POST" })); }
    catch { setError("코드 생성 검사에 연결하지 못했습니다. 서버 상태와 접근 권한을 확인하세요."); }
    finally { setBusy(false); }
  }
  return <article className="llm-result-card code-diagram-test">
    <h3>코드 다이어그램 검사</h3>
    <p className="help">고정 C#/C++ 코드로 의미 생성과 검토를 확인합니다. Thinking은 꺼져 있으며 실제 코드의 품질은 별도로 확인해야 합니다.</p>
    <button type="button" className="secondary" disabled={busy} onClick={() => void run()}>{busy ? "코드 생성 검사 중…" : "코드 생성 검사"}</button>
    {error && <p role="alert">{error}</p>}
    {result && <><p role="status">{result.success ? "검사 통과" : "검사 미완료 — 진단 보고서를 확인하세요."}</p>
      <ul>{result.cases.map(item => <li key={item.language}>{item.language === "csharp" ? "C#" : "C/C++"}: 의미 검토 완료 {item.semanticPages}/{item.pages} 페이지 · {({ Completed: "완료", Partial: "미완료", Failed: "실패", Skipped: "건너뜀" } as Record<string, string>)[item.state] ?? "미확인"}</li>)}</ul>
      <button type="button" className="secondary" onClick={() => {
        const url = URL.createObjectURL(new Blob([result.report], { type: "text/plain;charset=utf-8" }));
        const link = document.createElement("a"); link.href = url; link.download = "code-diagram-test.txt"; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }}>검사 보고서 다운로드</button></>}
  </article>;
}
