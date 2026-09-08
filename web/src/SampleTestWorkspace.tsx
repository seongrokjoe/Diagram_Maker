import { useCallback, useEffect, useState } from "react";
import { api } from "./api";
import { AnalysisResultView } from "./AnalysisWorkspace";
import { DiagramEditor } from "./DiagramEditor";
import { useElapsedSeconds } from "./useElapsedSeconds";
import type { AnalysisPlan, AnalysisResponse, DiagramPreset, DiagramViewSelection, NaturalDiagramRecord } from "./types";
import type { RuntimeInfo, SampleCatalog, SampleGenerateInput, SampleMetadata } from "./sampleTestTypes";

const terminal = new Set(["Completed", "Partial", "Failed"]);
const names: Record<string, string> = { flowchart: "흐름 / 영향도", sequence: "호출 시퀀스", class: "클래스 관계", "code-relation": "변경 구현 맵", state: "상태 전이" };

export function SampleTestWorkspace({ initialRuntime }: { initialRuntime: RuntimeInfo }) {
  const [runtime, setRuntime] = useState(initialRuntime);
  const [catalog, setCatalog] = useState<SampleCatalog | null>(null);
  const [scenarioId, setScenarioId] = useState("cpp-packets");
  const [type, setType] = useState("all");
  const [refinementId, setRefinementId] = useState("default");
  const [direction, setDirection] = useState("");
  const [detail, setDetail] = useState("");
  const [presets, setPresets] = useState<DiagramPreset[]>([]);
  const [plan, setPlan] = useState<AnalysisPlan | null>(null);
  const [plans, setPlans] = useState<AnalysisPlan[]>([]);
  const [history, setHistory] = useState<Array<{ id: string; label: string }>>([]);
  const [naturalHistory, setNaturalHistory] = useState<NaturalDiagramRecord[]>([]);
  const [pending, setPending] = useState<AnalysisResponse | null>(null);
  const [analysis, setAnalysis] = useState<AnalysisResponse | null>(null);
  const [natural, setNatural] = useState<NaturalDiagramRecord | null>(null);
  const [activeGroup, setActiveGroup] = useState("");
  const [activeView, setActiveView] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const reportError = useCallback((message: string) => setError(message), []);
  const planRunning = Boolean(plan && !["Ready", "Failed", "Expired"].includes(plan.state));
  const generating = Boolean(pending && !terminal.has(pending.state));
  const locked = busy || planRunning || generating;
  const seconds = useElapsedSeconds(locked);
  const scenario = catalog?.scenarios.find(item => item.id === scenarioId);
  const selectedNatural = natural?.views?.find(view => view.viewId === activeView) ?? natural?.views?.[0];
  const naturalArtifact = selectedNatural?.diagram ?? selectedNatural?.lastSuccessfulDiagram ?? natural?.diagram;

  const refreshPlans = useCallback(async () => setPlans(await api.listAnalysisPlans()), []);
  const refreshHistory = useCallback(async (id: string) => {
    const values = await api.listAnalysisPlanAnalyses(id);
    setHistory(values.map(value => ({ id: value.id, label: `${new Date(value.createdAt).toLocaleString("ko-KR")} · ${value.state}` })));
  }, []);
  useEffect(() => {
    void Promise.all([api.sampleCatalog(), api.listPresets(), api.listAnalysisPlans(), api.listNaturalDiagrams()])
      .then(([samples, presetList, recent, records]) => { setCatalog(samples); setPresets(presetList); setPlans(recent); setNaturalHistory(records); })
      .catch(reason => setError(String(reason)));
  }, []);
  useEffect(() => {
    if (!locked) return;
    const timer = window.setInterval(() => { void api.runtimeInfo().then(setRuntime).catch(() => undefined); }, 1500);
    return () => window.clearInterval(timer);
  }, [locked]);
  useEffect(() => {
    if (!plan || !planRunning) return;
    let active = true;
    const timer = window.setTimeout(() => { void api.getAnalysisPlan(plan.id).then(value => {
      if (!active) return;
      setPlan(value);
      if (value.state === "Ready") void refreshPlans().catch(reason => setError(String(reason)));
    }).catch(reason => { if (active) setError(String(reason)); }); }, 1000);
    return () => { active = false; window.clearTimeout(timer); };
  }, [plan, planRunning, refreshPlans]);
  useEffect(() => {
    if (!pending || !generating) return;
    let active = true;
    const timer = window.setTimeout(() => { void api.getAnalysis(pending.id).then(value => {
      if (!active) return;
      setPending(value);
      if (terminal.has(value.state)) {
        if (value.result) { setAnalysis(value); setNatural(null); }
        if (value.errorMessage) setError(`${value.errorCode}: ${value.errorMessage}`);
        if (plan) void refreshHistory(plan.id).catch(reason => setError(String(reason)));
        void api.runtimeInfo().then(setRuntime).catch(() => undefined);
      }
    }).catch(reason => { if (active) setError(String(reason)); }); }, 1000);
    return () => { active = false; window.clearTimeout(timer); };
  }, [pending, generating, plan, refreshHistory]);

  async function prepare() {
    setBusy(true); setError("");
    try {
      const value = await api.prepareSample(scenarioId);
      setPlan(await api.getAnalysisPlan(value.id));
      setHistory([]);
      await refreshPlans();
    } catch (reason) { setError(String(reason)); }
    finally { setBusy(false); }
  }
  async function generate(overrides?: SampleGenerateInput) {
    setBusy(true); setError("");
    try {
      const input: SampleGenerateInput = overrides ?? {
        planId: plan?.id, refinementId, diagramTypes: type === "all" ? undefined : [type],
        direction: direction || undefined, detailLevel: detail || undefined,
      };
      const value = await api.generateSample(scenarioId, input);
      if (value.kind === "git") {
        const job = await api.getAnalysis(value.id);
        setPending(job);
        if (terminal.has(job.state)) {
          if (job.result) { setAnalysis(job); setNatural(null); }
          if (job.errorMessage) setError(`${job.errorCode}: ${job.errorMessage}`);
          if (plan) await refreshHistory(plan.id);
        }
      }
      else {
        setNatural(value.record); setAnalysis(null); setActiveView(value.record.views?.[0]?.viewId ?? "");
        setNaturalHistory(await api.listNaturalDiagrams());
      }
      setRuntime(await api.runtimeInfo());
    } catch (reason) { setError(String(reason)); }
    finally { setBusy(false); }
  }
  async function regenerateView(_groupId: string, selection: DiagramViewSelection) {
    if (locked) return;
    const choice = catalog?.refinements.find(item => item.instruction === (selection.refinementInstruction ?? ""));
    if (!choice) { setError("준비된 수정 요청을 선택하세요."); return; }
    await generate({ planId: plan?.id, refinementId: choice.id, diagramTypes: [selection.diagramType],
      presetId: selection.presetId, ...selection.overrides, focusOnChanges: selection.focusOnChanges });
  }
  async function restorePlan(id: string) {
    if (!id) return;
    setBusy(true); setError("");
    try {
      const selected = await api.getAnalysisPlan(id);
      const repositories = await api.listRepositories();
      const name = repositories.find(item => item.id === selected.request.repositoryId)?.name;
      const sample = catalog?.scenarios.find(item => item.title === name && item.kind === "git");
      if (!sample) throw new Error("현재 샘플에 해당하는 초안이 아닙니다.");
      setScenarioId(sample.id); setPlan(selected); setNatural(null); setAnalysis(null); setPending(null);
      await refreshHistory(id);
      const recent = await api.listAnalysisPlanAnalyses(id);
      const completed = recent.find(item => item.hasResult);
      if (completed) setAnalysis(await api.getAnalysis(completed.id));
    } catch (reason) { setError(String(reason)); }
    finally { setBusy(false); }
  }
  const metadata = (analysis as (AnalysisResponse & { testMetadata?: SampleMetadata }) | null)?.testMetadata ??
    (natural?.request as (NaturalDiagramRecord["request"] & { testMetadata?: SampleMetadata }) | undefined)?.testMetadata;
  return <main className="sample-workspace">
    <header className="panel sample-banner"><p className="section-label">CODEX · SYNTHETIC DATA ONLY</p>
      <h1>Codex 샘플 테스트</h1><p>사내 모델 검증 아님 · 샘플 코드와 준비된 요청만 OpenAI 서비스로 전송합니다.</p>
      <small>{runtime.model} · Thinking/seed/temperature/토큰 상한은 사내 모델과 동일하게 검증하지 않습니다.</small>
      <p className="help">직접 편집한 내용은 로컬에만 저장합니다. 다시 그리기는 원본 샘플과 선택한 옵션으로 새로 생성하며 직접 입력한 문구를 전송하지 않습니다.</p>
    </header>
    {(error || runtime.codex?.error) && <section role="alert" className="panel analysis-error"><p>{error}</p>
      {runtime.codex?.error && <p>{runtime.codex.errorCode} · {runtime.codex.error}</p>}</section>}
    <section className="panel controls"><div className="field-row">
      <label>테스트 샘플<select value={scenarioId} disabled={locked} onChange={event => {
        setScenarioId(event.target.value); setPlan(null); setPending(null); setAnalysis(null); setNatural(null); setHistory([]); setType("all"); setError("");
      }}>{catalog?.scenarios.map(item => <option key={item.id} value={item.id}>{item.title}</option>)}</select></label>
      <label>다이어그램<select value={type} disabled={locked} onChange={event => setType(event.target.value)}><option value="all">전체 4종</option>
        {["flowchart", "sequence", "class", scenario?.kind === "natural" ? "state" : "code-relation"].map(value => <option key={value} value={value}>{names[value]}</option>)}
      </select></label>
      <label>준비된 수정 요청<select value={refinementId} disabled={locked} onChange={event => setRefinementId(event.target.value)}>
        {catalog?.refinements.map(item => <option key={item.id} value={item.id}>{item.label}</option>)}</select></label>
      <label>방향<select value={direction} disabled={locked} onChange={event => setDirection(event.target.value)}><option value="">기본</option><option value="LR">가로</option><option value="TB">세로</option></select></label>
      <label>상세도<select value={detail} disabled={locked} onChange={event => setDetail(event.target.value)}><option value="">기본</option><option value="compact">간결</option><option value="balanced">균형</option><option value="detailed">상세</option></select></label>
    </div>
      <div className="result-option-actions">{scenario?.kind === "git" && <button type="button" className="secondary" disabled={locked || !catalog} onClick={() => void prepare()}>1. 샘플 변경점 분석</button>}
        <button type="button" className="primary" disabled={locked || !catalog || scenario?.kind === "git" && plan?.state !== "Ready"} onClick={() => void generate()}>
          {locked ? `처리 중 · ${seconds}초` : scenario?.kind === "git" ? "2. Codex로 다이어그램 그리기" : "Codex로 다이어그램 그리기"}</button>
      </div>
      {plan && <p>{plan.state} · {plan.stageMessage} · 변경점 {plan.candidates.length}개{plan.errorMessage && ` · ${plan.errorMessage}`}</p>}
      {pending && <p>{pending.state} · {pending.progress}% · {pending.stageMessage}</p>}
      {locked && <p>여러 번의 의미 설계·검토가 수행될 수 있습니다. CLI 호출 {runtime.codex?.calls ?? 0}회 · 이전 결과는 아래에 보존됩니다.</p>}
      {scenario && <details><summary>사용하는 합성 입력 보기</summary>{scenario.prompt ? <p>{scenario.prompt}</p> : <div className="sample-source-grid"><section><h3>변경 전</h3><pre>{scenario.before}</pre></section><section><h3>변경 후</h3><pre>{scenario.after}</pre></section></div>}</details>}
    </section>
    <section className="panel"><h2>저장된 테스트 결과</h2><div className="field-row">
      <label>최근 분석 초안<select value={plan?.id ?? ""} disabled={locked} onChange={event => void restorePlan(event.target.value)}><option value="">초안 선택</option>
        {plans.map(item => <option key={item.id} value={item.id}>{new Date(item.createdAt).toLocaleString("ko-KR")} · {item.targetCommitMessage ?? "샘플"} · {item.state}</option>)}</select></label>
      {history.length > 0 && <label>생성 이력<select value={analysis?.id ?? ""} disabled={locked} onChange={event => {
        void api.getAnalysis(event.target.value).then(value => { setAnalysis(value); setNatural(null); }).catch(reason => setError(String(reason)));
      }}><option value="">이력 선택</option>{history.map(item => <option key={item.id} value={item.id}>{item.label}</option>)}</select></label>}
      {naturalHistory.length > 0 && <label>자연어 예제 이력<select value={natural?.id ?? ""} disabled={locked} onChange={event => {
        const record = naturalHistory.find(item => item.id === event.target.value);
        if (record) { setNatural(record); setAnalysis(null); setPlan(null); setPending(null); setScenarioId("natural-orders"); setActiveView(""); }
      }}><option value="">이력 선택</option>{naturalHistory.map(item => <option key={item.id} value={item.id}>{new Date(item.createdAt).toLocaleString("ko-KR")} · {item.diagram.ir.title}</option>)}</select></label>}
    </div><small>저장된 결과 열기는 Codex를 호출하지 않습니다.</small></section>
    {(analysis || natural) && <section className="panel analysis-output"><h2>샘플 다이어그램 결과</h2>
      {metadata && <p>공급자 {metadata.provider} · 샘플 v{metadata.sampleVersion} · {metadata.scenarioId} · 요청 {metadata.refinementId}</p>}
      {analysis && <AnalysisResultView analysis={analysis} activeGroup={activeGroup} setActiveGroup={setActiveGroup} activeView={activeView} setActiveView={setActiveView}
        reportError={reportError} presets={presets} regeneratingViewId={locked ? "sample-generation" : ""} onRegenerateView={regenerateView} sampleRefinements={catalog?.refinements} />}
      {natural && <><div className="diagram-type-tabs">{natural.views?.map(view => <button type="button" key={view.viewId} className={selectedNatural?.viewId === view.viewId ? "active" : ""}
        onClick={() => setActiveView(view.viewId)}>{names[view.selection.diagramType]} · {view.state}</button>)}</div>
        {selectedNatural?.errorMessage && <p className="warning">{selectedNatural.errorMessage}</p>}
        {naturalArtifact && selectedNatural && <DiagramEditor key={naturalArtifact.id} artifact={naturalArtifact} zoomable downloadName={`sample-${naturalArtifact.type}`} reportError={reportError}
          onSave={input => api.saveNaturalDiagramEdit(natural.id, selectedNatural.viewId, input)}
          onPreview={(input, signal) => api.previewNaturalDiagramEdit(natural.id, selectedNatural.viewId, input, signal)} />}</>}
    </section>}
  </main>;
}
