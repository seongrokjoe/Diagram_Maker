import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { api } from "./api";
import { MermaidPreview, type DiagramInlineEdit, type DiagramSelection } from "./MermaidPreview";
import { elapsedLabel, useElapsedSeconds } from "./useElapsedSeconds";
import type { DiagramArtifact, DiagramEditDocument, DiagramEditPreview, DiagramRevisionRecord } from "./types";

type EditInput = { rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument };
export function DiagramEditor({ artifact, downloadName, zoomable = false, onSave, onPreview, reportError }: {
  artifact: DiagramArtifact;
  downloadName: string;
  zoomable?: boolean;
  onSave: (input: EditInput) => Promise<DiagramRevisionRecord>;
  onPreview: (input: EditInput, signal: AbortSignal) => Promise<DiagramEditPreview>;
  reportError: (message: string) => void;
}) {
  const [revisions, setRevisions] = useState<DiagramRevisionRecord[]>([]);
  const [selectedRevisionId, setSelectedRevisionId] = useState("");
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [draft, setDraft] = useState<DiagramEditDocument>(() => toDocument(artifact));
  const [history, setHistory] = useState<DiagramEditDocument[]>([toDocument(artifact)]);
  const [historyIndex, setHistoryIndex] = useState(0);
  const [preview, setPreview] = useState<DiagramArtifact | null>(null);
  const [previewError, setPreviewError] = useState("");
  const [selection, setSelection] = useState<DiagramSelection[]>([]);
  const [inlineEdit, setInlineEdit] = useState<DiagramInlineEdit | null>(null);
  const [directEditingAvailable, setDirectEditingAvailable] = useState(false);
  const editorRef = useRef<HTMLDivElement>(null);
  const elapsed = useElapsedSeconds(saving);

  useEffect(() => {
    const original = toDocument(artifact);
    setRevisions([]);
    setSelectedRevisionId("");
    setEditing(false);
    setDraft(original);
    setHistory([original]);
    setHistoryIndex(0);
    setPreview(null);
    setSelection([]);
    setInlineEdit(null);
    void api.listDiagramRevisions(artifact.id).then((items) => {
      setRevisions(items);
      const latest = items.at(-1);
      if (latest) {
        const document = toDocument(latest.diagram);
        setSelectedRevisionId(latest.id);
        setDraft(document);
        setHistory([document]);
      }
    }).catch((reason: unknown) => reportError(messageOf(reason, "편집 리비전을 불러오지 못했습니다.")));
  }, [artifact.id, reportError]);

  const selectedRevision = revisions.find((item) => item.id === selectedRevisionId);
  const latestRevision = revisions.at(-1);
  const displayed = selectedRevision?.diagram ?? artifact;
  const editingLatest = !selectedRevisionId || selectedRevisionId === latestRevision?.id;
  const currentArtifact = editing && preview ? preview : displayed;
  const nodeOptions = useMemo(() => draft.nodes.map((node) => <option key={node.id} value={node.id}>{node.label}</option>), [draft.nodes]);
  const selectedNodes = selection.filter((item) => item.kind === "node")
    .map((item) => draft.nodes.find((node) => node.id === item.id)).filter((node): node is DiagramEditDocument["nodes"][number] => Boolean(node));
  const selectedEdges = selection.filter((item) => item.kind === "edge")
    .map((item) => draft.edges.find((edge) => edge.id === item.id)).filter((edge): edge is DiagramEditDocument["edges"][number] => Boolean(edge));
  const selectedNodeIds = new Set(selectedNodes.map((node) => node.id));
  const connectedEdgeCount = draft.edges.filter((edge) => selectedNodeIds.has(edge.sourceId) || selectedNodeIds.has(edge.targetId)).length;

  useEffect(() => {
    if (!editing) return;
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void onPreview(editInput(artifact, latestRevision, draft), controller.signal)
        .then((result) => {
          setPreview({ ...displayed, version: result.version, ir: result.ir, mermaidDsl: result.mermaidDsl });
          setPreviewError("");
        })
        .catch((reason: unknown) => {
          if (controller.signal.aborted) return;
          setPreviewError(messageOf(reason, "미리보기를 생성하지 못했습니다."));
        });
    }, 250);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [artifact, displayed, draft, editing, latestRevision, onPreview]);

  function beginEdit() {
    const latest = latestRevision?.diagram ?? artifact;
    const document = toDocument(latest);
    setSelectedRevisionId(latestRevision?.id ?? "");
    setDraft(document);
    setHistory([document]);
    setHistoryIndex(0);
    setPreview(latest);
    setSelection([]);
    setInlineEdit(null);
    setEditing(true);
  }

  function updateDraft(update: (current: DiagramEditDocument) => DiagramEditDocument) {
    setDraft((current) => {
      const next = update(current);
      setHistory((values) => {
        const appended = [...values.slice(0, historyIndex + 1), next];
        const limited = appended.slice(-100);
        setHistoryIndex(limited.length - 1);
        return limited;
      });
      return next;
    });
  }

  function undo() {
    if (historyIndex <= 0) return;
    const index = historyIndex - 1;
    setHistoryIndex(index);
    setDraft(history[index]);
    setSelection([]);
    setInlineEdit(null);
  }

  function redo() {
    if (historyIndex >= history.length - 1) return;
    const index = historyIndex + 1;
    setHistoryIndex(index);
    setDraft(history[index]);
    setSelection([]);
    setInlineEdit(null);
  }

  function cancel() {
    const latest = latestRevision?.diagram ?? artifact;
    const document = toDocument(latest);
    setDraft(document);
    setHistory([document]);
    setHistoryIndex(0);
    setPreview(null);
    setPreviewError("");
    setSelection([]);
    setInlineEdit(null);
    setEditing(false);
  }

  function addNode() {
    const id = uniqueId("manual_node", draft.nodes.map((node) => node.id));
    updateDraft((current) => ({ ...current, nodes: [...current.nodes, { id, label: "새 노드" }] }));
  }

  function removeNode(id: string) {
    updateDraft((current) => ({
      ...current,
      nodes: current.nodes.filter((node) => node.id !== id),
      edges: current.edges.filter((edge) => edge.sourceId !== id && edge.targetId !== id),
    }));
    setSelection([]);
    setInlineEdit(null);
  }

  function removeEdge(id: string) {
    updateDraft((current) => ({ ...current, edges: current.edges.filter((edge) => edge.id !== id) }));
    setSelection([]);
    setInlineEdit(null);
  }

  function selectItem(item: DiagramSelection | null, additive: boolean) {
    editorRef.current?.focus({ preventScroll: true });
    setInlineEdit(null);
    setSelection((current) => {
      if (!item) return [];
      if (!additive) return [item];
      const exists = current.some((value) => value.kind === item.kind && value.id === item.id);
      return exists ? current.filter((value) => value.kind !== item.kind || value.id !== item.id) : [...current, item];
    });
  }

  function deleteSelection() {
    if (selection.length === 0) return;
    updateDraft((current) => {
      const requestedNodeIds = new Set(selection.filter((item) => item.kind === "node").map((item) => item.id));
      const nodeIds = requestedNodeIds.size < current.nodes.length ? requestedNodeIds : new Set<string>();
      const edgeIds = new Set(selection.filter((item) => item.kind === "edge").map((item) => item.id));
      return {
        ...current,
        nodes: current.nodes.filter((node) => !nodeIds.has(node.id)),
        edges: current.edges.filter((edge) => !edgeIds.has(edge.id) && !nodeIds.has(edge.sourceId) && !nodeIds.has(edge.targetId)),
      };
    });
    setSelection([]);
    setInlineEdit(null);
  }

  function requestInlineEdit(item: DiagramSelection) {
    const value = item.kind === "node"
      ? draft.nodes.find((node) => node.id === item.id)?.label
      : draft.edges.find((edge) => edge.id === item.id)?.label;
    if (value === undefined) return;
    setSelection([item]);
    setInlineEdit({ ...item, value });
  }

  function commitInlineEdit() {
    if (!inlineEdit) return;
    const value = inlineEdit.value.trim();
    if (inlineEdit.kind === "node" && !value) { setInlineEdit(null); return; }
    updateDraft((current) => inlineEdit.kind === "node"
      ? { ...current, nodes: current.nodes.map((node) => node.id === inlineEdit.id ? { ...node, label: value } : node) }
      : { ...current, edges: current.edges.map((edge) => edge.id === inlineEdit.id ? { ...edge, label: value } : edge) });
    setInlineEdit(null);
  }

  function handleEditorKey(event: KeyboardEvent<HTMLDivElement>) {
    if (!editing || event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement ||
        event.target instanceof HTMLSelectElement || (event.target instanceof HTMLElement && event.target.isContentEditable)) return;
    if (event.key === "Delete") {
      if (selection.length > 0) { event.preventDefault(); deleteSelection(); }
      return;
    }
    if (!(event.ctrlKey || event.metaKey)) return;
    if (event.key.toLowerCase() === "z") {
      event.preventDefault();
      if (event.shiftKey) redo(); else undo();
    } else if (event.key.toLowerCase() === "y") {
      event.preventDefault();
      redo();
    }
  }

  function addEdge() {
    if (draft.nodes.length < 2) return;
    const id = uniqueId("manual_edge", draft.edges.map((edge) => edge.id));
    updateDraft((current) => ({ ...current, edges: [...current.edges, {
      id, sourceId: current.nodes[0].id, targetId: current.nodes[1].id, label: "호출", type: defaultEdgeType(artifact.type),
    }] }));
  }

  function move(kind: "nodes" | "edges", index: number, offset: number) {
    updateDraft((current) => {
      const values = [...current[kind]];
      const destination = index + offset;
      if (destination < 0 || destination >= values.length) return current;
      [values[index], values[destination]] = [values[destination], values[index]];
      return { ...current, [kind]: values };
    });
  }

  async function save() {
    if (draft.nodes.length === 0) { reportError("노드를 하나 이상 남겨야 합니다."); return; }
    setSaving(true);
    try {
      const created = await onSave(editInput(artifact, latestRevision, draft));
      setRevisions((current) => [...current, created]);
      setSelectedRevisionId(created.id);
      const document = toDocument(created.diagram);
      setDraft(document);
      setHistory([document]);
      setHistoryIndex(0);
      setPreview(null);
      setSelection([]);
      setInlineEdit(null);
      setEditing(false);
    } catch (reason) {
      reportError(messageOf(reason, "구조 편집 리비전을 저장하지 못했습니다."));
    } finally { setSaving(false); }
  }

  return <div ref={editorRef} className="structured-diagram-editor" tabIndex={-1} onKeyDown={handleEditorKey}>
    <div className="diagram-meta">
      <span>표시 리비전: v{displayed.version}{selectedRevision ? " · 구조 편집" : " · 생성 원본"}</span>
      <div className="button-row">
        {revisions.length > 0 && <label className="inline-select">리비전<select value={selectedRevisionId} disabled={editing} onChange={(event) => setSelectedRevisionId(event.target.value)}><option value="">생성 원본 v{artifact.version}</option>{revisions.map((revision) => <option key={revision.id} value={revision.id}>편집 v{revision.version}</option>)}</select></label>}
        {!editing && <button type="button" className="secondary" disabled={!editingLatest && revisions.length > 0} onClick={beginEdit}>구조 편집</button>}
      </div>
    </div>
    {editing && <div className="direct-edit-toolbar">
      <button type="button" className="secondary" disabled={historyIndex <= 0} onClick={undo}>실행 취소</button>
      <button type="button" className="secondary" disabled={historyIndex >= history.length - 1} onClick={redo}>다시 실행</button>
      {directEditingAvailable
        ? <span>{selection.length > 0 ? `선택 ${selection.length}개 · 노드 ${selectedNodes.length}개 · 관계 ${selectedEdges.length}개 · 연결 관계 ${connectedEdgeCount}개` : "클릭으로 선택하고 Shift+클릭으로 다중 선택하세요. 더블클릭하면 텍스트를 편집합니다."}</span>
        : <span>이 결과는 DOM 매핑을 확정할 수 없어 아래 구조 목록에서 편집하세요.</span>}
      {selection.length > 0 && <button type="button" className="text-button danger" disabled={selectedNodes.length >= draft.nodes.length && selectedEdges.length === 0} onClick={deleteSelection}>선택 항목 삭제</button>}
    </div>}
    <MermaidPreview source={currentArtifact.mermaidDsl} artifact={currentArtifact} downloadName={`${downloadName}-v${displayed.version}`}
      zoomable={zoomable} interactive={editing} selected={selection} inlineEdit={inlineEdit} onSelect={selectItem} onEditRequest={requestInlineEdit}
      onInlineEditChange={(value) => setInlineEdit((current) => current ? { ...current, value } : null)}
      onInlineEditCommit={commitInlineEdit} onInlineEditCancel={() => setInlineEdit(null)} onInteractionReady={setDirectEditingAvailable} />
    {previewError && <p className="warning">{previewError} 마지막 정상 미리보기를 유지합니다.</p>}
    <details><summary>Mermaid DSL 확인</summary><pre>{currentArtifact.mermaidDsl}</pre></details>
    {editing && <section className="structure-editor-panel">
      <div className="panel-heading"><div><h3>구조 편집</h3><p className="help">추가는 목록에서, 삭제는 다이어그램 또는 목록에서 수행합니다. 저장하면 새 리비전이 생성됩니다.</p></div><div className="button-row"><button type="button" className="secondary" disabled={saving} onClick={cancel}>취소</button><button type="button" className="primary" disabled={saving || draft.nodes.length === 0 || Boolean(previewError)} onClick={() => void save()}>{elapsedLabel("새 리비전 저장", saving, elapsed)}</button></div></div>
      <div className="field-row"><label>제목<input maxLength={200} value={draft.title} onChange={(event) => updateDraft((current) => ({ ...current, title: event.target.value }))} /></label><label>방향<select value={draft.direction ?? "LR"} onChange={(event) => updateDraft((current) => ({ ...current, direction: event.target.value as "LR" | "TB" }))}><option value="LR">가로 (LR)</option><option value="TB">세로 (TB)</option></select></label></div>
      <div className="editor-section-heading"><h4>노드 ({draft.nodes.length})</h4><button type="button" className="secondary" onClick={addNode}>노드 추가</button></div>
      <div className="edit-list">{draft.nodes.map((node, index) => <div className="edit-row" key={node.id}><code>{node.id}</code><textarea aria-label={`${node.id} 이름`} rows={Math.min(6, Math.max(2, node.label.split("\n").length))} maxLength={1000} value={node.label} onChange={(event) => updateDraft((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === node.id ? { ...item, label: event.target.value } : item) }))} /><button type="button" className="text-button" disabled={index === 0} onClick={() => move("nodes", index, -1)}>↑</button><button type="button" className="text-button" disabled={index === draft.nodes.length - 1} onClick={() => move("nodes", index, 1)}>↓</button><button type="button" className="text-button danger" disabled={draft.nodes.length <= 1} onClick={() => removeNode(node.id)}>삭제</button></div>)}</div>
      <div className="editor-section-heading"><h4>관계 ({draft.edges.length})</h4><button type="button" className="secondary" disabled={draft.nodes.length < 2} onClick={addEdge}>관계 추가</button></div>
      <div className="edit-list">{draft.edges.map((edge, index) => <div className="edit-row edge-edit-row" key={edge.id}><select aria-label={`${edge.id} 출발 노드`} value={edge.sourceId} onChange={(event) => updateDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, sourceId: event.target.value } : item) }))}>{nodeOptions}</select><span>→</span><select aria-label={`${edge.id} 도착 노드`} value={edge.targetId} onChange={(event) => updateDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, targetId: event.target.value } : item) }))}>{nodeOptions}</select><input aria-label={`${edge.id} 관계 이름`} maxLength={240} value={edge.label} onChange={(event) => updateDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, label: event.target.value } : item) }))} /><button type="button" className="text-button" disabled={index === 0} onClick={() => move("edges", index, -1)}>↑</button><button type="button" className="text-button" disabled={index === draft.edges.length - 1} onClick={() => move("edges", index, 1)}>↓</button><button type="button" className="text-button danger" onClick={() => removeEdge(edge.id)}>삭제</button></div>)}</div>
    </section>}
  </div>;
}

function editInput(artifact: DiagramArtifact, latest: DiagramRevisionRecord | undefined, document: DiagramEditDocument): EditInput {
  return { rootArtifactId: artifact.id, parentRevisionId: latest?.id, expectedVersion: latest?.version ?? artifact.version, document };
}

function toDocument(artifact: DiagramArtifact): DiagramEditDocument {
  return {
    title: artifact.ir.title,
    direction: artifact.ir.direction === "TB" ? "TB" : "LR",
    nodes: artifact.ir.nodes.map((node) => ({ id: node.id, label: node.label })),
    edges: artifact.ir.edges.map((edge) => ({ id: edge.id, sourceId: edge.sourceId, targetId: edge.targetId, label: edge.label, type: edge.type })),
  };
}

function uniqueId(prefix: string, existing: string[]) {
  const used = new Set(existing);
  let index = used.size + 1;
  while (used.has(`${prefix}_${index}`)) index++;
  return `${prefix}_${index}`;
}

function defaultEdgeType(type: string) {
  return type === "sequence" ? "message" : type === "class" ? "uses" : type === "state" ? "transition" : "flow";
}

function messageOf(reason: unknown, fallback: string) { return reason instanceof Error ? reason.message : fallback; }
