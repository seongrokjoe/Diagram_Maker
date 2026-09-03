import { FormEvent, useCallback, useEffect, useState } from "react";
import { AnalysisWorkspace } from "./AnalysisWorkspace";
import { api } from "./api";
import { DiagramEditor } from "./DiagramEditor";
import { PresetPicker } from "./PresetPicker";
import { RepositoryRuleEditor } from "./RepositoryRuleEditor";
import { elapsedLabel, useElapsedSeconds } from "./useElapsedSeconds";
import type {
  DiagramPreset,
  DiagramType,
  DiagramViewSelection,
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
const diagramTypes: Array<{ value: DiagramType; label: string }> = [
  { value: "flowchart", label: "Flow / Component" },
  { value: "sequence", label: "Sequence" },
  { value: "class", label: "Class" },
  { value: "state", label: "State" },
];

export default function App() {
  const [tab, setTab] = useState<Tab>("natural");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [naturalRecord, setNaturalRecord] = useState<NaturalDiagramRecord | null>(null);
  const [naturalPrompt, setNaturalPrompt] = useState("");
  const [naturalThinking, setNaturalThinking] = useState(false);
  const [naturalHistory, setNaturalHistory] = useState<NaturalDiagramRecord[]>([]);
  const [naturalRevisions, setNaturalRevisions] = useState<NaturalDiagramRecord[]>([]);
  const [naturalViews, setNaturalViews] = useState<DiagramViewSelection[]>([
    { id: "natural-view-1", diagramType: "flowchart", presetId: "" },
  ]);
  const [activeNaturalView, setActiveNaturalView] = useState("natural-view-1");
  const [naturalPresets, setNaturalPresets] = useState<DiagramPreset[]>([]);
  const [repositoryName, setRepositoryName] = useState("");
  const [repositoryPath, setRepositoryPath] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [repositoryInspection, setRepositoryInspection] = useState<RepositoryInspection | null>(null);
  const [llmTests, setLlmTests] = useState<Partial<Record<LlmTestKind, LlmTestValue>>>({});
  const [busyAction, setBusyAction] = useState("");
  const [error, setError] = useState("");
  const reportError = useCallback((message: string) => setError(message), []);
  const busy = Boolean(busyAction);
  const busySeconds = useElapsedSeconds(busy);

  const loadRepositories = useCallback(async () => {
    try { setRepositories(await api.listRepositories()); }
    catch (reason) { setError(messageOf(reason, "저장소 목록을 불러오지 못했습니다.")); }
  }, []);

  const loadNaturalHistory = useCallback(async () => {
    try { setNaturalHistory(await api.listNaturalDiagrams()); }
    catch (reason) { setError(messageOf(reason, "다이어그램 이력을 불러오지 못했습니다.")); }
  }, []);

  useEffect(() => { void loadRepositories(); void loadNaturalHistory(); }, [loadRepositories, loadNaturalHistory]);
  useEffect(() => {
    void api.listPresets().then((items) => {
      setNaturalPresets(items);
      setNaturalViews((current) => current.map((view) => items.some((item) => item.id === view.presetId)
        ? view
        : { ...view, presetId: defaultPreset(view.diagramType, items) }));
    }).catch((reason: unknown) => setError(messageOf(reason, "샘플 목록을 불러오지 못했습니다.")));
  }, []);
  useEffect(() => {
    if (!naturalRecord) { setNaturalRevisions([]); return; }
    void api.listNaturalDiagramRevisions(naturalRecord.id).then(setNaturalRevisions)
      .catch((reason: unknown) => setError(messageOf(reason, "리비전 이력을 불러오지 못했습니다.")));
  }, [naturalRecord?.id]);

  async function createNatural(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusyAction("natural-create"); setError("");
    try {
      const primary = naturalViews[0];
      const record = naturalRecord && naturalRecord.request.prompt === naturalPrompt && naturalRecord.request.enableThinking === naturalThinking
        ? await api.reviseNaturalDiagramViews(naturalRecord.id, naturalViews, changedNaturalViewIds(naturalViews, naturalRecord))
        : await api.createNaturalDiagram({
          prompt: naturalPrompt,
          diagramType: primary.diagramType,
          presetId: primary.presetId,
          views: naturalViews,
          enableThinking: naturalThinking,
        });
      setNaturalRecord(record);
      setActiveNaturalView(effectiveNaturalViews(record)[0]?.viewId ?? primary.id);
      await loadNaturalHistory();
    } catch (reason) { setError(messageOf(reason, "다이어그램 생성에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function regenerateNatural(viewId: string) {
    if (!naturalRecord) return;
    setBusyAction(`natural-regenerate-${viewId}`); setError("");
    try {
      const views = naturalRecord.request.views ?? naturalViews;
      const revised = await api.reviseNaturalDiagramViews(naturalRecord.id, views, [viewId]);
      setNaturalRecord(revised);
      setActiveNaturalView(viewId);
      await loadNaturalHistory();
    }
    catch (reason) { setError(messageOf(reason, "다이어그램 재생성에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function inspectRepository() {
    setBusyAction("repository-inspect"); setError(""); setRepositoryInspection(null);
    try {
      const result = await api.inspectRepository(repositoryPath);
      setRepositoryInspection(result); setRepositoryPath(result.normalizedPath); setDefaultBranch(result.defaultBranch);
      if (!repositoryName.trim()) setRepositoryName(result.normalizedPath.split(/[\\/]/).filter(Boolean).at(-1) ?? "Local repository");
    } catch (reason) { setError(messageOf(reason, "Git 저장소 연결 테스트에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function registerRepository(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!repositoryInspection || repositoryInspection.normalizedPath !== repositoryPath) { setError("현재 경로의 연결 테스트를 먼저 완료하세요."); return; }
    setBusyAction("repository-register"); setError("");
    try {
      await api.registerRepository({ name: repositoryName, localPath: repositoryPath, defaultBranch });
      setRepositoryName(""); setRepositoryPath(""); setDefaultBranch("main"); setRepositoryInspection(null);
      await loadRepositories();
    } catch (reason) { setError(messageOf(reason, "저장소 등록에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function runLlmTest(kind: LlmTestKind) {
    setBusyAction(`llm-${kind}`); setError("");
    try {
      const result = kind === "connection" ? await api.testLlmConnection()
        : kind === "diagram" ? await api.testLlmDiagramContract() : await api.testLlmThinkingContract();
      setLlmTests((current) => ({ ...current, [kind]: result }));
    } catch (reason) { setError(messageOf(reason, "사내 LLM 시험에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  function updateNaturalView(id: string, patch: Partial<DiagramViewSelection>) {
    setNaturalViews((current) => current.map((view) => view.id === id ? { ...view, ...patch } : view));
  }

  function addNaturalView() {
    const availableType = diagramTypes.find((type) => !naturalViews.some((view) => view.diagramType === type.value))?.value;
    if (!availableType) return;
    const view = { id: crypto.randomUUID(), diagramType: availableType, presetId: defaultPreset(availableType, naturalPresets) };
    setNaturalViews((current) => [...current, view]);
  }

  const naturalResults = naturalRecord ? effectiveNaturalViews(naturalRecord) : [];
  const selectedNatural = naturalResults.find((view) => view.viewId === activeNaturalView) ?? naturalResults[0];
  const diagram = selectedNatural?.diagram ?? selectedNatural?.lastSuccessfulDiagram;
  return <div className="app-shell">
    <header className="topbar"><div><p className="eyebrow">INTERNAL · SOURCE SAFE</p><h1>AI Git Architecture Reviewer</h1></div><span className="network-badge">외부 전송 없음</span></header>
    <nav className="tabs" aria-label="주요 기능">
      <button className={tab === "natural" ? "active" : ""} onClick={() => setTab("natural")}>자연어 다이어그램</button>
      <button className={tab === "analysis" ? "active" : ""} onClick={() => setTab("analysis")}>Git 변경 분석</button>
      <button className={tab === "repositories" ? "active" : ""} onClick={() => setTab("repositories")}>저장소 관리</button>
      <button className={tab === "llm" ? "active" : ""} onClick={() => setTab("llm")}>사내 LLM 점검</button>
    </nav>
    {error && <div className="error-panel" role="alert">{error}</div>}
    <main>
      {tab === "natural" && <section className="workspace-grid">
        <form className="panel controls" onSubmit={createNatural}>
          <p className="section-label">NATURAL LANGUAGE</p><h2>요청을 구조화된 다이어그램으로 변환</h2>
          <label>요청<textarea name="prompt" required rows={8} value={naturalPrompt} onChange={(event) => setNaturalPrompt(event.target.value)} placeholder="예: 사용자가 API Gateway를 통해 주문 서비스와 데이터베이스를 호출하는 순서를 그려줘" /></label>
          <div className="view-editor-heading"><strong>출력 형식 ({naturalViews.length}/4)</strong><button type="button" className="secondary" disabled={naturalViews.length >= 4 || naturalViews.length >= diagramTypes.length} onClick={addNaturalView}>형식 추가</button></div>
          {naturalViews.map((view, index) => {
            const typePresets = naturalPresets.filter((preset) => preset.type === view.diagramType);
            return <fieldset className="diagram-view-editor" key={view.id}><legend>다이어그램 {index + 1}</legend>
              <div className="field-row"><label>다이어그램 종류<select value={view.diagramType} onChange={(event) => { const diagramType = event.target.value as DiagramType; updateNaturalView(view.id, { diagramType, presetId: defaultPreset(diagramType, naturalPresets), overrides: undefined }); }}>{diagramTypes.map((item) => <option key={item.value} value={item.value} disabled={naturalViews.some((other) => other.id !== view.id && other.diagramType === item.value)}>{item.label}</option>)}</select></label>{naturalViews.length > 1 && <button type="button" className="text-button danger" onClick={() => setNaturalViews((current) => current.filter((item) => item.id !== view.id))}>삭제</button>}</div>
              <PresetPicker presets={typePresets} selectedId={view.presetId} onSelect={(preset) => updateNaturalView(view.id, { presetId: preset.id, overrides: undefined })} />
            </fieldset>;
          })}
          <label className="checkbox"><input type="checkbox" name="enableThinking" checked={naturalThinking} onChange={(event) => setNaturalThinking(event.target.checked)} /> Thinking 모드 사용</label>
          <button className="primary" disabled={busy || !naturalPrompt.trim() || naturalViews.some((view) => !view.presetId)}>{elapsedLabel(naturalRecord?.request.prompt === naturalPrompt && naturalRecord.request.enableThinking === naturalThinking ? "선택 구성 적용" : "다이어그램 생성", busyAction === "natural-create", busySeconds)}</button>
          <p className="help">같은 요청·종류·샘플은 같은 결과를 재사용합니다. 다른 결과가 필요할 때만 재생성하세요.</p>
          {naturalHistory.length > 0 && <div className="revision-list"><strong>최근 다이어그램</strong>{naturalHistory.map((record) => <button type="button" className={naturalRecord?.id === record.id ? "active" : ""} key={record.id} onClick={() => loadNaturalRecord(record, setNaturalRecord, setNaturalPrompt, setNaturalThinking, setNaturalViews, setActiveNaturalView)}><span>{record.diagram.ir.title}</span><small>{effectiveNaturalViews(record).length}개 형식 · r{record.revision}</small></button>)}</div>}
        </form>
        <section className="panel preview">
          <div className="panel-heading"><div><p className="section-label">PREVIEW</p><h2>{diagram?.ir.title ?? "생성 결과"}</h2></div>{diagram && <span className="status-chip">{formatDiagramType(diagram.type)} · v{diagram.version}</span>}</div>
          {naturalResults.length > 0 && <div className="diagram-type-tabs">{naturalResults.map((view) => <button type="button" key={view.viewId} className={selectedNatural?.viewId === view.viewId ? "active" : ""} onClick={() => setActiveNaturalView(view.viewId)}>{formatDiagramType(view.selection.diagramType)}{view.state === "Failed" ? " · 실패" : ""}</button>)}</div>}
          {naturalRecord && selectedNatural && <div className="diagram-meta"><span>{selectedNatural.reused ? "이전 결과 재사용" : selectedNatural.state === "Failed" ? "생성 실패 · 마지막 정상 결과 표시" : "LLM 생성"}</span><button type="button" className="secondary" disabled={busy} onClick={() => void regenerateNatural(selectedNatural.viewId)}>{elapsedLabel("이 형식 다시 생성", busyAction === `natural-regenerate-${selectedNatural.viewId}`, busySeconds)}</button></div>}
          {selectedNatural?.errorMessage && <p className="warning">{selectedNatural.errorMessage}</p>}
          {diagram && naturalRecord && selectedNatural ? <DiagramEditor artifact={diagram} downloadName={`natural-${diagram.type}`} reportError={reportError} onSave={(input) => api.saveNaturalDiagramEdit(naturalRecord.id, selectedNatural.viewId, input)} /> : <EmptyState text="요청을 입력하면 결과가 여기에 표시됩니다." />}
          {naturalRevisions.length > 1 && <div className="revision-list horizontal"><strong>요청 리비전</strong>{naturalRevisions.map((record) => <button type="button" className={naturalRecord?.id === record.id ? "active" : ""} key={record.id} onClick={() => loadNaturalRecord(record, setNaturalRecord, setNaturalPrompt, setNaturalThinking, setNaturalViews, setActiveNaturalView)}>r{record.revision} · {record.source === "manualDsl" ? "DSL 편집" : "생성/구성"}</button>)}</div>}
        </section>
      </section>}

      {tab === "analysis" && <AnalysisWorkspace repositories={repositories} reportError={reportError} />}

      {tab === "repositories" && <section className="repository-layout">
        <form className="panel controls" onSubmit={registerRepository}><p className="section-label">ADMIN</p><h2>사내 PC의 Git 저장소 등록</h2>
          <label>표시 이름<input value={repositoryName} onChange={(event) => setRepositoryName(event.target.value)} required placeholder="VSAssist" /></label>
          <label>Git 저장소 로컬 경로<input value={repositoryPath} onChange={(event) => { setRepositoryPath(event.target.value); setRepositoryInspection(null); }} required placeholder="C:\Work\Git\VSAssist" /></label>
          <label>기본 Branch<input value={defaultBranch} onChange={(event) => setDefaultBranch(event.target.value)} required /></label>
          <div className="button-row"><button type="button" className="secondary" disabled={busy || !repositoryPath.trim()} onClick={() => void inspectRepository()}>{elapsedLabel("연결 테스트", busyAction === "repository-inspect", busySeconds)}</button><button className="primary" disabled={busy || !repositoryInspection}>{elapsedLabel("저장소 등록", busyAction === "repository-register", busySeconds)}</button></div>
          {repositoryInspection && <div className="connection-card"><strong>연결 성공</strong><span>{repositoryInspection.isBare ? "Bare repository" : "Working repository"}</span><code>{repositoryInspection.normalizedPath}</code><span>{repositoryInspection.defaultBranch} · {repositoryInspection.headSha.slice(0, 10)}</span><small>{repositoryInspection.headMessage}</small></div>}
        </form>
        <section className="panel"><p className="section-label">REGISTERED</p><h2>사용 가능한 저장소</h2><ul className="repository-list">{repositories.map((repository) => <li key={repository.id}><div><strong>{repository.name}</strong><span>{repository.defaultBranch}</span></div><code title={repository.localPath}>{repository.localPath}</code><RepositoryRuleEditor repository={repository} reportError={reportError} onSaved={(updated) => setRepositories((current) => current.map((item) => item.id === updated.id ? updated : item))} /></li>)}</ul>{repositories.length === 0 && <EmptyState text="등록된 저장소가 없습니다." />}</section>
      </section>}

      {tab === "llm" && <section className="llm-test-layout">
        <section className="panel controls"><p className="section-label">SYNTHETIC DATA ONLY</p><h2>사내 LLM 연결 점검</h2><p className="help">실제 저장소·커밋·사용자 요청을 보내지 않고 고정된 합성 데이터만 사용합니다.</p><button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("connection")}>{elapsedLabel("1. 기본 연결 시험", busyAction === "llm-connection", busySeconds)}</button><button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("diagram")}>{elapsedLabel("2. DiagramIR 계약 시험", busyAction === "llm-diagram", busySeconds)}</button><button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("thinking")}>{elapsedLabel("3. Thinking 계약 시험", busyAction === "llm-thinking", busySeconds)}</button></section>
        <section className="panel"><p className="section-label">BOUNDED DIAGNOSTICS</p><h2>시험 결과</h2><div className="llm-result-list"><LlmTestCard title="기본 연결" value={llmTests.connection} /><LlmTestCard title="DiagramIR 구조화" value={llmTests.diagram} /><LlmTestCard title="Thinking 구조화" value={llmTests.thinking} /></div></section>
      </section>}
    </main>
  </div>;
}

function LlmTestCard({ title, value }: { title: string; value?: LlmTestValue }) {
  return <article className={`llm-result-card ${value?.success ? "success" : ""}`}><div><strong>{title}</strong><span>{value ? value.success ? "성공" : "실패" : "미실행"}</span></div>{value && <dl><dt>응답 시간</dt><dd>{value.elapsedMilliseconds} ms</dd><dt>종료 사유</dt><dd>{value.finishReason || "미제공"}</dd><dt>출력 토큰 상한</dt><dd>{value.requestedMaxOutputTokens}</dd><dt>토큰 사용량</dt><dd>{value.promptTokens ?? "-"} / {value.completionTokens ?? "-"} / {value.totalTokens ?? "-"}</dd>{"nodeCount" in value && <><dt>노드 / 관계</dt><dd>{value.nodeCount} / {value.edgeCount}</dd></>}</dl>}</article>;
}

function effectiveNaturalViews(record: NaturalDiagramRecord) {
  if (record.views?.length) return record.views;
  const selection: DiagramViewSelection = record.request.views?.[0] ?? {
    id: "primary", diagramType: record.request.diagramType, presetId: record.request.presetId, overrides: record.request.style,
  };
  return [{ viewId: selection.id, selection, diagram: record.diagram, state: "Completed", reused: record.reused }];
}
function changedNaturalViewIds(views: DiagramViewSelection[], record: NaturalDiagramRecord) {
  const previous = new Map(effectiveNaturalViews(record).map((view) => [view.viewId, view.selection]));
  return views.filter((view) => !previous.has(view.id) || JSON.stringify(previous.get(view.id)) !== JSON.stringify(view)).map((view) => view.id);
}
function loadNaturalRecord(
  record: NaturalDiagramRecord,
  setRecord: (value: NaturalDiagramRecord) => void,
  setPrompt: (value: string) => void,
  setThinking: (value: boolean) => void,
  setViews: (value: DiagramViewSelection[]) => void,
  setActiveView: (value: string) => void,
) {
  const views = effectiveNaturalViews(record);
  setRecord(record);
  setPrompt(record.request.prompt);
  setThinking(record.request.enableThinking);
  setViews(views.map((view) => view.selection));
  setActiveView(views[0]?.viewId ?? "");
}
function defaultPreset(type: DiagramType, presets: DiagramPreset[]) { return presets.find((preset) => preset.type === type && preset.detailLevel === "balanced")?.id ?? presets.find((preset) => preset.type === type)?.id ?? "balanced"; }
function formatDiagramType(type: string) { return ({ flowchart: "흐름 / 영향도", class: "클래스 관계", sequence: "호출 시퀀스", "code-relation": "코드 관계도", state: "상태 전이" } as Record<string, string>)[type] ?? type; }
function EmptyState({ text }: { text: string }) { return <div className="empty-state"><p>{text}</p></div>; }
function messageOf(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback; }
