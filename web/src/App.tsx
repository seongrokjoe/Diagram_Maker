import { FormEvent, useCallback, useEffect, useRef, useState } from "react";
import { AnalysisWorkspace } from "./AnalysisWorkspace";
import { CodeBlockWorkspace } from "./CodeBlockWorkspace";
import { CodeDiagramTest } from "./CodeDiagramTest";
import { api } from "./api";
import { createFeatureErrors, type FeatureMessages } from "./featureErrors";
import { DiagramEditor } from "./DiagramEditor";
import { PresetPicker } from "./PresetPicker";
import { RepositoryRuleEditor } from "./RepositoryRuleEditor";

import type { RuntimeInfo } from "./runtimeTypes";
import { elapsedLabel, useElapsedSeconds } from "./useElapsedSeconds";
import type {
  DiagramPreset,
  DiagramType,
  DiagramViewSelection,
  LlmConnectionTestResult,
  LlmContractTestResult,
  LlmThinkingContractTestResult,
  NaturalDiagramRecord,
  NaturalDiagramRun,
  Repository,
  RepositoryInspection,
} from "./types";

type Tab = "natural" | "analysis" | "code-block" | "repositories" | "llm";
type LlmTestKind = "connection" | "diagram" | "thinking";
type LlmTestValue = LlmConnectionTestResult | LlmContractTestResult | LlmThinkingContractTestResult;
const diagramTypes: Array<{ value: DiagramType; label: string }> = [
  { value: "flowchart", label: "Flow / Component" },
  { value: "sequence", label: "Sequence" },
  { value: "class", label: "Class" },
  { value: "state", label: "State" },
];

export default function App() {
  const [runtime, setRuntime] = useState<RuntimeInfo | null>(null);
  const [startupError, setStartupError] = useState("");
  useEffect(() => {
    void api.runtimeInfo().then(setRuntime).catch(reason => setStartupError(messageOf(reason, "실행 모드를 확인하지 못했습니다.")));
  }, []);
  // Never mount the ordinary repository/prompt controls until server mode is known.
  if (!runtime) return <main className="panel"><p>{startupError || "실행 환경 확인 중…"}</p>
    {startupError && <button type="button" onClick={() => window.location.reload()}>다시 확인</button>}</main>;
  return <NormalApp />;
}

function NormalApp() {
  const [tab, setTab] = useState<Tab>(() => new URLSearchParams(window.location.search).has("codeWorkspace") ? "code-block" : "natural");
  const [repositories, setRepositories] = useState<Repository[]>([]);
  const [naturalRecord, setNaturalRecord] = useState<NaturalDiagramRecord | null>(null);
  const [naturalRun, setNaturalRun] = useState<NaturalDiagramRun | null>(null);
  const [naturalRuns, setNaturalRuns] = useState<NaturalDiagramRun[]>([]);
  const naturalSelection = useRef(0);
  const [naturalPrompt, setNaturalPrompt] = useState("");
  const [naturalThinking, setNaturalThinking] = useState(false);
  const [naturalHistory, setNaturalHistory] = useState<NaturalDiagramRecord[]>([]);
  const [naturalRevisions, setNaturalRevisions] = useState<NaturalDiagramRecord[]>([]);
  const [naturalViews, setNaturalViews] = useState<DiagramViewSelection[]>([
    { id: "natural-view-1", diagramType: "flowchart", presetId: "" },
  ]);
  const [activeNaturalView, setActiveNaturalView] = useState("natural-view-1");
  const [activeNaturalPage, setActiveNaturalPage] = useState("");
  const [naturalPresets, setNaturalPresets] = useState<DiagramPreset[]>([]);
  const [repositoryName, setRepositoryName] = useState("");
  const [repositoryPath, setRepositoryPath] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [repositoryInspection, setRepositoryInspection] = useState<RepositoryInspection | null>(null);
  const [llmTests, setLlmTests] = useState<Partial<Record<LlmTestKind, LlmTestValue>>>({});
  const [busyAction, setBusyAction] = useState("");
  const [errors, setErrors] = useState<FeatureMessages>({});
  const [errorChannels] = useState(() => createFeatureErrors(setErrors));
  const { natural: reportError, analysis: reportAnalysisError, repositories: reportRepositoryError } = errorChannels.reporters;
  const error = errors[tab] ?? "";
  const busy = Boolean(busyAction);
  const busySeconds = useElapsedSeconds(busy);

  const loadRepositories = useCallback(async () => {
    try { setRepositories(await api.listRepositories()); }
    catch (reason) { reportRepositoryError(messageOf(reason, "저장소 목록을 불러오지 못했습니다.")); }
  }, []);

  const loadNaturalHistory = useCallback(async () => {
    try { setNaturalHistory(await api.listNaturalDiagrams()); }
    catch (reason) { reportError(messageOf(reason, "다이어그램 이력을 불러오지 못했습니다.")); }
  }, []);

  useEffect(() => { void loadRepositories(); void loadNaturalHistory(); }, [loadRepositories, loadNaturalHistory]);
  useEffect(() => {
    void api.listNaturalDiagramRuns().then((runs) => {
      setNaturalRuns(runs);
      const recoverable = runs.find(run => run.state === "Queued" || run.state === "Generating");
      if (recoverable) void openNaturalRun(recoverable);
    }).catch((reason: unknown) => reportError(messageOf(reason, "자연어 실행 이력을 불러오지 못했습니다.")));
  }, []);
  useEffect(() => {
    if (naturalRun) setNaturalRuns(runs => [naturalRun, ...runs.filter(run => run.id !== naturalRun.id)].slice(0, 20));
  }, [naturalRun]);

  async function openNaturalRun(run: NaturalDiagramRun) {
    const selection = ++naturalSelection.current;
    setNaturalRun(run);
    setNaturalRecord(null);
    setNaturalPrompt(run.request.prompt);
    setNaturalThinking(run.request.enableThinking);
    const views = run.request.views ?? [{ id: "primary", diagramType: run.request.diagramType, presetId: run.request.presetId }];
    setNaturalViews(views);
    setActiveNaturalView(views[0]?.id ?? "");
    setActiveNaturalPage(run.views?.[0]?.pages?.[0]?.id ?? "");
    if (run.resultDiagramId) {
      try {
        const record = await api.getNaturalDiagram(run.resultDiagramId);
        if (selection === naturalSelection.current) setNaturalRecord(record);
      } catch (reason) {
        if (selection === naturalSelection.current) reportError(messageOf(reason, "저장된 자연어 결과를 불러오지 못했습니다."));
      }
    }
  }
  function selectNaturalRecord(record: NaturalDiagramRecord) {
    naturalSelection.current++;
    setNaturalRun(null);
    loadNaturalRecord(record, setNaturalRecord, setNaturalPrompt, setNaturalThinking, setNaturalViews, setActiveNaturalView, setActiveNaturalPage);
  }
  useEffect(() => {
    void api.listPresets().then((items) => {
      setNaturalPresets(items);
      setNaturalViews((current) => current.map((view) => items.some((item) => item.id === view.presetId)
        ? view
        : { ...view, presetId: defaultPreset(view.diagramType, items) }));
    }).catch((reason: unknown) => reportError(messageOf(reason, "샘플 목록을 불러오지 못했습니다.")));
  }, []);
  useEffect(() => {
    if (!naturalRecord) { setNaturalRevisions([]); return; }
    let active = true;
    void api.listNaturalDiagramRevisions(naturalRecord.id).then(records => { if (active) setNaturalRevisions(records); })
      .catch((reason: unknown) => { if (active) reportError(messageOf(reason, "리비전 이력을 불러오지 못했습니다.")); });
    return () => { active = false; };
  }, [naturalRecord?.id]);
  useEffect(() => {
    if (!naturalRun || (naturalRun.state !== "Queued" && naturalRun.state !== "Generating")) return;
    let stopped = false;
    let timer = 0;
    const poll = async () => {
      try {
        const current = await api.getNaturalDiagramRun(naturalRun.id);
        if (stopped) return;
        let completedRecord: NaturalDiagramRecord | null = null;
        if (current.resultDiagramId) {
          completedRecord = await api.getNaturalDiagram(current.resultDiagramId);
          if (stopped) return;
          setNaturalRecord(completedRecord);
          const view = effectiveNaturalViews(completedRecord).find(item => item.viewId === activeNaturalView) ?? effectiveNaturalViews(completedRecord)[0];
          setActiveNaturalView(view?.viewId ?? "");
          setActiveNaturalPage(page => view && effectiveNaturalPages(view).some(item => item.id === page)
            ? page : view ? effectiveNaturalPages(view)[0]?.id ?? "" : "");
          await loadNaturalHistory();
        }
        if (stopped) return;
        setNaturalRun(current);
        if (current.state === "Queued" || current.state === "Generating") timer = window.setTimeout(poll, 500);
      } catch (reason) {
        if (!stopped) reportError(messageOf(reason, "자연어 실행 상태를 불러오지 못했습니다."));
      }
    };
    timer = window.setTimeout(poll, 250);
    return () => { stopped = true; window.clearTimeout(timer); };
  }, [naturalRun?.id, naturalRun?.state]);

  async function createNatural(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    naturalSelection.current++;
    setBusyAction("natural-create"); const reportAttemptError = errorChannels.begin("natural");
    try {
      const primary = naturalViews[0];
      const sameSource = naturalRecord && naturalRecord.request.prompt === naturalPrompt && naturalRecord.request.enableThinking === naturalThinking;
      const run = await api.createNaturalDiagramRun({
        request: {
          prompt: naturalPrompt,
          diagramType: primary.diagramType,
          presetId: primary.presetId,
          views: naturalViews,
          enableThinking: naturalThinking,
          forceRegenerate: false,
        },
        sourceDiagramId: sameSource ? naturalRecord.id : undefined,
        regenerateViewIds: sameSource ? changedNaturalViewIds(naturalViews, naturalRecord) : undefined,
      });
      setNaturalRun(run);
      setActiveNaturalView(primary.id);
      setActiveNaturalPage("");
    } catch (reason) { reportAttemptError(messageOf(reason, "다이어그램 생성에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function regenerateNatural(viewId: string, pageId?: string) {
    if (!naturalRecord) return;
    setBusyAction(`natural-regenerate-${viewId}`); const reportAttemptError = errorChannels.begin("natural");
    try {
      const views = naturalRecord.request.views ?? naturalViews;
      const run = await api.createNaturalDiagramRun({
        request: { ...naturalRecord.request, views, forceRegenerate: false },
        sourceDiagramId: naturalRecord.id,
        regenerateViewIds: pageId ? undefined : [viewId],
        regeneratePageIds: pageId ? [pageId] : undefined,
      });
      setNaturalRun(run);
      setActiveNaturalView(viewId);
      if (pageId) setActiveNaturalPage(pageId);
    }
    catch (reason) { reportAttemptError(messageOf(reason, "다이어그램 재생성에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function cancelNaturalRun() {
    if (!naturalRun) return;
    const reportAttemptError = errorChannels.begin("natural");
    try {
      const current = await api.getNaturalDiagramRun(naturalRun.id);
      setNaturalRun(await api.cancelNaturalDiagramRun(current.id, current.revision));
    } catch (reason) { reportAttemptError(messageOf(reason, "자연어 실행을 취소하지 못했습니다.")); }
  }

  async function resumeNaturalRun() {
    if (!naturalRun) return;
    const reportAttemptError = errorChannels.begin("natural");
    try {
      const current = await api.getNaturalDiagramRun(naturalRun.id);
      setNaturalRun(await api.resumeNaturalDiagramRun(current.id, current.revision));
    } catch (reason) { reportAttemptError(messageOf(reason, "자연어 실행을 이어서 시작하지 못했습니다.")); }
  }

  async function inspectRepository() {
    setBusyAction("repository-inspect"); const reportAttemptError = errorChannels.begin("repositories"); setRepositoryInspection(null);
    try {
      const result = await api.inspectRepository(repositoryPath);
      setRepositoryInspection(result); setRepositoryPath(result.normalizedPath); setDefaultBranch(result.defaultBranch);
      if (!repositoryName.trim()) setRepositoryName(result.normalizedPath.split(/[\\/]/).filter(Boolean).at(-1) ?? "Local repository");
    } catch (reason) { reportAttemptError(messageOf(reason, "Git 저장소 연결 테스트에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function registerRepository(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!repositoryInspection || repositoryInspection.normalizedPath !== repositoryPath) { reportRepositoryError("현재 경로의 연결 테스트를 먼저 완료하세요."); return; }
    setBusyAction("repository-register"); const reportAttemptError = errorChannels.begin("repositories");
    try {
      await api.registerRepository({ name: repositoryName, localPath: repositoryPath, defaultBranch });
      setRepositoryName(""); setRepositoryPath(""); setDefaultBranch("main"); setRepositoryInspection(null);
      await loadRepositories();
    } catch (reason) { reportAttemptError(messageOf(reason, "저장소 등록에 실패했습니다.")); }
    finally { setBusyAction(""); }
  }

  async function runLlmTest(kind: LlmTestKind) {
    setBusyAction(`llm-${kind}`); const reportAttemptError = errorChannels.begin("llm");
    try {
      const result = kind === "connection" ? await api.testLlmConnection()
        : kind === "diagram" ? await api.testLlmDiagramContract() : await api.testLlmThinkingContract();
      setLlmTests((current) => ({ ...current, [kind]: result }));
    } catch (reason) { reportAttemptError(messageOf(reason, "사내 LLM 시험에 실패했습니다.")); }
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

  const naturalRunActive = naturalRun?.state === "Queued" || naturalRun?.state === "Generating";
  const naturalResults = naturalRun?.views?.length ? naturalRun.views : naturalRecord ? effectiveNaturalViews(naturalRecord) : [];
  const naturalRequirements = naturalRun?.requirements ?? naturalRecord?.requirements;
  const selectedNatural = naturalResults.find((view) => view.viewId === activeNaturalView) ?? naturalResults[0];
  const naturalPages = selectedNatural ? effectiveNaturalPages(selectedNatural) : [];
  const selectedNaturalPage = naturalPages.find(page => page.id === activeNaturalPage) ?? naturalPages[0];
  const diagram = selectedNaturalPage?.diagram ?? selectedNaturalPage?.lastSuccessfulDiagram ?? selectedNatural?.diagram ?? selectedNatural?.lastSuccessfulDiagram;
  const canEditNatural = Boolean(naturalRecord && (!naturalRun || naturalRun.resultDiagramId === naturalRecord.id));
  return <div className="app-shell">
    <header className="topbar"><div><p className="eyebrow">INTERNAL · SOURCE SAFE</p><h1>AI Git Architecture Reviewer</h1></div><span className="network-badge">외부 전송 없음</span></header>
    <nav className="tabs" aria-label="주요 기능">
      <button className={tab === "natural" ? "active" : ""} onClick={() => setTab("natural")}>자연어 다이어그램</button>
      <button className={tab === "code-block" ? "active" : ""} onClick={() => setTab("code-block")}>코드 블럭 다이어그램</button>
      <button className={tab === "analysis" ? "active" : ""} onClick={() => setTab("analysis")}>Git 변경 분석</button>
      <button className={tab === "repositories" ? "active" : ""} onClick={() => setTab("repositories")}>저장소 관리</button>
      <button className={tab === "llm" ? "active" : ""} onClick={() => setTab("llm")}>LLM 점검</button>
    </nav>
    {error && <div className="error-panel" role="alert">{error}<button type="button" className="secondary" onClick={() => errorChannels.clear(tab)}>오류 닫기</button></div>}
    <main>
      {tab === "natural" && <section className="workspace-grid natural-workspace">
        <form className="panel controls" onSubmit={createNatural}>
          <p className="section-label">NATURAL LANGUAGE</p><h2>요청을 구조화된 다이어그램으로 변환</h2>
          <label>요청<textarea name="prompt" required rows={8} value={naturalPrompt} onChange={(event) => setNaturalPrompt(event.target.value)} placeholder="예: 사용자가 API Gateway를 통해 주문 서비스와 데이터베이스를 호출하는 순서를 그려줘" /></label>
          <div className="view-editor-heading"><strong>출력 형식 ({naturalViews.length}/4)</strong><button type="button" className="secondary" disabled={naturalViews.length >= 4 || naturalViews.length >= diagramTypes.length} onClick={addNaturalView}>형식 추가</button></div>
          {naturalViews.map((view, index) => {
            const typePresets = naturalPresets.filter((preset) => preset.type === view.diagramType);
            return <fieldset className="diagram-view-editor" key={view.id}><legend>다이어그램 {index + 1}</legend>
              <div className="natural-type-row"><label><span>다이어그램 종류</span><select value={view.diagramType} onChange={(event) => { const diagramType = event.target.value as DiagramType; updateNaturalView(view.id, { diagramType, presetId: defaultPreset(diagramType, naturalPresets), overrides: undefined }); }}>{diagramTypes.map((item) => <option key={item.value} value={item.value} disabled={naturalViews.some((other) => other.id !== view.id && other.diagramType === item.value)}>{item.label}</option>)}</select></label>{naturalViews.length > 1 && <button type="button" className="text-button danger" onClick={() => setNaturalViews((current) => current.filter((item) => item.id !== view.id))}>삭제</button>}</div>
              <PresetPicker layout="list" presets={typePresets} selectedId={view.presetId} onSelect={(preset) => updateNaturalView(view.id, { presetId: preset.id, overrides: undefined })} />
            </fieldset>;
          })}
          <label className="checkbox"><input type="checkbox" name="enableThinking" checked={naturalThinking} onChange={(event) => setNaturalThinking(event.target.checked)} /> Thinking 모드 사용</label>
          <button className="primary" disabled={busy || naturalRunActive || !naturalPrompt.trim() || naturalViews.some((view) => !view.presetId)}>{elapsedLabel(naturalRecord?.request.prompt === naturalPrompt && naturalRecord.request.enableThinking === naturalThinking ? "선택 구성 적용" : "다이어그램 생성", busyAction === "natural-create", busySeconds)}</button>
          <p className="help">같은 요청·종류·샘플은 같은 결과를 재사용합니다. 다른 결과가 필요할 때만 재생성하세요.</p>
          {naturalRuns.length > 0 && <details className="natural-run-history"><summary>최근 실행 ({naturalRuns.length})</summary><div className="revision-list">
            {naturalRuns.map(run => <button type="button" key={run.id} className={naturalRun?.id === run.id ? "active" : ""} onClick={() => void openNaturalRun(run)}>
              <span>{run.request.prompt.slice(0, 80)}</span><small>{run.state} · {run.progress}%</small>
            </button>)}
          </div></details>}
          {naturalHistory.length > 0 && <div className="revision-list"><strong>최근 다이어그램</strong>{naturalHistory.map((record) => <button type="button" className={naturalRecord?.id === record.id && !naturalRun ? "active" : ""} key={record.id} onClick={() => selectNaturalRecord(record)}><span>{record.diagram.ir.title}</span><small>{effectiveNaturalViews(record).length}개 형식 · r{record.revision}</small></button>)}</div>}
        </form>
        <section className="panel preview">
          <div className="panel-heading"><div><p className="section-label">PREVIEW</p><h2>{diagram?.ir.title ?? "생성 결과"}</h2></div>{diagram && <span className="status-chip">{formatDiagramType(diagram.type)} · v{diagram.version}</span>}</div>
          {naturalRun && <div className="connection-card natural-run-status" role="status"><strong>{naturalRun.stageMessage}</strong><progress max={100} value={naturalRun.progress} /><span>{naturalRun.progress}% · {naturalRun.state}</span>{naturalRun.errorMessage && <small>{naturalRun.errorMessage}</small>}<div className="button-row">
            {naturalRunActive && <button type="button" className="secondary" onClick={() => void cancelNaturalRun()}>실행 취소</button>}
            {(naturalRun.state === "Partial" || naturalRun.state === "Failed" || naturalRun.state === "Cancelled") && <button type="button" className="secondary" onClick={() => void resumeNaturalRun()}>저장 지점에서 이어하기</button>}
          </div></div>}
          {naturalResults.length > 0 && <div className="diagram-type-tabs">{naturalResults.map((view) => <button type="button" key={view.viewId} className={selectedNatural?.viewId === view.viewId ? "active" : ""} onClick={() => { setActiveNaturalView(view.viewId); setActiveNaturalPage(effectiveNaturalPages(view)[0]?.id ?? ""); }}>{formatDiagramType(view.selection.diagramType)}{view.state === "Failed" ? " · 실패" : view.state === "Partial" ? " · 부분" : ""}</button>)}</div>}
          {naturalPages.length > 1 && <div className="diagram-type-tabs natural-scenario-tabs" aria-label="자연어 시나리오 결과">{naturalPages.map(page =>
            <button type="button" key={page.id} className={selectedNaturalPage?.id === page.id ? "active" : ""} onClick={() => setActiveNaturalPage(page.id)}>{page.title}{page.state === "Failed" ? " · 실패" : ""}</button>)}</div>}
          {naturalRecord && selectedNatural && <div className="diagram-meta"><span>{selectedNaturalPage?.reused || selectedNatural.reused ? "이전 결과 재사용" : selectedNaturalPage?.state === "Failed" ? "생성 실패 · 마지막 정상 결과 표시" : "LLM 생성"}</span><div className="button-row"><button type="button" className="secondary" disabled={busy || naturalRunActive} onClick={() => void regenerateNatural(selectedNatural.viewId)}>{elapsedLabel("이 형식 다시 생성", busyAction === `natural-regenerate-${selectedNatural.viewId}`, busySeconds)}</button>{selectedNaturalPage && <button type="button" className="secondary" disabled={busy || naturalRunActive} onClick={() => void regenerateNatural(selectedNatural.viewId, selectedNaturalPage.id)}>이 시나리오 다시 생성</button>}</div></div>}
          {(selectedNaturalPage?.errorMessage ?? selectedNatural?.errorMessage) && <p className="warning">{selectedNaturalPage?.errorMessage ?? selectedNatural?.errorMessage}</p>}
          {(selectedNaturalPage?.designQuality ?? selectedNatural?.designQuality) && (() => { const quality = selectedNaturalPage?.designQuality ?? selectedNatural!.designQuality!; return <details className="diagnostic-panel"><summary>{quality.status === "Reviewed" ? "생성 원본 설계 검토 완료" : "검토 후 편집됨"} · 요구사항 {quality.reviewedRequirementIds.length}개 · 설계 가정 {quality.assumptionElementIds.length}개</summary>
            <p className="help">설계 가정은 그림에 표시됩니다. 수동 편집한 내용은 별도 검토가 필요합니다.</p>
            <ul>{naturalRequirements?.requirements.filter(item => !selectedNaturalPage?.scenarioId || item.scenarioId === selectedNaturalPage.scenarioId).map(item => { const range = naturalRequirements.sourceRanges?.find(source => source.id === item.sourceRangeId); return <li key={item.id}>{item.origin === "assumption" ? "설계 가정: " : "요구사항: "}{item.text}{range && <small> · 원문 {range.startOffset + 1}–{range.endOffset}</small>}</li>; })}</ul>
          </details>; })()}
          {diagram && naturalRecord && selectedNatural && canEditNatural ? <DiagramEditor artifact={diagram} downloadName={`natural-${diagram.type}`} reportError={reportError} zoomable canvasToolbar
            onSave={(input) => api.saveNaturalDiagramEdit(naturalRecord.id, selectedNatural.viewId, input, selectedNaturalPage?.id)}
            onPreview={(input, signal) => api.previewNaturalDiagramEdit(naturalRecord.id, selectedNatural.viewId, input, signal, selectedNaturalPage?.id)} /> : diagram ? <p className="help">실행이 완료되어 결과가 저장되면 이 페이지를 직접 편집할 수 있습니다.</p> : <EmptyState text="요청을 입력하면 결과가 여기에 표시됩니다." />}
          {naturalRevisions.length > 1 && <div className="revision-list horizontal"><strong>요청 리비전</strong>{naturalRevisions.map((record) => <button type="button" className={naturalRecord?.id === record.id ? "active" : ""} key={record.id} onClick={() => selectNaturalRecord(record)}>r{record.revision} · {record.source === "manualDsl" ? "DSL 편집" : "생성/구성"}</button>)}</div>}
        </section>
      </section>}

      {tab === "analysis" && <AnalysisWorkspace repositories={repositories} reportError={reportAnalysisError} />}
      <div hidden={tab !== "code-block"}><CodeBlockWorkspace /></div>

      {tab === "repositories" && <section className="repository-layout">
        <form className="panel controls" onSubmit={registerRepository}><p className="section-label">ADMIN</p><h2>사내 PC의 Git 저장소 등록</h2>
          <label>표시 이름<input value={repositoryName} onChange={(event) => setRepositoryName(event.target.value)} required placeholder="VSAssist" /></label>
          <label>Git 저장소 로컬 경로<input value={repositoryPath} onChange={(event) => { setRepositoryPath(event.target.value); setRepositoryInspection(null); }} required placeholder="C:\Work\Git\VSAssist" /></label>
          <label>기본 Branch<input value={defaultBranch} onChange={(event) => setDefaultBranch(event.target.value)} required /></label>
          <div className="button-row"><button type="button" className="secondary" disabled={busy || !repositoryPath.trim()} onClick={() => void inspectRepository()}>{elapsedLabel("연결 테스트", busyAction === "repository-inspect", busySeconds)}</button><button className="primary" disabled={busy || !repositoryInspection}>{elapsedLabel("저장소 등록", busyAction === "repository-register", busySeconds)}</button></div>
          {repositoryInspection && <div className="connection-card"><strong>연결 성공</strong><span>{repositoryInspection.isBare ? "Bare repository" : "Working repository"}</span><code>{repositoryInspection.normalizedPath}</code><span>{repositoryInspection.defaultBranch} · {repositoryInspection.headSha.slice(0, 10)}</span><small>{repositoryInspection.headMessage}</small></div>}
        </form>
        <section className="panel"><p className="section-label">REGISTERED</p><h2>사용 가능한 저장소</h2><ul className="repository-list">{repositories.map((repository) => <li key={repository.id}><div><strong>{repository.name}</strong><span>{repository.defaultBranch}</span></div><code title={repository.localPath}>{repository.localPath}</code><RepositoryRuleEditor repository={repository} reportError={reportRepositoryError} onSaved={(updated) => setRepositories((current) => current.map((item) => item.id === updated.id ? updated : item))} /></li>)}</ul>{repositories.length === 0 && <EmptyState text="등록된 저장소가 없습니다." />}</section>
      </section>}

      {tab === "llm" && <section className="llm-test-layout">
        <section className="panel controls"><p className="section-label">SYNTHETIC DATA ONLY</p><h2>사내 LLM 연결 점검</h2><p className="help">실제 저장소·커밋·사용자 요청을 보내지 않고 고정된 합성 데이터만 사용합니다.</p><button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("connection")}>{elapsedLabel("1. 기본 연결 시험", busyAction === "llm-connection", busySeconds)}</button><button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("diagram")}>{elapsedLabel("2. DiagramIR 계약 시험", busyAction === "llm-diagram", busySeconds)}</button><button type="button" className="secondary" disabled={busy} onClick={() => void runLlmTest("thinking")}>{elapsedLabel("3. Thinking 계약 시험", busyAction === "llm-thinking", busySeconds)}</button></section>
        <section className="panel"><p className="section-label">BOUNDED DIAGNOSTICS</p><h2>시험 결과</h2><div className="llm-result-list"><LlmTestCard title="기본 연결" value={llmTests.connection} /><LlmTestCard title="DiagramIR 구조화" value={llmTests.diagram} /><LlmTestCard title="Thinking 구조화" value={llmTests.thinking} /><CodeDiagramTest /></div></section>
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
function effectiveNaturalPages(view: ReturnType<typeof effectiveNaturalViews>[number]) {
  if (view.pages?.length) return view.pages;
  return [{ id: `${view.viewId}-scenario-1`, scenarioId: "scenario-1", title: view.diagram?.ir.title ?? "시나리오 1",
    diagram: view.diagram, state: view.state, errorCode: view.errorCode, errorMessage: view.errorMessage,
    lastSuccessfulDiagram: view.lastSuccessfulDiagram, reused: view.reused, designQuality: view.designQuality }];
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
  setActivePage: (value: string) => void,
) {
  const views = effectiveNaturalViews(record);
  setRecord(record);
  setPrompt(record.request.prompt);
  setThinking(record.request.enableThinking);
  setViews(views.map((view) => view.selection));
  setActiveView(views[0]?.viewId ?? "");
  setActivePage(views[0] ? effectiveNaturalPages(views[0])[0]?.id ?? "" : "");
}
function defaultPreset(type: DiagramType, presets: DiagramPreset[]) { return presets.find((preset) => preset.type === type && preset.detailLevel === "balanced")?.id ?? presets.find((preset) => preset.type === type)?.id ?? "balanced"; }
function formatDiagramType(type: string) { return ({ flowchart: "흐름 / 영향도", class: "클래스 관계", sequence: "호출 시퀀스", "code-relation": "변경 구현 맵", state: "상태 전이" } as Record<string, string>)[type] ?? type; }
function EmptyState({ text }: { text: string }) { return <div className="empty-state"><p>{text}</p></div>; }
function messageOf(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback; }
