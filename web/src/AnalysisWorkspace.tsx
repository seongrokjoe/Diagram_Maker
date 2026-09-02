import { FormEvent, useEffect, useMemo, useState } from "react";
import { api } from "./api";
import { MermaidPreview } from "./MermaidPreview";
import { PresetPicker } from "./PresetPicker";
import type {
  AnalysisGroupSelection,
  AnalysisPlan,
  AnalysisResponse,
  DiagramPreset,
  DiagramType,
  EvidenceSnippet,
  GitCommit,
  Repository,
} from "./types";

const terminalStates = new Set(["Completed", "Partial", "Failed"]);
const diagramTypes: Array<{ value: DiagramType; label: string }> = [
  { value: "flowchart", label: "흐름 / 영향도" },
  { value: "sequence", label: "호출 시퀀스" },
  { value: "class", label: "클래스 관계" },
  { value: "state", label: "상태 전이" },
];

export function AnalysisWorkspace({ repositories, reportError }: {
  repositories: Repository[];
  reportError: (message: string) => void;
}) {
  const [repositoryId, setRepositoryId] = useState("");
  const [commits, setCommits] = useState<GitCommit[]>([]);
  const [targetRevision, setTargetRevision] = useState("");
  const [advancedBase, setAdvancedBase] = useState(false);
  const [baseRevision, setBaseRevision] = useState("");
  const [useLlmGrouping, setUseLlmGrouping] = useState(true);
  const [enableThinking, setEnableThinking] = useState(false);
  const [plan, setPlan] = useState<AnalysisPlan | null>(null);
  const [recentPlans, setRecentPlans] = useState<AnalysisPlan[]>([]);
  const [groups, setGroups] = useState<AnalysisGroupSelection[]>([]);
  const [mergeIds, setMergeIds] = useState<string[]>([]);
  const [presets, setPresets] = useState<DiagramPreset[]>([]);
  const [analysis, setAnalysis] = useState<AnalysisResponse | null>(null);
  const [activeResultGroup, setActiveResultGroup] = useState("");
  const [busy, setBusy] = useState(false);
  const [evidence, setEvidence] = useState<Record<string, EvidenceSnippet>>({});

  useEffect(() => {
    void Promise.all([api.listAnalysisPlans(), api.listPresets()])
      .then(([plans, presetList]) => { setRecentPlans(plans); setPresets(presetList); })
      .catch((reason: unknown) => reportError(messageOf(reason, "초안 목록을 불러오지 못했습니다.")));
  }, [reportError]);

  useEffect(() => {
    if (!repositoryId) { setCommits([]); setTargetRevision(""); return; }
    void api.listCommits(repositoryId)
      .then((items) => { setCommits(items); setTargetRevision(items[0]?.sha ?? ""); })
      .catch((reason: unknown) => reportError(messageOf(reason, "커밋 목록을 불러오지 못했습니다.")));
  }, [repositoryId, reportError]);

  useEffect(() => {
    if (!plan || ["Ready", "Failed", "Expired"].includes(plan.state)) return;
    const timer = window.setTimeout(() => {
      void api.getAnalysisPlan(plan.id).then(setPlan)
        .catch((reason: unknown) => reportError(messageOf(reason, "사전 분석 상태를 불러오지 못했습니다.")));
    }, 1000);
    return () => window.clearTimeout(timer);
  }, [plan, reportError]);

  useEffect(() => {
    if (plan?.state === "Ready") setGroups(plan.selections);
  }, [plan?.id, plan?.state, plan?.revision]);

  useEffect(() => {
    if (!analysis || terminalStates.has(analysis.state)) return;
    const timer = window.setTimeout(() => {
      void api.getAnalysis(analysis.id).then(setAnalysis)
        .catch((reason: unknown) => reportError(messageOf(reason, "다이어그램 생성 상태를 불러오지 못했습니다.")));
    }, 900);
    return () => window.clearTimeout(timer);
  }, [analysis, reportError]);

  useEffect(() => {
    const first = analysis?.result?.diagramGroups?.find((group) => group.diagram);
    if (first) setActiveResultGroup(first.groupId);
  }, [analysis?.id, analysis?.result]);

  const assignment = useMemo(() => {
    const result = new Map<string, string>();
    groups.forEach((group) => group.changeIds.forEach((changeId) => result.set(changeId, group.id)));
    return result;
  }, [groups]);

  async function createPlan(event: FormEvent) {
    event.preventDefault();
    if (!repositoryId || !targetRevision) return;
    setBusy(true);
    reportError("");
    setAnalysis(null);
    try {
      const created = await api.createAnalysisPlan({
        repositoryId,
        targetRevision,
        baseRevision: advancedBase && baseRevision.trim() ? baseRevision.trim() : undefined,
        useLlmGrouping,
        enableThinking: useLlmGrouping && enableThinking,
      });
      setPlan(created);
      setRecentPlans((current) => [created, ...current.filter((item) => item.id !== created.id)]);
    } catch (reason) {
      reportError(messageOf(reason, "사전 분석을 시작하지 못했습니다."));
    } finally { setBusy(false); }
  }

  function toggleCandidate(changeId: string, checked: boolean) {
    setGroups((current) => {
      if (!checked) return current.map((group) => ({ ...group, changeIds: group.changeIds.filter((id) => id !== changeId) }));
      if (current.length === 0) {
        return [{ id: crypto.randomUUID(), title: "선택한 변경점", changeIds: [changeId], diagramType: "flowchart", presetId: defaultPreset("flowchart", presets) }];
      }
      if (current.some((group) => group.changeIds.includes(changeId))) return current;
      return current.map((group, index) => index === 0 ? { ...group, changeIds: [...group.changeIds, changeId] } : group);
    });
  }

  function moveCandidate(changeId: string, destinationId: string) {
    setGroups((current) => current.map((group) => ({
      ...group,
      changeIds: group.id === destinationId
        ? [...group.changeIds.filter((id) => id !== changeId), changeId]
        : group.changeIds.filter((id) => id !== changeId),
    })));
  }

  function updateGroup(id: string, patch: Partial<AnalysisGroupSelection>) {
    setGroups((current) => current.map((group) => group.id === id ? { ...group, ...patch } : group));
  }

  function addGroup() {
    setGroups((current) => [...current, {
      id: crypto.randomUUID(),
      title: `새 그룹 ${current.length + 1}`,
      changeIds: [],
      diagramType: "flowchart",
      presetId: defaultPreset("flowchart", presets),
    }]);
  }

  function mergeSelectedGroups() {
    if (mergeIds.length < 2) return;
    setGroups((current) => {
      const merging = current.filter((group) => mergeIds.includes(group.id));
      const first = merging[0];
      if (!first) return current;
      const merged = { ...first, title: `${first.title} 외 ${merging.length - 1}개`, changeIds: [...new Set(merging.flatMap((group) => group.changeIds))] };
      return current.map((group) => group.id === first.id ? merged : group).filter((group) => !mergeIds.includes(group.id) || group.id === first.id);
    });
    setMergeIds([]);
  }

  async function saveAndGenerate() {
    if (!plan) return;
    const validGroups = groups.filter((group) => group.changeIds.length > 0);
    if (validGroups.length === 0) { reportError("다이어그램에 포함할 변경점을 하나 이상 선택하세요."); return; }
    setBusy(true);
    reportError("");
    try {
      const saved = await api.saveAnalysisPlan(plan.id, plan.revision, validGroups);
      setPlan(saved);
      setGroups(saved.selections);
      setAnalysis(await api.generateAnalysisPlan(saved.id, saved.revision));
    } catch (reason) {
      reportError(messageOf(reason, "선택 저장 또는 다이어그램 생성에 실패했습니다."));
    } finally { setBusy(false); }
  }

  function restorePlan(id: string) {
    void api.getAnalysisPlan(id).then((value) => { setPlan(value); setAnalysis(null); })
      .catch((reason: unknown) => reportError(messageOf(reason, "초안을 복원하지 못했습니다.")));
  }

  function loadEvidence(changeId: string) {
    if (!plan || evidence[changeId]) return;
    void api.getAnalysisPlanEvidence(plan.id, changeId)
      .then((value) => setEvidence((current) => ({ ...current, [changeId]: value })))
      .catch((reason: unknown) => reportError(messageOf(reason, "소스 근거를 불러오지 못했습니다.")));
  }

  return (
    <section className="analysis-workspace">
      <ol className="stepper" aria-label="Git 변경 분석 단계">
        <li className={!plan ? "active" : "done"}>1. 커밋 선택</li>
        <li className={plan?.state === "Ready" && !analysis ? "active" : plan ? "done" : ""}>2. 변경점 선택·그룹화</li>
        <li className={analysis ? "active" : ""}>3. 다이어그램 확인</li>
      </ol>

      {!plan && <div className="workspace-grid">
        <form className="panel controls" onSubmit={createPlan}>
          <p className="section-label">STEP 1 · IMMUTABLE REVISION</p>
          <h2>분석할 커밋 선택</h2>
          <label>저장소
            <select required value={repositoryId} onChange={(event) => setRepositoryId(event.target.value)}>
              <option value="">저장소 선택</option>
              {repositories.map((repository) => <option key={repository.id} value={repository.id}>{repository.name}</option>)}
            </select>
          </label>
          <label>Target 커밋
            <select required value={targetRevision} onChange={(event) => setTargetRevision(event.target.value)}>
              <option value="">커밋 선택</option>
              {commits.map((commit) => <option key={commit.sha} value={commit.sha}>{commit.sha.slice(0, 10)} · {oneLine(commit.message)}</option>)}
            </select>
          </label>
          <label className="checkbox"><input type="checkbox" checked={advancedBase} onChange={(event) => setAdvancedBase(event.target.checked)} /> Base 커밋 직접 지정</label>
          {advancedBase && <label>Base revision<input value={baseRevision} onChange={(event) => setBaseRevision(event.target.value)} placeholder="커밋 SHA 또는 브랜치" /></label>}
          {!advancedBase && <p className="help">Base는 Target의 첫 번째 부모 커밋으로 자동 선택합니다.</p>}
          <label className="checkbox"><input type="checkbox" checked={useLlmGrouping} onChange={(event) => setUseLlmGrouping(event.target.checked)} /> 내부 LLM의 그룹 제안 사용</label>
          <label className="checkbox"><input type="checkbox" disabled={!useLlmGrouping} checked={enableThinking} onChange={(event) => setEnableThinking(event.target.checked)} /> 그룹 제안에 Thinking 사용</label>
          <button className="primary" disabled={busy || !targetRevision}>변경점 사전 분석</button>
        </form>
        <section className="panel">
          <p className="section-label">30-DAY DRAFTS</p><h2>최근 분석 초안</h2>
          <div className="revision-list">
            {recentPlans.map((item) => <button type="button" key={item.id} onClick={() => restorePlan(item.id)}>
              <span>{item.targetSha?.slice(0, 10) ?? item.request.targetRevision}</span>
              <small>{item.state} · {new Date(item.updatedAt).toLocaleString("ko-KR")}</small>
            </button>)}
          </div>
          {recentPlans.length === 0 && <EmptyState text="저장된 분석 초안이 없습니다." />}
        </section>
      </div>}

      {plan && plan.state !== "Ready" && !analysis && <section className="panel">
        <div className="panel-heading"><div><p className="section-label">PRE-ANALYSIS</p><h2>변경 구조를 분석하고 있습니다</h2></div><span className={`status-chip ${plan.state.toLowerCase()}`}>{plan.state}</span></div>
        <Progress value={plan.progress} label={plan.stageMessage} />
        {plan.errorMessage && <div className="analysis-error"><strong>{plan.errorCode}</strong><p>{plan.errorMessage}</p></div>}
        {plan.state === "Failed" && <button className="secondary" type="button" onClick={() => setPlan(null)}>다시 선택</button>}
      </section>}

      {plan?.state === "Ready" && !analysis && <section className="plan-editor">
        <div className="panel plan-toolbar">
          <div><p className="section-label">STEP 2 · EVIDENCE-BOUND GROUPS</p><h2>표시할 변경점과 그룹 확인</h2></div>
          <div className="button-row"><button type="button" className="secondary" onClick={() => setPlan(null)}>커밋 다시 선택</button><button type="button" className="secondary" onClick={addGroup}>그룹 추가</button><button type="button" className="primary" disabled={busy} onClick={() => void saveAndGenerate()}>선택대로 다이어그램 생성</button></div>
        </div>
        {plan.warnings.map((warning) => <p className="warning" key={warning}>{warning}</p>)}
        <div className="selection-layout">
          <section className="panel candidate-panel">
            <h3>변경 심볼 ({plan.candidates.length})</h3>
            <p className="help">체크 해제하면 출력에서 제외됩니다. 드롭다운으로 그룹만 바꿀 수 있습니다.</p>
            <div className="candidate-list">
              {plan.candidates.map((candidate) => {
                const groupId = assignment.get(candidate.id);
                return <article className="candidate-row" key={candidate.id}>
                  <label className="checkbox"><input type="checkbox" checked={Boolean(groupId)} onChange={(event) => toggleCandidate(candidate.id, event.target.checked)} /><span><strong>{candidate.qualifiedName}</strong><small>{candidate.changeType} · {candidate.filePath}:{candidate.startLine} · caller {candidate.callerCount} / callee {candidate.calleeCount}</small></span></label>
                  <div className="candidate-actions">{groupId && <select aria-label={`${candidate.qualifiedName} 그룹`} value={groupId} onChange={(event) => moveCandidate(candidate.id, event.target.value)}>{groups.map((group) => <option key={group.id} value={group.id}>{group.title}</option>)}</select>}{!evidence[candidate.id] && <button type="button" className="text-button" onClick={() => loadEvidence(candidate.id)}>소스 근거 보기</button>}</div>
                  {evidence[candidate.id] && <details open className="evidence-snippet"><summary>{evidence[candidate.id].filePath}:{evidence[candidate.id].startLine}-{evidence[candidate.id].endLine}</summary><pre>{evidence[candidate.id].content}</pre></details>}
                </article>;
              })}
            </div>
          </section>
          <section className="group-stack">
            <div className="merge-toolbar"><button type="button" className="secondary" disabled={mergeIds.length < 2} onClick={mergeSelectedGroups}>선택 그룹 병합</button></div>
            {groups.map((group) => {
              const typePresets = presets.filter((preset) => preset.type === group.diagramType);
              return <article className="panel group-card" key={group.id}>
                <div className="group-heading"><label className="checkbox"><input type="checkbox" checked={mergeIds.includes(group.id)} onChange={(event) => setMergeIds((current) => event.target.checked ? [...current, group.id] : current.filter((id) => id !== group.id))} /> 병합 선택</label><button type="button" className="text-button" onClick={() => setGroups((current) => current.filter((item) => item.id !== group.id))}>그룹 삭제</button></div>
                <label>그룹 이름<input value={group.title} maxLength={120} onChange={(event) => updateGroup(group.id, { title: event.target.value })} /></label>
                <div className="field-row"><label>다이어그램 형식<select value={group.diagramType} onChange={(event) => { const diagramType = event.target.value as DiagramType; updateGroup(group.id, { diagramType, presetId: defaultPreset(diagramType, presets) }); }}>{diagramTypes.map((type) => <option key={type.value} value={type.value}>{type.label}</option>)}</select></label><span className="count-chip">변경점 {group.changeIds.length}개</span></div>
                <PresetPicker presets={typePresets} selectedId={group.presetId} onSelect={(preset) => updateGroup(group.id, { presetId: preset.id, overrides: undefined })} />
                <details><summary>고급 옵션</summary><div className="field-row"><label>방향<select value={group.overrides?.direction ?? ""} onChange={(event) => updateGroup(group.id, { overrides: { ...group.overrides, direction: (event.target.value || undefined) as "LR" | "TB" | undefined } })}><option value="">샘플 기본값</option><option value="LR">가로 (LR)</option><option value="TB">세로 (TB)</option></select></label><label>Caller 깊이<select value={group.overrides?.callerDepth ?? ""} onChange={(event) => updateGroup(group.id, { overrides: { ...group.overrides, callerDepth: event.target.value === "" ? undefined : Number(event.target.value) } })}><option value="">샘플 기본값</option>{[0,1,2,3].map((value) => <option key={value}>{value}</option>)}</select></label><label>Callee 깊이<select value={group.overrides?.calleeDepth ?? ""} onChange={(event) => updateGroup(group.id, { overrides: { ...group.overrides, calleeDepth: event.target.value === "" ? undefined : Number(event.target.value) } })}><option value="">샘플 기본값</option>{[0,1,2,3].map((value) => <option key={value}>{value}</option>)}</select></label></div></details>
              </article>;
            })}
          </section>
        </div>
      </section>}

      {analysis && <section className="panel analysis-output">
        <div className="panel-heading"><div><p className="section-label">STEP 3 · RESULT</p><h2>선택된 변경점 다이어그램</h2></div><span className={`status-chip ${analysis.state.toLowerCase()}`}>{analysis.state}</span></div>
        {!analysis.result && <><Progress value={analysis.progress} label={analysis.stageMessage} />{analysis.errorMessage && <div className="analysis-error"><strong>{analysis.errorCode}</strong><p>{analysis.errorMessage}</p><code>{analysis.id}</code></div>}</>}
        {analysis.result && <AnalysisResultView analysis={analysis} activeGroup={activeResultGroup} setActiveGroup={setActiveResultGroup} />}
        {terminalStates.has(analysis.state) && <button type="button" className="secondary" onClick={() => { setAnalysis(null); if (plan) setGroups(plan.selections); }}>선택 단계로 돌아가기</button>}
      </section>}
    </section>
  );
}

function AnalysisResultView({ analysis, activeGroup, setActiveGroup }: { analysis: AnalysisResponse; activeGroup: string; setActiveGroup: (id: string) => void }) {
  if (!analysis.result) return null;
  const groups = analysis.result.diagramGroups ?? [];
  const selected = groups.find((group) => group.groupId === activeGroup) ?? groups.find((group) => group.diagram);
  return <div className="analysis-result">
    <div className="summary-card"><strong>전체 요약</strong><p>{analysis.result.narrative.summary}</p></div>
    {analysis.result.narrative.warnings.map((warning) => <p className="warning" key={warning}>{warning}</p>)}
    {groups.length > 0 && <div className="diagram-type-tabs">{groups.map((group) => <button type="button" className={selected?.groupId === group.groupId ? "active" : ""} key={group.groupId} onClick={() => setActiveGroup(group.groupId)}>{group.title}</button>)}</div>}
    {selected && <article className="diagram-result"><div className="summary-card"><strong>{selected.title}</strong><p>{selected.narrative.summary}</p><small>{selected.narrative.intent}</small></div>{selected.diagram ? <><MermaidPreview source={selected.diagram.mermaidDsl} downloadName={`git-${selected.diagram.type}-${analysis.targetSha?.slice(0, 8) ?? "result"}`} /><details><summary>Mermaid DSL 확인</summary><pre>{selected.diagram.mermaidDsl}</pre></details></> : <EmptyState text={selected.warnings[0] ?? "이 그룹의 다이어그램을 생성하지 못했습니다."} />}</article>}
    {groups.length === 0 && analysis.result.diagrams[0] && <MermaidPreview source={analysis.result.diagrams[0].mermaidDsl} downloadName="git-analysis" />}
    <h3>변경 파일</h3><ul className="file-list">{analysis.result.changedFiles.map((file) => <li key={`${file.path}-${file.changeKind}`}><span className={`change ${file.changeKind.toLowerCase()}`}>{file.changeKind}</span><code>{file.previousPath ? `${file.previousPath} → ${file.path}` : file.path}</code></li>)}</ul>
  </div>;
}

function Progress({ value, label }: { value: number; label: string }) {
  return <div className="progress-block"><div className="progress-meta"><span>{label}</span><strong>{value}%</strong></div><div className="progress-track"><span style={{ width: `${value}%` }} /></div></div>;
}

function EmptyState({ text }: { text: string }) { return <div className="empty-state"><p>{text}</p></div>; }
function oneLine(value: string) { return value.split(/\r?\n/, 1)[0]; }
function messageOf(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback; }
function defaultPreset(type: DiagramType, presets: DiagramPreset[]) { return presets.find((preset) => preset.type === type && preset.detailLevel === "balanced")?.id ?? presets.find((preset) => preset.type === type)?.id ?? "balanced"; }
