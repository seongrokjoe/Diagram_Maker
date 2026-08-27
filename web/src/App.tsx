import { FormEvent, useEffect, useState } from "react";
import { api } from "./api";
import { MermaidPreview } from "./MermaidPreview";
import type { AnalysisResponse, DiagramArtifact, Repository, RepositoryInspection } from "./types";

type Tab = "natural" | "analysis" | "repositories";

const terminalStates = new Set(["Completed", "Partial", "Failed"]);

export default function App() {
  const [tab, setTab] = useState<Tab>("natural");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [diagram, setDiagram] = useState<DiagramArtifact | null>(null);
  const [analysis, setAnalysis] = useState<AnalysisResponse | null>(null);
  const [repositoryName, setRepositoryName] = useState("");
  const [repositoryPath, setRepositoryPath] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [repositoryInspection, setRepositoryInspection] = useState<RepositoryInspection | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const loadRepositories = async () => {
    try {
      setRepositories(await api.listRepositories());
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "저장소 목록을 불러오지 못했습니다.");
    }
  };

  useEffect(() => {
    void loadRepositories();
  }, []);

  useEffect(() => {
    if (!analysis || terminalStates.has(analysis.state)) return;
    const timer = window.setTimeout(() => {
      void api.getAnalysis(analysis.id).then(setAnalysis).catch((reason: unknown) => {
        setError(reason instanceof Error ? reason.message : "분석 상태를 불러오지 못했습니다.");
      });
    }, 900);
    return () => window.clearTimeout(timer);
  }, [analysis]);

  async function createNatural(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    setBusy(true);
    setError("");
    try {
      const record = await api.createNaturalDiagram({
        prompt: String(data.get("prompt")),
        diagramType: String(data.get("diagramType")),
      });
      setDiagram(record.diagram);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "생성에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  async function createAnalysis(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    setBusy(true);
    setError("");
    try {
      setAnalysis(await api.createAnalysis({
        repositoryId: String(data.get("repositoryId")),
        baseRevision: String(data.get("baseRevision")),
        targetRevision: String(data.get("targetRevision")),
        includeLlmSummary: data.get("includeLlmSummary") === "on",
      }));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "분석 요청에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  async function inspectRepository() {
    setBusy(true);
    setError("");
    setRepositoryInspection(null);
    try {
      const result = await api.inspectRepository(repositoryPath);
      setRepositoryInspection(result);
      setRepositoryPath(result.normalizedPath);
      setDefaultBranch(result.defaultBranch);
      if (!repositoryName.trim()) {
        setRepositoryName(result.normalizedPath.split(/[\\/]/).filter(Boolean).at(-1) ?? "Local repository");
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Git 저장소 연결 테스트에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  async function registerRepository(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!repositoryInspection || repositoryInspection.normalizedPath !== repositoryPath) {
      setError("먼저 현재 경로의 연결 테스트를 완료하세요.");
      return;
    }
    setBusy(true);
    setError("");
    try {
      await api.registerRepository({
        name: repositoryName,
        localPath: repositoryPath,
        defaultBranch,
      });
      setRepositoryName("");
      setRepositoryPath("");
      setDefaultBranch("main");
      setRepositoryInspection(null);
      await loadRepositories();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "저장소 등록에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">INTERNAL · SOURCE SAFE</p>
          <h1>AI Git Architecture Reviewer</h1>
        </div>
        <span className="network-badge">외부 전송 없음</span>
      </header>

      <nav className="tabs" aria-label="주요 기능">
        <button className={tab === "natural" ? "active" : ""} onClick={() => setTab("natural")}>자연어 다이어그램</button>
        <button className={tab === "analysis" ? "active" : ""} onClick={() => setTab("analysis")}>Git 변경 분석</button>
        <button className={tab === "repositories" ? "active" : ""} onClick={() => setTab("repositories")}>저장소 관리</button>
      </nav>

      {error && <div className="error-panel" role="alert">{error}</div>}

      <main>
        {tab === "natural" && (
          <section className="workspace-grid">
            <form className="panel controls" onSubmit={createNatural}>
              <p className="section-label">NATURAL LANGUAGE</p>
              <h2>요청으로 구조를 그립니다</h2>
              <label>
                다이어그램 종류
                <select name="diagramType" defaultValue="auto">
                  <option value="auto">자동 선택</option>
                  <option value="flowchart">Flow / Component</option>
                  <option value="sequence">Sequence</option>
                  <option value="class">Class</option>
                  <option value="state">State</option>
                </select>
              </label>
              <label>
                요청
                <textarea name="prompt" required rows={10} placeholder="예: 사용자 → API Gateway → 주문 서비스 → 데이터베이스 흐름을 그려줘" />
              </label>
              <button className="primary" disabled={busy}>{busy ? "생성 중…" : "다이어그램 생성"}</button>
              <p className="help">개발 모드에서는 화살표 기반 결정적 생성기를 사용할 수 있습니다. 운영 환경은 사내 LLM만 호출합니다.</p>
            </form>
            <section className="panel preview">
              <div className="panel-heading">
                <div>
                  <p className="section-label">PREVIEW</p>
                  <h2>{diagram?.ir.title ?? "생성 결과"}</h2>
                </div>
                {diagram && <span className="status-chip">{diagram.type}</span>}
              </div>
              {diagram ? <MermaidPreview source={diagram.mermaidDsl} /> : <EmptyState text="요청을 입력하면 결과가 여기에 표시됩니다." />}
              {diagram && <details><summary>Mermaid DSL 확인</summary><pre>{diagram.mermaidDsl}</pre></details>}
            </section>
          </section>
        )}

        {tab === "analysis" && (
          <section className="workspace-grid">
            <form className="panel controls" onSubmit={createAnalysis}>
              <p className="section-label">IMMUTABLE REVISION REVIEW</p>
              <h2>Commit 구조 변경 분석</h2>
              <label>
                저장소
                <select name="repositoryId" required defaultValue="">
                  <option value="" disabled>저장소 선택</option>
                  {repositories.map((repository) => <option key={repository.id} value={repository.id}>{repository.name}</option>)}
                </select>
              </label>
              <div className="field-row">
                <label>Base revision<input name="baseRevision" required placeholder="HEAD~1 또는 SHA" /></label>
                <label>Target revision<input name="targetRevision" required placeholder="HEAD 또는 SHA" /></label>
              </div>
              <label className="checkbox"><input type="checkbox" name="includeLlmSummary" defaultChecked /> 사내 LLM 변경 요약 포함</label>
              <button className="primary" disabled={busy || repositories.length === 0}>분석 시작</button>
              {repositories.length === 0 && <p className="help">먼저 관리자가 분석할 저장소를 등록해야 합니다.</p>}
            </form>
            <section className="panel preview">
              <div className="panel-heading">
                <div><p className="section-label">ANALYSIS</p><h2>변경 분석 결과</h2></div>
                {analysis && <span className={`status-chip ${analysis.state.toLowerCase()}`}>{analysis.state}</span>}
              </div>
              {!analysis && <EmptyState text="Base와 Target revision을 선택해 분석을 시작하세요." />}
              {analysis && !analysis.result && (
                <div className="progress-block">
                  <div className="progress-meta"><span>{analysis.stageMessage}</span><strong>{analysis.progress}%</strong></div>
                  <div className="progress-track"><span style={{ width: `${analysis.progress}%` }} /></div>
                  {analysis.errorMessage && <p className="error-text">{analysis.errorMessage}</p>}
                </div>
              )}
              {analysis?.result && (
                <div className="analysis-result">
                  <div className="summary-card"><strong>요약</strong><p>{analysis.result.narrative.summary}</p></div>
                  {analysis.result.narrative.warnings.map((warning) => <p className="warning" key={warning}>{warning}</p>)}
                  {analysis.result.diagrams[0] && <MermaidPreview source={analysis.result.diagrams[0].mermaidDsl} />}
                  <h3>변경 파일</h3>
                  <ul className="file-list">
                    {analysis.result.changedFiles.map((file) => (
                      <li key={`${file.path}-${file.changeKind}`}><span className={`change ${file.changeKind.toLowerCase()}`}>{file.changeKind}</span><code>{file.previousPath ? `${file.previousPath} → ${file.path}` : file.path}</code></li>
                    ))}
                  </ul>
                  {analysis.result.narrative.risks.length > 0 && <><h3>확인 필요</h3>{analysis.result.narrative.risks.map((risk, index) => <div className="risk" key={`${risk.text}-${index}`}><strong>{risk.severity}</strong><span>{risk.text}</span></div>)}</>}
                </div>
              )}
            </section>
          </section>
        )}

        {tab === "repositories" && (
          <section className="repository-layout">
            <form className="panel controls" onSubmit={registerRepository}>
              <p className="section-label">ADMIN</p>
              <h2>내 PC의 Git 저장소 등록</h2>
              <label>
                표시 이름
                <input value={repositoryName} onChange={(event) => setRepositoryName(event.target.value)} required placeholder="VSAssist" />
              </label>
              <label>
                Git 저장소 절대 경로
                <input
                  value={repositoryPath}
                  onChange={(event) => {
                    setRepositoryPath(event.target.value);
                    setRepositoryInspection(null);
                  }}
                  required
                  placeholder="C:\\Work\\Git\\VSAssist 또는 C:\\Work\\Git\\VSAssist\\.git"
                />
              </label>
              <label>
                기본 Branch
                <input value={defaultBranch} onChange={(event) => setDefaultBranch(event.target.value)} required />
              </label>
              <div className="button-row">
                <button type="button" className="secondary" disabled={busy || !repositoryPath.trim()} onClick={() => void inspectRepository()}>
                  연결 테스트
                </button>
                <button className="primary" disabled={busy || !repositoryInspection}>저장소 등록</button>
              </div>
              {repositoryInspection && (
                <div className="connection-card" role="status">
                  <strong>연결 성공</strong>
                  <span>{repositoryInspection.isBare ? "Bare repository" : "Working repository"}</span>
                  <code>{repositoryInspection.normalizedPath}</code>
                  <span>{repositoryInspection.defaultBranch} · {repositoryInspection.headSha.slice(0, 10)}</span>
                  <small>{repositoryInspection.headMessage}</small>
                </div>
              )}
              <p className="help">탐색기 주소 표시줄에서 저장소 루트 또는 .git 경로를 복사해 붙여넣으세요. 소스나 Git hook은 실행하지 않습니다.</p>
            </form>
            <section className="panel">
              <p className="section-label">REGISTERED</p>
              <h2>사용 가능한 저장소</h2>
              <ul className="repository-list">
                {repositories.map((repository) => <li key={repository.id}><div><strong>{repository.name}</strong><span>{repository.defaultBranch}</span></div><code title={repository.localPath}>{repository.localPath}</code></li>)}
              </ul>
              {repositories.length === 0 && <EmptyState text="등록된 저장소가 없습니다." />}
            </section>
          </section>
        )}
      </main>
    </div>
  );
}

function EmptyState({ text }: { text: string }) {
  return <div className="empty-state"><div className="empty-icon">◇</div><p>{text}</p></div>;
}
