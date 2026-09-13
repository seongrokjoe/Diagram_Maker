import { useCallback, useEffect, useRef, useState } from "react";
import { api, request } from "./api";
import { DiagramEditor } from "./DiagramEditor";
import { SemanticProgressView } from "./SemanticProgressView";
import { CodeBlockComposer } from "./CodeBlockComposer";
import { codeInputError, defaultCodeBlockLimits, type CodeBlockLimits } from "./codeBlockLimits";
import { CodeBlockResultTree } from "./CodeBlockResultTree";
import { initialDraft, normalizeDraft, isSemanticPage, typeLabels, type CodeBlockLocationSelection } from "./codeBlockWorkspaceState";
import type { DiagramArtifact, DiagramEditPreview, DiagramPreset, DiagramRevisionRecord } from "./types";
import type { CodeBlockDraft, CodeBlockEvidence, CodeBlockRun, CodeBlockWorkspaceRecord, CodeBlockWorkspaceSummary } from "./codeBlockTypes";

const terminal = (state: string) => ["Completed", "Partial", "Failed", "Cancelled"].includes(state);
const json = (value: unknown) => JSON.stringify(value);
const errorOf = (error: unknown) => error instanceof Error ? error.message : "요청을 처리하지 못했습니다.";

export function CodeBlockWorkspace() {
  const [draft, setDraft] = useState<CodeBlockDraft>(initialDraft);
  const [draftKey, setDraftKey] = useState(0);
  const [titleInvalid, setTitleInvalid] = useState(false);
  const titleInput = useRef<HTMLInputElement>(null);
  const [workspace, setWorkspace] = useState<CodeBlockWorkspaceRecord | null>(null);
  const [workspaces, setWorkspaces] = useState<CodeBlockWorkspaceSummary[]>([]);
  const [runs, setRuns] = useState<CodeBlockRun[]>([]);
  const [run, setRun] = useState<CodeBlockRun | null>(null);
  const [openedPartial, setOpenedPartial] = useState("");
  const resultsVisible = !run || terminal(run.state) && (!run.stopReason || openedPartial === run.id);
  const [presets, setPresets] = useState<DiagramPreset[]>([]);
  const [llmConfigured, setLlmConfigured] = useState<boolean | null>(null);
  const [limits, setLimits] = useState<CodeBlockLimits>(defaultCodeBlockLimits);
  const inputError = codeInputError(draft, limits);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [pendingOpen, setPendingOpen] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [merges, setMerges] = useState<Record<string, boolean>>({});
  const [active, setActive] = useState<CodeBlockLocationSelection | null>(null);
  const [artifact, setArtifact] = useState<DiagramArtifact | null>(null);
  const [evidence, setEvidence] = useState<CodeBlockEvidence | null>(null);
  const [screen, setScreen] = useState<"compose" | "results">("compose");
  const [showStatic, setShowStatic] = useState(false);
  const [ready, setReady] = useState(false);
  const epoch = useRef(0);
  const mergeRun = useRef<string | null>(null);
  const reportError = useCallback((message: string) => setError(message), []);
  const refreshWorkspaces = useCallback(async () => setWorkspaces(await request<CodeBlockWorkspaceSummary[]>("/api/v1/code-block-workspaces")), []);
  const refreshRuns = useCallback(async (id: string) => setRuns(await request<CodeBlockRun[]>(`/api/v1/code-block-workspaces/${id}/runs`)), []);
  useEffect(() => {
    void refreshWorkspaces().catch(e => setError(errorOf(e)));
    void api.listPresets().then(setPresets).catch(e => setError(errorOf(e)));
    void request<{ llmConfigured: boolean; codeBlockLimits?: CodeBlockLimits }>("/api/v1/runtime-info").then(r => {
      setLlmConfigured(r.llmConfigured); if (r.codeBlockLimits) setLimits(r.codeBlockLimits);
    }).catch(() => setLlmConfigured(null));
    const current = new URLSearchParams(window.location.search).get("codeWorkspace");
    if (current) void open(current, true); else setReady(true);
  }, [refreshWorkspaces]);
  useEffect(() => {
    if (!run || terminal(run.state) || run.state === "NeedsClarification") return;
    const id = run.id; let cancelled = false; let pending = false;
    const timer = window.setInterval(() => {
      if (pending) return; pending = true;
      void request<CodeBlockRun>(`/api/v1/code-block-runs/${id}`).then(value => {
        if (cancelled) return; setRun(value);
        if (terminal(value.state)) void refreshRuns(value.workspaceId).catch(e => setError(errorOf(e)));
      }).catch(e => { if (!cancelled) setError(errorOf(e)); }).finally(() => { pending = false; });
    }, 1000);
    return () => { cancelled = true; window.clearInterval(timer); };
  }, [run?.id, run?.state, refreshRuns]);
  useEffect(() => {
    if (!resultsVisible || active || !run?.results.length) return;
    for (const g of run.results) for (const v of g.views) {
      const p = v.pages.find(p => isSemanticPage(v, p));
      if (p) { setActive({ group: g.groupId, view: v.viewId, page: p.id }); return; }
    }
    const g = run.results[0]; if (g.views[0]) setActive({ group: g.groupId, view: g.views[0].viewId, page: "" });
  }, [run, active, resultsVisible]);
  useEffect(() => {
    setArtifact(null); setEvidence(null); if (!resultsVisible || !run || !active?.page) return;
    const controller = new AbortController();
    void request<DiagramArtifact>(pagePath(run.id, active), { signal: controller.signal }).then(setArtifact)
      .catch(e => { if (!controller.signal.aborted) setError(errorOf(e)); });
    return () => controller.abort();
  }, [run?.id, active?.group, active?.view, active?.page, resultsVisible]);
  useEffect(() => {
    if (!ready || busy) return;
    const url = new URL(window.location.href);
    const values = { codeWorkspace: workspace?.id, codeRun: run?.id, codeScreen: screen,
      codeGroup: active?.group, codeView: active?.view, codePage: active?.page };
    for (const [key, value] of Object.entries(values)) { if (value) url.searchParams.set(key, value); else url.searchParams.delete(key); }
    window.history.replaceState(null, "", url);
  }, [ready, busy, workspace?.id, run?.id, screen, active]);
  useEffect(() => {
    const unload = (event: BeforeUnloadEvent) => { if (dirty) event.preventDefault(); };
    window.addEventListener("beforeunload", unload); return () => window.removeEventListener("beforeunload", unload);
  }, [dirty]);
  useEffect(() => { if (titleInvalid && !busy && screen === "compose") titleInput.current?.focus(); }, [titleInvalid, busy, screen]);
  useEffect(() => {
    if (!run || mergeRun.current !== run.id || !terminal(run.state)) return;
    mergeRun.current = null;
    if (dirty || run.inputRevision !== workspace?.revision || !run.groups.length) return;
    setDraft(current => normalizeDraft({ ...current, groups: [...run.groups,
      ...(current.groups ?? []).filter(g => !g.blockIds.length && !run.groups.some(r => r.id === g.id))] }));
    setDirty(true);
  }, [run, dirty, workspace?.revision]);
  function change(value: CodeBlockDraft) { setDraft(value); setDirty(true); }
  function selectRun(value: CodeBlockRun | null, location: CodeBlockLocationSelection | null = null) {
    setRun(value); setAnswers({}); setMerges({}); setActive(location); setArtifact(null); setEvidence(null); setShowStatic(false);
    if (location?.page) { const v = value?.results.find(g => g.groupId === location.group)?.views.find(v => v.viewId === location.view);
      const p = v?.pages.find(p => p.id === location.page); if (v && p) setShowStatic(!isSemanticPage(v, p)); }
  }
  async function action(work: () => Promise<void>) { setBusy(true); setError(""); try { await work(); } catch (e) { setError(errorOf(e)); } finally { setBusy(false); } }
  async function open(id: string, restore = false) {
    const generation = ++epoch.current;
    const params = new URLSearchParams(window.location.search);
    await action(async () => {
      if (id === "new") { setWorkspace(null); setDraft(initialDraft()); setDraftKey(key => key + 1); setTitleInvalid(false); mergeRun.current = null; selectRun(null); setRuns([]); setDirty(false); setPendingOpen(null); setScreen("compose"); setReady(true); return; }
      const [record, history] = await Promise.all([
        request<CodeBlockWorkspaceRecord>(`/api/v1/code-block-workspaces/${id}`), request<CodeBlockRun[]>(`/api/v1/code-block-workspaces/${id}/runs`)
      ]);
      let selected = restore ? history.find(r => r.id === params.get("codeRun")) ?? history[0] : history[0];
      if (restore && params.get("codeRun") && !history.some(r => r.id === params.get("codeRun"))) {
        try { const older = await request<CodeBlockRun>(`/api/v1/code-block-runs/${encodeURIComponent(params.get("codeRun")!)}`); if (older.workspaceId === id) { selected = older; history.push(older); } } catch { /* A deleted run falls back to the latest history. */ }
      }
      if (generation !== epoch.current) return;
      const g = selected?.results.find(g => g.groupId === params.get("codeGroup"));
      const v = g?.views.find(v => v.viewId === params.get("codeView"));
      const p = v?.pages.find(p => p.id === params.get("codePage"));
      setWorkspace(record); setDraft(normalizeDraft(record.input, history.find(r => r.inputRevision === record.revision && r.groups.length > 0)));
      setDraftKey(key => key + 1); setTitleInvalid(false); mergeRun.current = null;
      setRuns(history); selectRun(selected ?? null, restore && g && v ? { group: g.groupId, view: v.viewId, page: p?.id ?? "" } : null);
      setDirty(false); setPendingOpen(null); setScreen(restore && params.get("codeScreen") === "results" ? "results" : "compose"); setReady(true);
    });
  }
  async function save(): Promise<CodeBlockWorkspaceRecord> {
    if (inputError) throw new Error(inputError);
    if (new TextEncoder().encode(json({ input: draft, expectedRevision: workspace?.revision })).byteLength > limits.maximumRequestBytes)
      throw new Error("저장 요청의 바이트 한도를 초과했습니다. 입력은 보존했습니다. 작업을 나누어 주세요.");
    if (!draft.title.trim()) { setTitleInvalid(true); setScreen("compose"); throw new Error("작업 제목을 입력하세요."); }
    if (workspace && !dirty && json(draft) === json(workspace.input)) return workspace;
    const record = await request<CodeBlockWorkspaceRecord>(workspace ? `/api/v1/code-block-workspaces/${workspace.id}` : "/api/v1/code-block-workspaces", {
      method: workspace ? "PUT" : "POST", body: json(workspace ? { expectedRevision: workspace.revision, input: draft } : draft) });
    setWorkspace(record); setDraft(normalizeDraft(record.input)); setDirty(false); await refreshWorkspaces(); return record;
  }
  async function generate(viewIds?: string[]) {
    await action(async () => { const record = await save(); const value = await request<CodeBlockRun>(`/api/v1/code-block-workspaces/${record.id}/runs`, {
      method: "POST", body: json({ expectedRevision: record.revision, regenerateViewIds: viewIds }) });
      selectRun(value); setScreen("results"); await refreshRuns(record.id); });
  }
  async function showEvidence(id: string) { if (run) await action(async () => setEvidence(await request<CodeBlockEvidence>(`/api/v1/code-block-runs/${run.id}/evidence/${encodeURIComponent(id)}`))); }
  async function submitAnswers(skip: boolean) {
    if (!run) return;
    if (!skip && run.questions.some(q => merges[q.id] && !["unknown", "none"].includes(answers[q.id] ?? "unknown"))) mergeRun.current = run.id;
    await action(async () => setRun(await request<CodeBlockRun>(`/api/v1/code-block-runs/${run.id}/answers`, { method: "POST", body: json({
      expectedInputRevision: run.inputRevision, expectedRunRevision: run.revision, skipRemaining: true,
      answers: skip ? [] : run.questions.map(q => ({ questionId: q.id, optionId: answers[q.id] ?? "unknown", mergeGroups: merges[q.id] ?? false }))
    }) })));
  }
  const running = run && !terminal(run.state);
  const groups = draft.groups!;
  const selectedGroup = run?.results.find(g => g.groupId === active?.group);
  const selectedView = selectedGroup?.views.find(v => v.viewId === active?.view);
  const selectedPage = selectedView?.pages.find(p => p.id === active?.page);
  function openStatic() {
    setShowStatic(true); const p = selectedView?.pages.find(p => !isSemanticPage(selectedView, p));
    if (p && active) setActive({ ...active, page: p.id });
  }
  return <section className="code-block-workspace" data-screen={screen}>
    <section className="panel code-block-toolbar"><h2>코드 블럭 다이어그램</h2>
      <p>코드를 그룹으로 구성하고, 동작의 의미와 원본 근거를 살펴보세요.</p>
      <div className="form-row code-block-workspace-actions"><label className="saved-workspace-picker">저장한 작업<select aria-label="저장한 코드 작업" title={workspace?.input.title ?? "새 작업"} value={workspace?.id ?? "new"} disabled={busy} onChange={e => dirty ? setPendingOpen(e.target.value) : void open(e.target.value)}>
        <option value="new">새 작업</option>{workspaces.map(w => <option key={w.id} value={w.id}>{w.title} · {w.blockCount}개</option>)}</select></label>
        <button disabled={busy} onClick={() => dirty ? setPendingOpen("new") : void open("new")}>새 작업</button>
        <button disabled={busy || Boolean(inputError)} onClick={() => void action(async () => { await save(); })}>초안 저장</button>
        <button disabled={busy || !workspace} onClick={() => setConfirmDelete(true)}>작업 삭제</button>
        <span className={llmConfigured === false ? "warning" : "help"}>{llmConfigured === null ? "LLM 구성 여부 확인 불가" : llmConfigured ? "LLM 구성됨" : "LLM 미설정 · 정적 구조만 제공"}</span></div>
      {pendingOpen && <div className="warning">저장하지 않은 초안이 있습니다. <button onClick={() => void action(async () => { await save(); await open(pendingOpen); })}>저장하고 열기</button>
        <button onClick={() => void open(pendingOpen)}>초안 버리고 열기</button><button onClick={() => setPendingOpen(null)}>계속 편집</button></div>}
      {confirmDelete && workspace && <div className="warning">코드 원문과 모든 생성·편집 이력을 삭제합니다.
        <button disabled={busy} onClick={() => void action(async () => { await request(`/api/v1/code-block-workspaces/${workspace.id}?expectedRevision=${workspace.revision}`, { method: "DELETE" });
          setConfirmDelete(false); await refreshWorkspaces(); await open("new"); })}>영구 삭제</button><button onClick={() => setConfirmDelete(false)}>취소</button></div>}
      {error && <p className="error" role="alert">{error}</p>}{busy && <p role="status">작업을 처리하고 있습니다…</p>}
      <div className="form-row code-block-title-row"><label>작업 제목 (필수)<input ref={titleInput} aria-label="작업 제목" required aria-invalid={titleInvalid} disabled={busy} placeholder="코드 블럭 다이어그램" value={draft.title} maxLength={200}
        onChange={e => { setTitleInvalid(false); change({ ...draft, title: e.target.value }); }} /></label>
        <p className="help">{dirty ? "저장하지 않은 초안" : workspace ? `저장됨 · 수정 ${workspace.revision}` : "새 초안"} · {draft.blocks.reduce((n, b) => n + b.code.length, 0).toLocaleString()} / {limits.maximumTotalCharacters.toLocaleString()}자</p></div>
      {inputError && <p className="error" role="alert">{inputError}</p>}
      <div className="code-block-screen-tabs" role="tablist" aria-label="코드 작업 화면" onKeyDown={e => {
        if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) return;
        e.preventDefault(); const next = e.key === "Home" ? "compose" : e.key === "End" ? "results" : screen === "compose" ? "results" : "compose";
        setScreen(next); e.currentTarget.querySelector<HTMLButtonElement>(`#code-tab-${next}`)?.focus();
      }}>
        <button className="code-block-screen-tab" id="code-tab-compose" role="tab" aria-controls="code-panel-compose" tabIndex={screen === "compose" ? 0 : -1} aria-selected={screen === "compose"} onClick={() => setScreen("compose")}>코드 구성</button>
        <button className="code-block-screen-tab" id="code-tab-results" role="tab" aria-controls="code-panel-results" tabIndex={screen === "results" ? 0 : -1} aria-selected={screen === "results"} onClick={() => setScreen("results")}>다이어그램 결과</button>
      </div>
    </section>
    <fieldset id="code-panel-compose" role="tabpanel" aria-labelledby="code-tab-compose" className="code-block-controls" disabled={busy} hidden={screen !== "compose"}>
      <CodeBlockComposer key={draftKey} draft={draft} groups={groups} presets={presets} limits={limits} onChange={change} />
      <div className="code-block-generate-actions"><button className="primary code-block-generate" disabled={Boolean(inputError || running && !dirty)} onClick={() => void generate()}>다이어그램 생성</button></div>
    </fieldset>
    {run && <section className="panel code-block-run-status" aria-label="생성 상태"><strong>{run.stageMessage}</strong>
      {!terminal(run.state) && <><progress max={100} value={run.progress} /><span> {run.progress}%</span><button disabled={busy} onClick={() => void action(async () => setRun(await request<CodeBlockRun>(`/api/v1/code-block-runs/${run.id}/cancel`, { method: "POST" })))}>생성 취소</button></>}
      {run.errorMessage && <p className="error">{run.errorMessage}</p>}
      {run.execution && <SemanticProgressView value={run.execution} running={Boolean(running)} />}
      {run.canResume && <button disabled={busy || dirty || Boolean(running) || run.inputRevision !== workspace?.revision} onClick={() => void action(async () => {
        const resumed = await request<CodeBlockRun>(`/api/v1/code-block-runs/${run.id}/resume`, { method: "POST", body: json({ expectedRevision: run.revision }) });
        selectRun(resumed); await refreshRuns(resumed.workspaceId);
      })}>완료 단위부터 이어서 생성</button>}
      <button disabled={busy} onClick={() => void action(async () => {
        const response = await fetch(`/api/v1/code-block-runs/${run.id}/diagnostics?format=text`);
        if (!response.ok) throw new Error("진단 텍스트를 다운로드하지 못했습니다.");
        const url = URL.createObjectURL(new Blob([await response.text()], { type: "text/plain;charset=utf-8" }));
        const link = document.createElement("a"); link.href = url; link.download = `code-block-${run.id}-diagnostics.txt`; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      })}>텍스트 진단 다운로드</button>
      <button disabled={busy} onClick={() => void action(async () => {
        const report = await request(`/api/v1/code-block-runs/${run.id}/diagnostics`);
        const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], { type: "application/json" }));
        const link = document.createElement("a"); link.href = url; link.download = `code-block-${run.id}-diagnostics.json`; link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      })}>작업 진단 다운로드</button>
      {run.warnings.length > 0 && <details><summary>분석 안내 {run.warnings.length}건</summary><ul>{run.warnings.map((w, i) => <li key={i}>{w}</li>)}</ul></details>}
      {run.inputRevision !== workspace?.revision && <p className="warning">이 결과는 과거 입력의 고정 스냅샷입니다.</p>}
      {run.state === "NeedsClarification" && <div><h3>관계 확인</h3>{run.questions.map(q => <fieldset key={q.id}><legend>{q.prompt}</legend>
        <select aria-label={q.prompt} value={answers[q.id] ?? "unknown"} onChange={e => setAnswers({ ...answers, [q.id]: e.target.value })}><option value="unknown">모르겠음</option><option value="none">연결 없음</option>{q.options.map(o => <option key={o.id} value={o.id}>{o.label}</option>)}</select>
        <label><input type="checkbox" checked={merges[q.id] ?? false} onChange={e => setMerges({ ...merges, [q.id]: e.target.checked })} />선택 대상과 그룹 합치기</label>
        {q.evidenceIds.map(id => <button key={id} onClick={() => void showEvidence(id)}>코드 위치 보기</button>)}</fieldset>)}
        <button disabled={busy || dirty} onClick={() => void submitAnswers(false)}>답변 적용하고 생성</button>
        <button disabled={busy || dirty} onClick={() => void submitAnswers(true)}>나머지 건너뛰기</button></div>}
    </section>}
    <section id="code-panel-results" role="tabpanel" aria-labelledby="code-tab-results" hidden={screen !== "results"} className="code-block-results">
      {!resultsVisible && <div className="panel" role="status"><p>{running ? "모든 상세 페이지를 완성한 뒤 결과를 표시합니다. 완료된 작업은 계속 저장됩니다." : "생성이 중단되었습니다. 저장된 부분 결과를 확인하거나 이어서 생성할 수 있습니다."}</p>
        {!running && run?.results.some(g => g.views.some(v => v.pages.length > 0)) && <button onClick={() => setOpenedPartial(run.id)}>부분 결과 열기</button>}</div>}
      {resultsVisible && <>
      <aside className="panel code-block-result-sidebar">
        <label>생성 이력<select aria-label="생성 이력" disabled={busy} value={run?.id ?? ""} onChange={e => selectRun(runs.find(r => r.id === e.target.value) ?? null)}><option value="" disabled>생성 이력 선택</option>
          {runs.map(r => <option key={r.id} value={r.id}>{new Date(r.createdAt).toLocaleString()} · 입력 {r.inputRevision} · {r.state}</option>)}</select></label>
        <label><input type="checkbox" checked={showStatic} onChange={e => { setShowStatic(e.target.checked); if (!e.target.checked && selectedPage && selectedView && !isSemanticPage(selectedView, selectedPage) && active) setActive({ ...active, page: "" }); }} />정적 구조 보기</label>
        {run ? <CodeBlockResultTree key={run.id} run={run} active={active} showStatic={showStatic} onSelect={setActive} /> : <p>코드를 입력하고 ‘다이어그램 생성’을 실행하세요.</p>}
      </aside>
      <div className="code-block-result-content">
        {selectedView && <section className="panel code-block-view-status"><div className="form-row"><h3>{typeLabels.find(([t]) => t === selectedView.selection.diagramType)?.[1]} · {selectedGroup?.title}</h3>
          <button disabled={busy || Boolean(running) || dirty || run?.inputRevision !== workspace?.revision} onClick={() => void generate([selectedView.viewId])}>이 결과 재생성</button></div>
          {selectedView.reused && <p className="help">{selectedView.state === "Completed" ? "이전 결과를 재사용했습니다." : "이전 성공 그림을 유지하고 최신 실패 사유를 표시합니다."}</p>}
          {selectedView.errorMessage && <p role="alert" className="error">{selectedView.errorMessage}</p>}
          {selectedView.failureStage && <p className="help">실패 단계: {failureLabel(selectedView.failureStage)}</p>}
          {selectedPage && !isSemanticPage(selectedView, selectedPage) && <p className="warning">정적 구조 · 의미 검토를 완료한 다이어그램이 아닙니다.</p>}
          {!active?.page && <><p>의미 다이어그램을 완성하지 못했습니다. 분석된 코드 구조를 별도로 살펴볼 수 있습니다.</p>
            {selectedView.pages.some(p => !isSemanticPage(selectedView, p)) && <button onClick={openStatic}>정적 구조 열기</button>}</>}
          <details><summary>생성 안내와 종류별 가능 여부</summary>{selectedView.warnings.map((w, i) => <p key={i}>{w}</p>)}<ul>{selectedGroup?.availability.map(a => <li key={a.type}>{typeLabels.find(([t]) => t === a.type)?.[1]}: {a.available ? "가능" : a.reason}</li>)}</ul></details>
        </section>}
        {run && active?.page && artifact && <DiagramEditor artifact={artifact} downloadName={`code-block-${run.id}`} zoomable showExplanation collapsibleExplanation canvasToolbar reportError={reportError} onEvidence={id => void showEvidence(id)}
          onOpenDetail={page => { if (selectedView?.pages.some(p => p.id === page)) setActive({ ...active, page }); }}
          onPreview={(input, signal) => request<DiagramEditPreview>(pagePath(run.id, active) + "/edit-preview", { method: "POST", body: json(input), signal })}
          onSave={input => request<DiagramRevisionRecord>(pagePath(run.id, active) + "/edits", { method: "POST", body: json(input) })} />}
        {!selectedView && <div className="panel empty-state">왼쪽 트리에서 결과를 선택하세요.</div>}
      </div>
      </>}
    </section>
    {evidence && <section className="panel code-block-evidence" role="region" aria-label="코드 근거"><h3>{evidence.blockTitle} · {evidence.startLine}–{evidence.endLine}행</h3>
      <button onClick={() => setEvidence(null)}>근거 닫기</button><pre>{evidence.content}</pre><small>코드 해시 {evidence.contentHash}</small></section>}
  </section>;
}
function pagePath(runId: string, active: CodeBlockLocationSelection) { return `/api/v1/code-block-runs/${runId}/groups/${encodeURIComponent(active.group)}/views/${encodeURIComponent(active.view)}/pages/${encodeURIComponent(active.page)}`; }
function failureLabel(stage: string) { return ({ "llm-configuration": "LLM 미설정", "llm-request": "LLM 요청", understanding: "코드 의미 이해", "plan-validation": "구조와 근거 검증", "semantic-review": "원본 의미 검토", "input-limit": "입력 한도", availability: "종류별 생성 가능 여부", projection: "그림 생성" } as Record<string, string>)[stage] ?? "생성 검증"; }
