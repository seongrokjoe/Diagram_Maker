import { FormEvent, useEffect, useState } from "react";
import { api } from "./api";
import { MermaidPreview } from "./MermaidPreview";
import type {
  AnalysisResponse,
  LlmConnectionTestResult,
  LlmContractTestResult,
  LlmThinkingContractTestResult,
  NaturalDiagramRecord,
  Repository,
  RepositoryInspection,
} from "./types";

type Tab = "natural" | "analysis" | "repositories" | "llm";
type LlmTestKind = "connection" | "diagram" | "thinking";
type LlmTestValue = LlmConnectionTestResult | LlmContractTestResult | LlmThinkingContractTestResult;

const terminalStates = new Set(["Completed", "Partial", "Failed"]);

export default function App() {
  const [tab, setTab] = useState<Tab>("natural");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [naturalRecord, setNaturalRecord] = useState<NaturalDiagramRecord | null>(null);
  const [naturalHistory, setNaturalHistory] = useState<NaturalDiagramRecord[]>([]);
  const [naturalRevisions, setNaturalRevisions] = useState<NaturalDiagramRecord[]>([]);
  const [analysis, setAnalysis] = useState<AnalysisResponse | null>(null);
  const [activeAnalysisDiagramType, setActiveAnalysisDiagramType] = useState("flowchart");
  const [repositoryName, setRepositoryName] = useState("");
  const [repositoryPath, setRepositoryPath] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [repositoryInspection, setRepositoryInspection] = useState<RepositoryInspection | null>(null);
  const [includeLlmSummary, setIncludeLlmSummary] = useState(true);
  const [diagramTypes, setDiagramTypes] = useState<string[]>(["flowchart"]);
  const [callerDepth, setCallerDepth] = useState(1);
  const [calleeDepth, setCalleeDepth] = useState(1);
  const [llmTests, setLlmTests] = useState<Partial<Record<LlmTestKind, LlmTestValue>>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const diagram = naturalRecord?.diagram ?? null;

  const loadRepositories = async () => {
    try {
      setRepositories(await api.listRepositories());
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "저장소 목록을 불러오지 못했습니다.");
    }
  };

  const loadNaturalHistory = async () => {
    try {
      setNaturalHistory(await api.listNaturalDiagrams());
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "다이어그램 이력을 불러오지 못했습니다.");
    }
  };

  useEffect(() => {
    void loadRepositories();
    void loadNaturalHistory();
  }, []);

  useEffect(() => {
    if (!naturalRecord) {
      setNaturalRevisions([]);
      return;
    }
    void api.listNaturalDiagramRevisions(naturalRecord.id).then(setNaturalRevisions).catch((reason: unknown) => {
      setError(reason instanceof Error ? reason.message : "리비전 이력을 불러오지 못했습니다.");
    });
  }, [naturalRecord?.id]);

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
        enableThinking: data.get("enableThinking") === "on",
      });
      setNaturalRecord(record);
      await loadNaturalHistory();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "생성에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  async function regenerateNatural() {
    if (!naturalRecord) return;
    setBusy(true);
    setError("");
    try {
      const record = await api.regenerateNaturalDiagram(naturalRecord.id);
      setNaturalRecord(record);
      await loadNaturalHistory();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "재생성에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  async function saveNaturalDslRevision(mermaidDsl: string) {
    if (!naturalRecord) return;
    setError("");
    try {
      const record = await api.saveNaturalDiagramDslRevision(naturalRecord.id, mermaidDsl);
      setNaturalRecord(record);
      await loadNaturalHistory();
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : "Mermaid 리비전 저장에 실패했습니다.";
      setError(message);
      throw reason;
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
        diagramTypes,
        callerDepth,
        calleeDepth,
        includeLlmSummary: data.get("includeLlmSummary") === "on",
        enableThinking: data.get("includeLlmSummary") === "on" && data.get("enableThinking") === "on",
      }));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "분석 요청에 실패했습니다.");
    } finally {
      setBusy(false);
    }
  }

  async function runLlmTest(kind: LlmTestKind) {
    setBusy(true);
    setError("");
    try {
      const result = kind === "connection"
        ? await api.testLlmConnection()
        : kind === "diagram"
          ? await api.testLlmDiagramContract()
          : await api.testLlmThinkingContract();
      setLlmTests((current) => ({ ...current, [kind]: result }));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "사내 LLM 시험에 실패했습니다.");
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
        <button className={tab === "llm" ? "active" : ""} onClick={() => setTab("llm")}>사내 LLM 점검</button>
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
              <label className="checkbox"><input type="checkbox" name="enableThinking" /> Thinking 모드 사용</label>
              <button className="primary" disabled={busy}>{busy ? "생성 중…" : "다이어그램 생성"}</button>
              <p className="help">같은 요청은 현재 서버 세션의 결과를 재사용합니다. 다른 결과가 필요할 때만 명시적으로 재생성하세요.</p>
              {naturalHistory.length > 0 && <div className="revision-list">
                <strong>최근 다이어그램</strong>
                {naturalHistory.map((record) => <button type="button" className={naturalRecord?.id === record.id ? "active" : ""} key={record.id} onClick={() => setNaturalRecord(record)}>
                  <span>{record.diagram.ir.title}</span><small>{formatDiagramType(record.diagram.type)} · v{record.diagram.version}</small>
                </button>)}
              </div>}
            </form>
            <section className="panel preview">
              <div className="panel-heading">
                <div>
                  <p className="section-label">PREVIEW</p>
                  <h2>{diagram?.ir.title ?? "생성 결과"}</h2>
                </div>
                {diagram && <span className="status-chip">{formatDiagramType(diagram.type)} · v{diagram.version}</span>}
              </div>
              {naturalRecord && <div className="diagram-meta">
                <span>{naturalRecord.reused ? "세션 결과 재사용" : naturalRecord.source === "manualDsl" ? "DSL 수동 리비전" : "LLM 생성"}</span>
                <button type="button" className="secondary" disabled={busy} onClick={() => void regenerateNatural()}>LLM으로 다시 생성</button>
              </div>}
              {diagram ? <MermaidPreview source={diagram.mermaidDsl} downloadName={`natural-${diagram.type}-v${diagram.version}`} editable onSaveRevision={saveNaturalDslRevision} /> : <EmptyState text="요청을 입력하면 결과가 여기에 표시됩니다." />}
              {naturalRevisions.length > 1 && <div className="revision-list horizontal">
                <strong>리비전</strong>
                {naturalRevisions.map((record) => <button type="button" className={naturalRecord?.id === record.id ? "active" : ""} key={record.id} onClick={() => setNaturalRecord(record)}>v{record.diagram.version} · {record.source === "manualDsl" ? "DSL 편집" : "LLM"}</button>)}
              </div>}
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
              <fieldset className="diagram-options">
                <legend>다이어그램 형식</legend>
                {[["flowchart", "영향도 흐름"], ["class", "클래스"], ["sequence", "호출 시퀀스"], ["state", "상태 전이"]].map(([value, label]) => (
                  <label className="checkbox" key={value}>
                    <input type="checkbox" checked={diagramTypes.includes(value)} onChange={(event) => setDiagramTypes((current) => event.target.checked ? [...current, value] : current.length > 1 ? current.filter((item) => item !== value) : current)} />
                    {label}
                  </label>
                ))}
              </fieldset>
              <div className="field-row">
                <label>Caller 깊이<select value={callerDepth} onChange={(event) => setCallerDepth(Number(event.target.value))}>{[0, 1, 2, 3].map(value => <option key={value} value={value}>{value}</option>)}</select></label>
                <label>Callee 깊이<select value={calleeDepth} onChange={(event) => setCalleeDepth(Number(event.target.value))}>{[0, 1, 2].map(value => <option key={value} value={value}>{value}</option>)}</select></label>
              </div>
              <label className="checkbox">
                <input
                  type="checkbox"
                  name="includeLlmSummary"
                  checked={includeLlmSummary}
                  onChange={(event) => setIncludeLlmSummary(event.target.checked)}
                />
                사내 LLM 변경 요약 포함
              </label>
              <label className="checkbox">
                <input type="checkbox" name="enableThinking" disabled={!includeLlmSummary} /> Thinking 모드 사용
              </label>
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
                  {analysis.errorMessage && <div className="analysis-error" role="alert">
                    <p className="error-text">{analysis.errorMessage}</p>
                    <dl>
                      <div><dt>오류 코드</dt><dd><code>{analysis.errorCode ?? "ANALYSIS_FAILED"}</code></dd></div>
                      <div><dt>분석 ID</dt><dd><code>{analysis.id}</code></dd></div>
                    </dl>
                  </div>}
                </div>
              )}
              {analysis?.result && (
                <div className="analysis-result">
                  <div className="summary-card"><strong>요약</strong><p>{analysis.result.narrative.summary}</p></div>
                  {analysis.result.narrative.warnings.map((warning) => <p className="warning" key={warning}>{warning}</p>)}
                  <div className="summary-card"><strong>변경 의도</strong><p>{analysis.result.narrative.intent}</p></div>
                  {analysis.result.diagrams.length > 0 && <div className="diagram-type-tabs" role="tablist">
                    {analysis.result.diagrams.map((item) => <button type="button" role="tab" aria-selected={activeAnalysisDiagramType === item.type} className={activeAnalysisDiagramType === item.type ? "active" : ""} key={item.id} onClick={() => setActiveAnalysisDiagramType(item.type)}>{formatDiagramType(item.type)}</button>)}
                  </div>}
                  {(() => {
                    const item = analysis.result.diagrams.find((candidate) => candidate.type === activeAnalysisDiagramType) ?? analysis.result.diagrams[0];
                    return item ? <article className="diagram-result" key={item.id}>
                      <h3>{formatDiagramType(item.type)}</h3>
                      <MermaidPreview source={item.mermaidDsl} downloadName={`diagram-${item.type}-${analysis.targetSha?.slice(0, 8) ?? "result"}`} />
                      <details><summary>Mermaid DSL 확인</summary><pre>{item.mermaidDsl}</pre></details>
                    </article> : null;
                  })()}
                  {analysis.result.diagramAvailability?.filter(item => !item.available).map(item => <p className="warning" key={item.type}>{formatDiagramType(item.type)}: {item.reason}</p>)}
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

        {tab === "llm" && (
          <section className="llm-test-layout">
            <section className="panel controls">
              <p className="section-label">SYNTHETIC DATA ONLY</p>
              <h2>사내 LLM 연결 점검</h2>
              <p className="help">실제 저장소, Commit 또는 사용자 요청을 보내지 않고 고정된 합성 데이터만 사용합니다.</p>
              <button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("connection")}>1. 기본 연결 시험</button>
              <button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("diagram")}>2. DiagramIR 계약 시험</button>
              <button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("thinking")}>3. Thinking 계약 시험</button>
            </section>
            <section className="panel">
              <p className="section-label">BOUNDED DIAGNOSTICS</p>
              <h2>시험 결과</h2>
              <div className="llm-result-list">
                <LlmTestCard title="기본 연결" value={llmTests.connection} />
                <LlmTestCard title="DiagramIR 구조화" value={llmTests.diagram} />
                <LlmTestCard title="Thinking 구조화" value={llmTests.thinking} />
              </div>
            </section>
          </section>
        )}
      </main>
    </div>
  );
}

function LlmTestCard({ title, value }: { title: string; value?: LlmTestValue }) {
  return (
    <article className={`llm-result-card ${value?.success ? "success" : ""}`}>
      <div><strong>{title}</strong><span>{value ? (value.success ? "성공" : "실패") : "미실행"}</span></div>
      {value && (
        <dl>
          <dt>응답 시간</dt><dd>{value.elapsedMilliseconds} ms</dd>
          <dt>종료 사유</dt><dd>{value.finishReason || "미제공"}</dd>
          <dt>출력 토큰 상한</dt><dd>{value.requestedMaxOutputTokens}</dd>
          <dt>토큰 사용량</dt><dd>{value.promptTokens ?? "-"} / {value.completionTokens ?? "-"} / {value.totalTokens ?? "-"}</dd>
          {"responseCharacters" in value && <><dt>응답 문자</dt><dd>{value.responseCharacters}</dd></>}
          {"nodeCount" in value && <>
            <dt>노드 / 엣지</dt><dd>{value.nodeCount} / {value.edgeCount}</dd>
          </>}
          {"structuredOutputApplied" in value && <>
            <dt>Thinking</dt><dd>{value.thinkingEnabled ? "사용" : "미사용"}</dd>
            <dt>구조화 적용</dt><dd>{value.structuredOutputApplied ? "예" : "아니오"}</dd>
            <dt>Fallback / 복구</dt><dd>{value.structuredOutputFallbackUsed ? "사용" : "없음"} / {value.repairUsed ? "사용" : "없음"}</dd>
          </>}
        </dl>
      )}
    </article>
  );
}

function formatDiagramType(type: string) {
  return ({ flowchart: "영향도 흐름", class: "클래스 관계", sequence: "호출 시퀀스", state: "상태 전이" } as Record<string, string>)[type] ?? type;
}

function EmptyState({ text }: { text: string }) {
  return <div className="empty-state"><div className="empty-icon">◇</div><p>{text}</p></div>;
}
