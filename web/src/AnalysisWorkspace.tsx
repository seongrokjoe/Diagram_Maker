import { FormEvent, useEffect, useMemo, useState } from "react";
import { api } from "./api";
import { CommitPicker } from "./CommitPicker";
import { DiagramEditor } from "./DiagramEditor";
import { MermaidPreview } from "./MermaidPreview";
import { PresetPicker } from "./PresetPicker";
import { elapsedLabel, useElapsedSeconds } from "./useElapsedSeconds";
import type {
  AnalysisDiagramGroup,
  AnalysisDiagramView,
  AnalysisGroupSelection,
  AnalysisHistorySummary,
  AnalysisPlan,
  AnalysisResponse,
  DiagramPreset,
  DiagramType,
  DiagramViewSelection,
  EvidenceSnippet,
  GitCommit,
  Repository,
} from "./types";

const terminalStates = new Set(["Completed", "Partial", "Failed"]);
const diagramTypes: Array<{ value: DiagramType; label: string }> = [
  { value: "flowchart", label: "흐름 / 영향도" },
  { value: "sequence", label: "호출 시퀀스" },
  { value: "class", label: "클래스 관계" },
  { value: "code-relation", label: "코드 관계도" },
  { value: "state", label: "상태 전이" },
];

export function AnalysisWorkspace({ repositories, reportError }: {
  repositories: Repository[];
  reportError: (message: string) => void;
}) {
  const [repositoryId, setRepositoryId] = useState("");
  const [targetRevision, setTargetRevision] = useState("");
  const [targetCommit, setTargetCommit] = useState<GitCommit | null>(null);
  const [advancedBase, setAdvancedBase] = useState(false);
  const [baseRevision, setBaseRevision] = useState("");
  const [baseCommit, setBaseCommit] = useState<GitCommit | null>(null);
  const [useLlmGrouping, setUseLlmGrouping] = useState(true);
  const [enableThinking, setEnableThinking] = useState(false);
  const [plan, setPlan] = useState<AnalysisPlan | null>(null);
  const [recentPlans, setRecentPlans] = useState<AnalysisPlan[]>([]);
  const [groups, setGroups] = useState<AnalysisGroupSelection[]>([]);
  const [mergeIds, setMergeIds] = useState<string[]>([]);
  const [presets, setPresets] = useState<DiagramPreset[]>([]);
  const [analysis, setAnalysis] = useState<AnalysisResponse | null>(null);
  const [analysisHistory, setAnalysisHistory] = useState<AnalysisHistorySummary[]>([]);
  const [currentStep, setCurrentStep] = useState<1 | 2 | 3>(1);
  const [sourceAnalysis, setSourceAnalysis] = useState<AnalysisResponse | null>(null);
  const [activeResultGroup, setActiveResultGroup] = useState("");
  const [activeResultView, setActiveResultView] = useState("");
  const [busyAction, setBusyAction] = useState("");
  const [evidence, setEvidence] = useState<Record<string, EvidenceSnippet>>({});
  const busy = Boolean(busyAction);
  const busySeconds = useElapsedSeconds(busy);
  const planRunning = Boolean(plan && !["Ready", "Failed", "Expired"].includes(plan.state));
  const analysisRunning = Boolean(analysis && !terminalStates.has(analysis.state));
  const planSeconds = useElapsedSeconds(planRunning);
  const analysisSeconds = useElapsedSeconds(analysisRunning);

  useEffect(() => {
    void Promise.all([api.listAnalysisPlans(), api.listPresets()])
      .then(([plans, presetList]) => { setRecentPlans(plans); setPresets(presetList); })
      .catch((reason: unknown) => reportError(messageOf(reason, "초안 목록을 불러오지 못했습니다.")));
  }, [reportError]);

  useEffect(() => {
    if (!plan || ["Ready", "Failed", "Expired"].includes(plan.state)) return;
    const timer = window.setTimeout(() => {
      void api.getAnalysisPlan(plan.id).then(setPlan)
        .catch((reason: unknown) => reportError(messageOf(reason, "사전 분석 상태를 불러오지 못했습니다.")));
    }, 1000);
    return () => window.clearTimeout(timer);
  }, [plan, reportError]);

  useEffect(() => {
    if (plan?.state === "Ready") setGroups(plan.selections.map(normalizeGroup));
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
    if (!plan || !analysis || !terminalStates.has(analysis.state)) return;
    void api.listAnalysisPlanAnalyses(plan.id).then(setAnalysisHistory)
      .catch((reason: unknown) => reportError(messageOf(reason, "생성 이력을 불러오지 못했습니다.")));
  }, [analysis?.id, analysis?.state, plan?.id, reportError]);

  useEffect(() => {
    const resultGroups = analysis?.result?.diagramGroups ?? [];
    const first = resultGroups.find((group) => effectiveResultViews(group).some((view) => view.diagram || view.comparisonBaseDiagram));
    if (!first) return;
    const currentGroup = resultGroups.find((group) => group.groupId === activeResultGroup &&
      effectiveResultViews(group).some((view) => view.diagram || view.comparisonBaseDiagram)) ?? first;
    const currentViews = effectiveResultViews(currentGroup);
    const firstVisibleView = currentViews.find((view) => view.diagram || view.comparisonBaseDiagram) ?? currentViews[0];
    if (activeResultGroup !== currentGroup.groupId) setActiveResultGroup(currentGroup.groupId);
    if (!currentViews.some((view) => view.viewId === activeResultView)) setActiveResultView(firstVisibleView?.viewId ?? "");
  }, [activeResultGroup, activeResultView, analysis?.id, analysis?.result]);

  useEffect(() => {
    if (analysis?.result && terminalStates.has(analysis.state)) setSourceAnalysis(analysis);
  }, [analysis]);

  const assignment = useMemo(() => {
    const result = new Map<string, string>();
    groups.forEach((group) => group.changeIds.forEach((changeId) => result.set(changeId, group.id)));
    return result;
  }, [groups]);
  const selectedRepository = repositories.find((repository) => repository.id === repositoryId);
  const revisionsMatch = advancedBase && Boolean(baseRevision) && baseRevision === targetRevision;

  async function createPlan(event: FormEvent) {
    event.preventDefault();
    if (!repositoryId || !targetRevision) return;
    setBusyAction("plan-create");
    reportError("");
    setAnalysis(null);
    setSourceAnalysis(null);
    try {
      const created = await api.createAnalysisPlan({
        repositoryId,
        targetRevision,
        baseRevision: advancedBase && baseRevision.trim() ? baseRevision.trim() : undefined,
        useLlmGrouping,
        enableThinking: useLlmGrouping && enableThinking,
      });
      setPlan(created);
      setCurrentStep(2);
      setAnalysisHistory([]);
      setRecentPlans((current) => [created, ...current.filter((item) => item.id !== created.id)]);
    } catch (reason) {
      reportError(messageOf(reason, "사전 분석을 시작하지 못했습니다."));
    } finally { setBusyAction(""); }
  }

  function toggleCandidate(changeId: string, checked: boolean) {
    setGroups((current) => {
      if (!checked) return current.map((group) => ({ ...group, changeIds: group.changeIds.filter((id) => id !== changeId) }));
      if (current.length === 0) {
        const id = crypto.randomUUID();
        const view = createView("flowchart", presets);
        return [{ id, title: "선택한 변경점", changeIds: [changeId], diagramType: view.diagramType, presetId: view.presetId, views: [view] }];
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

  function updateGroupView(groupId: string, viewId: string, patch: Partial<DiagramViewSelection>) {
    setGroups((current) => current.map((group) => {
      if (group.id !== groupId) return group;
      const views = effectiveGroupViews(group).map((view) => view.id === viewId ? { ...view, ...patch } : view);
      const primary = views[0];
      return { ...group, views, diagramType: primary.diagramType, presetId: primary.presetId, overrides: primary.overrides };
    }));
  }

  function addGroupView(groupId: string) {
    setGroups((current) => current.map((group) => {
      if (group.id !== groupId) return group;
      const views = effectiveGroupViews(group);
      const type = diagramTypes.find((item) => !views.some((view) => view.diagramType === item.value))?.value;
      if (!type || views.length >= 4) return group;
      return { ...group, views: [...views, createView(type, presets)] };
    }));
  }

  function removeGroupView(groupId: string, viewId: string) {
    setGroups((current) => current.map((group) => {
      if (group.id !== groupId) return group;
      const views = effectiveGroupViews(group).filter((view) => view.id !== viewId);
      const primary = views[0];
      return { ...group, views, diagramType: primary.diagramType, presetId: primary.presetId, overrides: primary.overrides };
    }));
  }

  function addGroup() {
    setGroups((current) => {
      const view = createView("flowchart", presets);
      return [...current, {
        id: crypto.randomUUID(), title: `새 그룹 ${current.length + 1}`, changeIds: [],
        diagramType: view.diagramType, presetId: view.presetId, views: [view],
      }];
    });
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
    setBusyAction("diagram-generate");
    reportError("");
    try {
      const saved = await api.saveAnalysisPlan(plan.id, plan.revision, validGroups);
      setPlan(saved);
      setGroups(saved.selections.map(normalizeGroup));
      const requestedViewIds = changedViewIds(validGroups, sourceAnalysis);
      setAnalysis(await api.generateAnalysisPlan(saved.id, saved.revision, sourceAnalysis?.id, requestedViewIds));
      setCurrentStep(3);
    } catch (reason) {
      reportError(messageOf(reason, "선택 저장 또는 다이어그램 생성에 실패했습니다."));
    } finally { setBusyAction(""); }
  }

  async function regenerateView(groupId: string, selection: DiagramViewSelection) {
    if (!plan || !analysis?.result) return;
    if (!groups.some((group) => group.id === groupId && effectiveGroupViews(group).some((view) => view.id === selection.id))) {
      reportError("이 생성 이력의 옵션은 현재 분석 계획과 달라 바로 수정할 수 없습니다. 최신 결과를 선택하세요.");
      return;
    }
    const nextGroups = groups.map((group) => group.id === groupId
      ? normalizeGroup({ ...group, views: effectiveGroupViews(group).map((view) => view.id === selection.id ? selection : view) })
      : group);
    setBusyAction(`view-regenerate-${selection.id}`);
    reportError("");
    try {
      const saved = await api.saveAnalysisPlan(plan.id, plan.revision, nextGroups);
      const normalized = saved.selections.map(normalizeGroup);
      setPlan(saved);
      setGroups(normalized);
      const source = analysis.result ? analysis : sourceAnalysis;
      if (source?.result) setSourceAnalysis(source);
      setAnalysis(await api.generateAnalysisPlan(saved.id, saved.revision, source?.id, [selection.id]));
      setActiveResultGroup(groupId);
      setActiveResultView(selection.id);
    } catch (reason) {
      reportError(messageOf(reason, "선택한 다이어그램을 다시 생성하지 못했습니다."));
    } finally { setBusyAction(""); }
  }

  function restorePlan(id: string) {
    setBusyAction("plan-restore");
    void Promise.all([api.getAnalysisPlan(id), api.listAnalysisPlanAnalyses(id)])
      .then(async ([value, history]) => {
        setPlan(value);
        setAnalysisHistory(history);
        setRepositoryId(value.request.repositoryId);
        setTargetRevision(value.targetSha ?? value.request.targetRevision);
        setAdvancedBase(Boolean(value.baseSha ?? value.request.baseRevision));
        setBaseRevision(value.baseSha ?? value.request.baseRevision ?? "");
        setUseLlmGrouping(value.request.useLlmGrouping);
        setEnableThinking(value.request.enableThinking);
        const repository = repositories.find((item) => item.id === value.request.repositoryId);
        const target = value.targetSha ?? value.request.targetRevision;
        const base = value.baseSha ?? value.request.baseRevision;
        const [resolvedTarget, resolvedBase] = await Promise.all([
          api.resolveCommit(value.request.repositoryId, target).catch(() => null),
          base ? api.resolveCommit(value.request.repositoryId, base).catch(() => null) : Promise.resolve(null),
        ]);
        setTargetCommit(resolvedTarget);
        setBaseCommit(resolvedBase);
        const latest = history.find((item) => item.hasResult && (item.state === "Completed" || item.state === "Partial"));
        if (latest) {
          const result = await api.getAnalysis(latest.id);
          setAnalysis(result);
          setSourceAnalysis(result);
          setCurrentStep(3);
        } else {
          setAnalysis(null);
          setSourceAnalysis(null);
          setCurrentStep(2);
        }
        if (!repository) reportError("복원한 초안의 저장소가 현재 접근 가능한 목록에 없습니다.");
      })
      .catch((reason: unknown) => reportError(messageOf(reason, "초안을 복원하지 못했습니다.")))
      .finally(() => setBusyAction(""));
  }

  function loadAnalysis(id: string) {
    void api.getAnalysis(id).then((value) => {
      setAnalysis(value);
      setSourceAnalysis(value.result ? value : sourceAnalysis);
      setCurrentStep(3);
    }).catch((reason: unknown) => reportError(messageOf(reason, "생성 결과를 불러오지 못했습니다.")));
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
        <li className={currentStep === 1 ? "active" : plan ? "done" : ""}><button type="button" onClick={() => setCurrentStep(1)}>1. 커밋 선택</button></li>
        <li className={currentStep === 2 ? "active" : currentStep === 3 ? "done" : ""}><button type="button" disabled={!plan} onClick={() => setCurrentStep(2)}>2. 변경점 선택·그룹화</button></li>
        <li className={currentStep === 3 ? "active" : ""}><button type="button" disabled={!analysis && analysisHistory.length === 0} onClick={() => setCurrentStep(3)}>3. 다이어그램 확인</button></li>
      </ol>

      {currentStep === 1 && <div className="workspace-grid">
        <form className="panel controls" onSubmit={createPlan}>
          <p className="section-label">STEP 1 · IMMUTABLE REVISION</p>
          <h2>분석할 커밋 선택</h2>
          <label>저장소
            <select required value={repositoryId} onChange={(event) => {
              setRepositoryId(event.target.value);
              setTargetRevision("");
              setTargetCommit(null);
              setBaseRevision("");
              setBaseCommit(null);
            }}>
              <option value="">저장소 선택</option>
              {repositories.map((repository) => <option key={repository.id} value={repository.id}>{repository.name}</option>)}
            </select>
          </label>
          <CommitPicker
            repositoryId={repositoryId}
            defaultBranch={selectedRepository?.defaultBranch ?? ""}
            label="Target 커밋"
            value={targetRevision}
            autoSelectFirst
            onSelect={(commit) => { setTargetCommit(commit); setTargetRevision(commit?.sha ?? ""); }}
          />
          <label className="checkbox"><input type="checkbox" checked={advancedBase} onChange={(event) => setAdvancedBase(event.target.checked)} /> Base 커밋 직접 지정</label>
          {advancedBase && <CommitPicker
            repositoryId={repositoryId}
            defaultBranch={selectedRepository?.defaultBranch ?? ""}
            label="Base 커밋"
            value={baseRevision}
            onSelect={(commit) => { setBaseCommit(commit); setBaseRevision(commit?.sha ?? ""); }}
          />}
          {!advancedBase && <p className="help">Base는 Target의 첫 번째 부모 커밋으로 자동 선택합니다.{targetCommit && (targetCommit.parentShas[0] ? ` (${targetCommit.parentShas[0].slice(0, 12)})` : " 선택한 Target은 부모가 없는 최초 커밋입니다.")}</p>}
          {revisionsMatch && <p className="error-text">Base와 Target은 서로 다른 커밋이어야 합니다.</p>}
          <label className="checkbox"><input type="checkbox" checked={useLlmGrouping} onChange={(event) => setUseLlmGrouping(event.target.checked)} /> 내부 LLM의 그룹 제안 사용</label>
          <label className="checkbox"><input type="checkbox" disabled={!useLlmGrouping} checked={enableThinking} onChange={(event) => setEnableThinking(event.target.checked)} /> 그룹 제안에 Thinking 사용</label>
          <button className="primary" disabled={busy || !targetRevision || (advancedBase && (!baseCommit || revisionsMatch))}>{elapsedLabel("변경점 사전 분석", busyAction === "plan-create", busySeconds)}</button>
        </form>
        <section className="panel">
          <p className="section-label">30-DAY DRAFTS</p><h2>최근 분석 초안</h2>
          <div className="revision-list">
            {recentPlans.map((item) => <button type="button" key={item.id} onClick={() => restorePlan(item.id)}>
              <span>{item.targetSha?.slice(0, 10) ?? item.request.targetRevision} · {item.targetCommitMessage ?? "커밋 메시지 확인 전"}</span>
              <small>{item.state} · {new Date(item.updatedAt).toLocaleString("ko-KR")}</small>
            </button>)}
          </div>
          {recentPlans.length === 0 && <EmptyState text="저장된 분석 초안이 없습니다." />}
        </section>
      </div>}

      {currentStep === 2 && plan && plan.state !== "Ready" && <section className="panel">
        <div className="panel-heading"><div><p className="section-label">PRE-ANALYSIS</p><h2>변경 구조를 분석하고 있습니다</h2></div><span className={`status-chip ${plan.state.toLowerCase()}`}>{plan.state}</span></div>
        <Progress value={plan.progress} label={planRunning ? `${plan.stageMessage} · ${planSeconds}초 경과` : plan.stageMessage} />
        {planRunning && <button type="button" className="primary running-action" disabled>{elapsedLabel("변경점 사전 분석 중", true, planSeconds)}</button>}
        {plan.errorMessage && <div className="analysis-error"><strong>{plan.errorCode}</strong><p>{plan.errorMessage}</p></div>}
        {plan.state === "Failed" && <button className="secondary" type="button" onClick={() => setPlan(null)}>다시 선택</button>}
      </section>}

      {currentStep === 2 && plan?.state === "Ready" && <section className="plan-editor">
        <div className="panel plan-toolbar">
          <div><p className="section-label">STEP 2 · EVIDENCE-BOUND GROUPS</p><h2>표시할 변경점과 그룹 확인</h2></div>
          <div className="button-row"><button type="button" className="secondary" onClick={() => setCurrentStep(1)}>커밋 다시 선택</button><button type="button" className="secondary" onClick={addGroup}>그룹 추가</button><button type="button" className="primary" disabled={busy} onClick={() => void saveAndGenerate()}>{elapsedLabel("선택대로 다이어그램 생성", busyAction === "diagram-generate", busySeconds)}</button></div>
        </div>
        {plan.warnings.filter((warning) => !isCppDiagnostic(warning)).map((warning) => <p className="warning" key={warning}>{warning}</p>)}
        {((plan.notices?.length ?? 0) > 0 || plan.warnings.some(isCppDiagnostic)) && <details className="panel diagnostic-panel">
          <summary>인덱싱·C++ 해석 진단 {(plan.notices?.length ?? 0) + plan.warnings.filter(isCppDiagnostic).length}건</summary>
          <p className="help">다이어그램에서 제외되거나 축약된 항목입니다. 필요한 경우에만 펼쳐 확인하세요.</p>
          <ul>{plan.notices?.map((notice) => <li key={`${notice.code}-${notice.message}`}><strong>{notice.code}</strong> {notice.message}</li>)}{plan.warnings.filter(isCppDiagnostic).map((warning) => <li key={warning}>{warning}</li>)}</ul>
        </details>}
        {plan.exclusions && plan.exclusions.totalCount > 0 && <details className="panel exclusion-panel">
          <summary>제외된 호출 {plan.exclusions.totalCount.toLocaleString("ko-KR")}건 · {plan.exclusions.fileCount.toLocaleString("ko-KR")}개 파일</summary>
          <p className="help">대상을 하나로 확정할 수 없어 관계에서 제외했습니다. 이 목록은 다이어그램에 포함되지 않습니다.</p>
          {[...new Set(plan.exclusions!.calls.map((call) => call.filePath))].map((filePath) => <details className="exclusion-file" key={filePath}>
            <summary>{filePath} ({plan.exclusions!.calls.filter((call) => call.filePath === filePath).length})</summary>
            <ul>{plan.exclusions!.calls.filter((call) => call.filePath === filePath).map((call, index) => <li key={`${call.line}-${call.expression}-${index}`}>
              <code>{call.line}: {call.expression}</code>
              <span>{exclusionReason(call.reason)}</span>
              {call.candidateTargets.length > 0 && <small>후보: {call.candidateTargets.join(", ")}</small>}
            </li>)}</ul>
          </details>)}
          {plan.exclusions.truncated && <p className="warning">목록이 안전 표시 한도에서 잘렸습니다. 전체 제외 건수는 위 요약을 확인하세요.</p>}
        </details>}
        <div className="selection-layout">
          <section className="panel candidate-panel">
            <h3>변경 심볼 ({plan.candidates.length})</h3>
            <p className="help">체크 해제하면 출력에서 제외됩니다. 드롭다운으로 그룹만 바꿀 수 있습니다.</p>
            <div className="candidate-list">
              {plan.candidates.map((candidate) => {
                const groupId = assignment.get(candidate.id);
                return <article className="candidate-row" key={candidate.id}>
                  <label className="checkbox"><input type="checkbox" checked={Boolean(groupId)} onChange={(event) => toggleCandidate(candidate.id, event.target.checked)} /><span><strong>{candidate.qualifiedName}</strong><small>{candidate.changeType} · {candidate.filePath}:{candidate.startLine} · caller {candidate.callerCount} / callee {candidate.calleeCount}</small></span></label>
                  <div className="candidate-actions">{groupId && <label className="group-select-label"><span>그룹 선택:</span><select aria-label={`${candidate.qualifiedName} 그룹 선택`} value={groupId} onChange={(event) => moveCandidate(candidate.id, event.target.value)}>{groups.map((group) => <option key={group.id} value={group.id}>{group.title}</option>)}</select></label>}{!evidence[candidate.id] && <button type="button" className="text-button" onClick={() => loadEvidence(candidate.id)}>소스 근거 보기</button>}</div>
                  {evidence[candidate.id] && <details open className="evidence-snippet"><summary>{evidence[candidate.id].filePath}:{evidence[candidate.id].startLine}-{evidence[candidate.id].endLine}</summary><pre>{evidence[candidate.id].content}</pre></details>}
                </article>;
              })}
            </div>
          </section>
          <section className="group-stack">
            <div className="merge-toolbar"><button type="button" className="secondary" disabled={mergeIds.length < 2} onClick={mergeSelectedGroups}>선택 그룹 병합</button></div>
            {groups.map((group) => {
              const views = effectiveGroupViews(group);
              return <article className="panel group-card" key={group.id}>
                <div className="group-heading"><label className="checkbox"><input type="checkbox" checked={mergeIds.includes(group.id)} onChange={(event) => setMergeIds((current) => event.target.checked ? [...current, group.id] : current.filter((id) => id !== group.id))} /> 병합 선택</label><button type="button" className="text-button" onClick={() => setGroups((current) => current.filter((item) => item.id !== group.id))}>그룹 삭제</button></div>
                <label>그룹 이름<input value={group.title} maxLength={120} onChange={(event) => updateGroup(group.id, { title: event.target.value })} /></label>
                <div className="view-editor-heading"><span className="count-chip">변경점 {group.changeIds.length}개 · 출력 {views.length}개</span><button type="button" className="secondary" disabled={views.length >= 4 || views.length >= diagramTypes.length} onClick={() => addGroupView(group.id)}>다이어그램 형식 추가</button></div>
                {views.map((view, index) => <GroupViewEditor key={view.id} group={group} view={view} index={index} presets={presets} siblingViews={views} onChange={(patch) => updateGroupView(group.id, view.id, patch)} onRemove={() => removeGroupView(group.id, view.id)} />)}
              </article>;
            })}
          </section>
        </div>
      </section>}

      {currentStep === 3 && <section className="panel analysis-output">
        <div className="panel-heading"><div><p className="section-label">STEP 3 · RESULT</p><h2>선택된 변경점 다이어그램</h2></div>{analysis && <span className={`status-chip ${analysis.state.toLowerCase()}`}>{analysis.state}</span>}</div>
        {analysisHistory.length > 0 && <label className="history-picker">생성 이력<select value={analysis?.id ?? ""} onChange={(event) => loadAnalysis(event.target.value)}>
          {!analysis && <option value="">결과 선택</option>}
          {analysisHistory.map((item) => <option key={item.id} value={item.id}>{new Date(item.createdAt).toLocaleString("ko-KR")} · {item.state} · {item.baseSha?.slice(0, 8)} → {item.targetSha?.slice(0, 8)} · 그룹 {item.successfulGroups}/{item.totalGroups} · 출력 {item.successfulViews}/{item.totalViews}</option>)}
        </select></label>}
        {!analysis && <EmptyState text="확인할 생성 이력이 없습니다." />}
        {analysis && <>
        {!analysis.result && <><Progress value={analysis.progress} label={analysisRunning ? `${analysis.stageMessage} · ${analysisSeconds}초 경과` : analysis.stageMessage} />{analysisRunning && <button type="button" className="primary running-action" disabled>{elapsedLabel("다이어그램 생성 중", true, analysisSeconds)}</button>}{analysis.errorMessage && <div className="analysis-error"><strong>{analysis.errorCode}</strong><p>{analysis.errorMessage}</p><code>{analysis.id}</code></div>}</>}
        {analysis.result && <AnalysisResultView analysis={analysis} activeGroup={activeResultGroup} setActiveGroup={setActiveResultGroup} activeView={activeResultView} setActiveView={setActiveResultView} reportError={reportError} presets={presets}
          regeneratingViewId={busyAction.startsWith("view-regenerate-") ? busyAction.slice("view-regenerate-".length) : ""} onRegenerateView={regenerateView} />}
        {terminalStates.has(analysis.state) && <button type="button" className="secondary" onClick={() => { if (analysis.result) setSourceAnalysis(analysis); if (plan) setGroups(plan.selections.map(normalizeGroup)); setCurrentStep(2); }}>선택 단계로 돌아가기</button>}
        </>}
      </section>}
    </section>
  );
}

function GroupViewEditor({ group, view, index, presets, siblingViews, onChange, onRemove, allowRemove = true }: {
  group: AnalysisGroupSelection;
  view: DiagramViewSelection;
  index: number;
  presets: DiagramPreset[];
  siblingViews: DiagramViewSelection[];
  onChange: (patch: Partial<DiagramViewSelection>) => void;
  onRemove: () => void;
  allowRemove?: boolean;
}) {
  const typePresets = presets.filter((preset) => preset.type === view.diagramType);
  const preset = typePresets.find((item) => item.id === view.presetId);
  const effective = {
    direction: view.overrides?.direction ?? preset?.direction ?? "LR",
    detail: view.overrides?.detailLevel ?? preset?.detailLevel ?? "balanced",
    caller: view.overrides?.callerDepth ?? preset?.callerDepth ?? 1,
    callee: view.overrides?.calleeDepth ?? preset?.calleeDepth ?? 1,
    relation: view.overrides?.relationDepth ?? preset?.relationDepth ?? 1,
  };
  const customized = Boolean(view.overrides && Object.values(view.overrides).some((value) => value !== undefined));
  return <fieldset className="diagram-view-editor">
    <legend>출력 {index + 1}</legend>
    <div className="field-row"><label>다이어그램 형식<select value={view.diagramType} onChange={(event) => { const diagramType = event.target.value as DiagramType; onChange({ diagramType, presetId: defaultPreset(diagramType, presets), overrides: undefined }); }}>{diagramTypes.map((type) => <option key={type.value} value={type.value} disabled={siblingViews.some((other) => other.id !== view.id && other.diagramType === type.value)}>{type.label}</option>)}</select></label>{allowRemove && siblingViews.length > 1 && <button type="button" className="text-button danger" onClick={onRemove}>이 출력 삭제</button>}</div>
    <div className="diagram-mode-options">
      <label className="checkbox"><input type="checkbox" checked={Boolean(view.focusOnChanges)} onChange={(event) => onChange({ focusOnChanges: event.target.checked })} /> 요약</label>
      <label className="checkbox"><input type="checkbox" checked={Boolean(view.compareRevisions)} onChange={(event) => onChange({ compareRevisions: event.target.checked })} /> 비교</label>
      <span>요약은 Git 변경점과 최소 문맥만 표시하며, 비교는 Base와 Target을 좌우로 생성합니다.</span>
    </div>
    <PresetPicker presets={typePresets} selectedId={view.presetId} onSelect={(selected) => onChange({ presetId: selected.id, overrides: undefined })} />
    <div className="effective-options" aria-label={`${group.title} 출력 ${index + 1} 최종 옵션`}><strong>최종 적용</strong><span>샘플 {preset?.name ?? view.presetId}</span><span>{effective.direction}</span><span>{effective.detail}</span><span>Caller {effective.caller}</span><span>Callee {effective.callee}</span><span>관계 {effective.relation}</span>{view.focusOnChanges && <span className="override-chip">요약</span>}{view.compareRevisions && <span className="override-chip">비교</span>}{customized && <span className="override-chip">고급값 적용</span>}</div>
    <details><summary>고급 옵션 {customized ? "· 적용됨" : "· 샘플 기본값"}</summary><div className="field-row">
      <label>방향<select value={view.overrides?.direction ?? ""} onChange={(event) => onChange({ overrides: { ...view.overrides, direction: (event.target.value || undefined) as "LR" | "TB" | undefined } })}><option value="">샘플 기본값</option><option value="LR">가로 (LR)</option><option value="TB">세로 (TB)</option></select></label>
      <label>상세도<select value={view.overrides?.detailLevel ?? ""} onChange={(event) => onChange({ overrides: { ...view.overrides, detailLevel: (event.target.value || undefined) as "compact" | "balanced" | "detailed" | undefined } })}><option value="">샘플 기본값</option><option value="compact">간결</option><option value="balanced">균형</option><option value="detailed">상세</option></select></label>
      <DepthSelect label="Caller 깊이" value={view.overrides?.callerDepth} onChange={(value) => onChange({ overrides: { ...view.overrides, callerDepth: value } })} />
      <DepthSelect label="Callee 깊이" value={view.overrides?.calleeDepth} onChange={(value) => onChange({ overrides: { ...view.overrides, calleeDepth: value } })} />
      <DepthSelect label="관계 깊이" value={view.overrides?.relationDepth} onChange={(value) => onChange({ overrides: { ...view.overrides, relationDepth: value } })} />
    </div></details>
  </fieldset>;
}

function DepthSelect({ label, value, onChange }: { label: string; value?: number; onChange: (value?: number) => void }) {
  return <label>{label}<select value={value ?? ""} onChange={(event) => onChange(event.target.value === "" ? undefined : Number(event.target.value))}><option value="">샘플 기본값</option>{[0, 1, 2, 3].map((item) => <option key={item}>{item}</option>)}</select></label>;
}

function AnalysisResultView({ analysis, activeGroup, setActiveGroup, activeView, setActiveView, reportError, presets,
  regeneratingViewId, onRegenerateView }: {
  analysis: AnalysisResponse;
  activeGroup: string;
  setActiveGroup: (id: string) => void;
  activeView: string;
  setActiveView: (id: string) => void;
  reportError: (message: string) => void;
  presets: DiagramPreset[];
  regeneratingViewId: string;
  onRegenerateView: (groupId: string, selection: DiagramViewSelection) => Promise<void>;
}) {
  if (!analysis.result) return null;
  const groups = analysis.result.diagramGroups ?? [];
  const selected = groups.find((group) => group.groupId === activeGroup) ??
    groups.find((group) => effectiveResultViews(group).some((view) => view.diagram || view.comparisonBaseDiagram));
  const views = selected ? effectiveResultViews(selected) : [];
  const selectedView = views.find((view) => view.viewId === activeView) ??
    views.find((view) => view.diagram || view.comparisonBaseDiagram) ?? views[0];
  const selectedPreset = selectedView ? presets.find((preset) => preset.id === selectedView.selection.presetId && preset.type === selectedView.selection.diagramType) : undefined;
  return <div className="analysis-result">
    <div className="summary-card"><strong>전체 요약</strong><p>{analysis.result.narrative.summary}</p></div>
    {analysis.result.narrative.warnings.map((warning) => <p className="warning" key={warning}>{warning}</p>)}
    {groups.length > 0 && <><p className="tab-label">그룹</p><div className="diagram-type-tabs">{groups.map((group) => <button type="button" className={selected?.groupId === group.groupId ? "active" : ""} key={group.groupId} onClick={() => { setActiveGroup(group.groupId); setActiveView(effectiveResultViews(group)[0]?.viewId ?? ""); }}>{group.title}</button>)}</div></>}
    {selected && <article className="diagram-result">
      <div className="summary-card"><strong>{selected.title}</strong><p>{selected.narrative.summary}</p><small>{selected.narrative.intent}</small></div>
      {views.length > 0 && <><p className="tab-label">다이어그램 형식</p><div className="diagram-type-tabs">{views.map((view) => <button type="button" className={selectedView?.viewId === view.viewId ? "active" : ""} key={view.viewId} onClick={() => setActiveView(view.viewId)}>{formatDiagramType(view.selection.diagramType)}{view.state === "Failed" ? " · 실패" : ""}</button>)}</div></>}
      {selectedView && <div className="effective-options"><strong>적용 옵션</strong><span>{selectedPreset?.name ?? selectedView.selection.presetId}</span><span>{selectedView.selection.overrides?.direction ?? selectedPreset?.direction ?? "LR"}</span><span>{selectedView.selection.overrides?.detailLevel ?? selectedPreset?.detailLevel ?? "balanced"}</span><span>Caller {selectedView.selection.overrides?.callerDepth ?? selectedPreset?.callerDepth ?? 1}</span><span>Callee {selectedView.selection.overrides?.calleeDepth ?? selectedPreset?.calleeDepth ?? 1}</span><span>관계 {selectedView.selection.overrides?.relationDepth ?? selectedPreset?.relationDepth ?? 1}</span>{selectedView.selection.focusOnChanges && <span className="override-chip">요약</span>}{selectedView.selection.compareRevisions && <span className="override-chip">비교</span>}<span>{selectedView.reused ? "이전 결과 재사용" : "새로 생성"}</span></div>}
      {selectedView && <ResultViewOptionsEditor group={selected} view={selectedView} siblingViews={views.map((item) => item.selection)} presets={presets}
        busy={regeneratingViewId === selectedView.viewId} onApply={(selection) => onRegenerateView(selected.groupId, selection)} />}
      {selectedView?.errorMessage && <p className="warning">{selectedView.errorMessage}</p>}
      {selectedView?.selection.compareRevisions ? <div className="comparison-diagrams">
        <AnalysisDiagramPane title={`Base · ${analysis.baseSha?.slice(0, 12) ?? "알 수 없음"}`} artifact={selectedView.comparisonBaseDiagram}
          emptyText="Base revision에 선택 변경 요소가 없습니다." analysis={analysis} groupId={selected.groupId} view={selectedView} side="base" reportError={reportError} />
        <AnalysisDiagramPane title={`Target · ${analysis.targetSha?.slice(0, 12) ?? "알 수 없음"}`} artifact={selectedView.diagram}
          emptyText="Target revision에 선택 변경 요소가 없습니다." analysis={analysis} groupId={selected.groupId} view={selectedView} side="target" reportError={reportError} />
      </div> : selectedView?.diagram ? <AnalysisDiagramPane artifact={selectedView.diagram} analysis={analysis} groupId={selected.groupId}
        view={selectedView} side="target" reportError={reportError} /> : <EmptyState text={selectedView?.warnings[0] ?? selected.warnings[0] ?? "이 그룹의 다이어그램을 생성하지 못했습니다."} />}
    </article>}
    {groups.length === 0 && analysis.result.diagrams[0] && <MermaidPreview source={analysis.result.diagrams[0].mermaidDsl} downloadName="git-analysis" zoomable />}
    <h3>변경 파일</h3><ul className="file-list">{analysis.result.changedFiles.map((file) => <li key={`${file.path}-${file.changeKind}`}><span className={`change ${file.changeKind.toLowerCase()}`}>{file.changeKind}</span><code>{file.previousPath ? `${file.previousPath} → ${file.path}` : file.path}</code></li>)}</ul>
  </div>;
}

function ResultViewOptionsEditor({ group, view, siblingViews, presets, busy, onApply }: {
  group: AnalysisDiagramGroup;
  view: AnalysisDiagramView;
  siblingViews: DiagramViewSelection[];
  presets: DiagramPreset[];
  busy: boolean;
  onApply: (selection: DiagramViewSelection) => Promise<void>;
}) {
  const [draft, setDraft] = useState(view.selection);
  useEffect(() => setDraft(view.selection), [view.viewId, view.selection]);
  const changed = JSON.stringify(draft) !== JSON.stringify(view.selection);
  const editorGroup: AnalysisGroupSelection = {
    id: group.groupId, title: group.title, changeIds: group.changeIds,
    diagramType: draft.diagramType, presetId: draft.presetId, overrides: draft.overrides, views: [draft],
  };
  return <details className="result-options-editor">
    <summary>적용 옵션 수정</summary>
    <GroupViewEditor group={editorGroup} view={draft} index={0} presets={presets}
      siblingViews={siblingViews.map((item) => item.id === draft.id ? draft : item)} allowRemove={false}
      onChange={(patch) => setDraft((current) => ({ ...current, ...patch }))} onRemove={() => undefined} />
    <div className="result-option-actions">
      <button type="button" className="secondary" disabled={!changed || busy} onClick={() => setDraft(view.selection)}>변경 취소</button>
      <button type="button" className="primary" disabled={!changed || busy} onClick={() => void onApply(draft)}>{busy ? "다시 생성 중…" : "이 다이어그램 다시 그리기"}</button>
    </div>
  </details>;
}

function AnalysisDiagramPane({ title, artifact, emptyText, analysis, groupId, view, side, reportError }: {
  title?: string;
  artifact?: AnalysisDiagramView["diagram"];
  emptyText?: string;
  analysis: AnalysisResponse;
  groupId: string;
  view: AnalysisDiagramView;
  side: "base" | "target";
  reportError: (message: string) => void;
}) {
  return <section className="comparison-pane">
    {title && <h4>{title}</h4>}
    {artifact ? <DiagramEditor artifact={artifact} zoomable downloadName={`git-${side}-${artifact.type}-${(side === "base" ? analysis.baseSha : analysis.targetSha)?.slice(0, 8) ?? "result"}`} reportError={reportError}
      onSave={(input) => api.saveAnalysisDiagramEdit(analysis.id, groupId, view.viewId, input, side)}
      onPreview={(input, signal) => api.previewAnalysisDiagramEdit(analysis.id, groupId, view.viewId, input, signal, side)} />
      : <EmptyState text={emptyText ?? view.warnings[0] ?? "다이어그램을 생성하지 못했습니다."} />}
  </section>;
}

function Progress({ value, label }: { value: number; label: string }) {
  return <div className="progress-block"><div className="progress-meta"><span>{label}</span><strong>{value}%</strong></div><div className="progress-track"><span style={{ width: `${value}%` }} /></div></div>;
}

function EmptyState({ text }: { text: string }) { return <div className="empty-state"><p>{text}</p></div>; }
function messageOf(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback; }
function defaultPreset(type: DiagramType, presets: DiagramPreset[]) { return presets.find((preset) => preset.type === type && preset.detailLevel === "balanced")?.id ?? presets.find((preset) => preset.type === type)?.id ?? "balanced"; }
function createView(type: DiagramType, presets: DiagramPreset[]): DiagramViewSelection {
  return { id: crypto.randomUUID(), diagramType: type, presetId: defaultPreset(type, presets) };
}
function effectiveGroupViews(group: AnalysisGroupSelection): DiagramViewSelection[] {
  return group.views?.length ? group.views : [{ id: `${group.id}-view`, diagramType: group.diagramType, presetId: group.presetId, overrides: group.overrides }];
}
function normalizeGroup(group: AnalysisGroupSelection): AnalysisGroupSelection {
  const views = effectiveGroupViews(group);
  return { ...group, views, diagramType: views[0].diagramType, presetId: views[0].presetId, overrides: views[0].overrides };
}
function effectiveResultViews(group: AnalysisDiagramGroup) {
  if (group.views?.length) return group.views;
  const selection: DiagramViewSelection = { id: `${group.groupId}-view`, diagramType: (group.diagram?.type ?? "flowchart") as DiagramType, presetId: "balanced" };
  return [{ viewId: selection.id, selection, diagram: group.diagram, warnings: group.warnings, state: group.diagram ? "Completed" : "Failed", reused: false }];
}
function changedViewIds(groups: AnalysisGroupSelection[], source: AnalysisResponse | null) {
  if (!source?.result?.diagramGroups) return groups.flatMap((group) => effectiveGroupViews(group).map((view) => view.id));
  const result: string[] = [];
  for (const group of groups) {
    const previousGroup = source.result.diagramGroups.find((item) => item.groupId === group.id);
    const changesChanged = !previousGroup || [...previousGroup.changeIds].sort().join("\n") !== [...group.changeIds].sort().join("\n");
    const previousViews = previousGroup ? new Map(effectiveResultViews(previousGroup).map((view) => [view.viewId, view.selection])) : new Map<string, DiagramViewSelection>();
    for (const view of effectiveGroupViews(group)) {
      const previous = previousViews.get(view.id);
      if (changesChanged || !previous || JSON.stringify(previous) !== JSON.stringify(view)) result.push(view.id);
    }
  }
  return result;
}
function formatDiagramType(type: string) { return ({ flowchart: "흐름 / 영향도", class: "클래스 관계", sequence: "호출 시퀀스", "code-relation": "코드 관계도", state: "상태 전이" } as Record<string, string>)[type] ?? type; }
function isCppDiagnostic(value: string) { return /C\+\+|syntax|parser|구문|모호|제외|인덱스|index/i.test(value); }
function exclusionReason(reason: string) {
  return ({
    multipleTargets: "동일한 조건의 호출 대상이 여러 개입니다.",
    indirectTypeUnresolved: "간접 호출의 클래스 인자를 문자열로 확정할 수 없습니다.",
    indirectTypeNotFound: "간접 호출이 가리키는 클래스를 프로젝트에서 찾지 못했습니다.",
  } as Record<string, string>)[reason] ?? reason;
}
