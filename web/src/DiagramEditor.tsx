import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { api } from "./api";
import { MermaidPreview, type DiagramInlineEdit, type DiagramSelection } from "./MermaidPreview";
import { elapsedLabel, useElapsedSeconds } from "./useElapsedSeconds";
import type { DiagramArtifact, DiagramEditDocument, DiagramEditPreview, DiagramRevisionRecord } from "./types";
import { deleteDiagramSelection, memberSelectionId, parseMemberSelectionId } from "./diagramInteraction";
import { DiagramExplanationPanel } from "./DiagramExplanationPanel";
import { DiagramBadge } from "./DiagramBadge";
import { originOf, diagramName, type DiagramVariant } from "./diagramOrigin";
import { withViewDirection, type ViewDirection } from "./diagramViewSettings";

type EditInput = { rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument };
export function DiagramEditor({ artifact, downloadName, variant, zoomable = false, onSave, onPreview, onOpenDetail, reportError, showExplanation = false, onEvidence, collapsibleExplanation = false, canvasToolbar = false }: {
  variant?: DiagramVariant;
  collapsibleExplanation?: boolean;
  canvasToolbar?: boolean;
  showExplanation?: boolean;
  onEvidence?: (id: string) => void;
  artifact: DiagramArtifact;
  downloadName: string;
  zoomable?: boolean;
  onSave: (input: EditInput) => Promise<DiagramRevisionRecord>;
  onPreview: (input: EditInput, signal: AbortSignal) => Promise<DiagramEditPreview>;
  reportError: (message: string) => void;
  onOpenDetail?: (pageId: string) => void;
}) {
  const [revisions, setRevisions] = useState<DiagramRevisionRecord[]>([]);
  const [loadedArtifactId, setLoadedArtifactId] = useState<string | null>(null);
  const loadingRevisions = loadedArtifactId !== artifact.id;
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
  const [viewDirection, setViewDirection] = useState<ViewDirection>("original");
  const editorRef = useRef<HTMLDivElement>(null);
  const previewRequest = useRef(onPreview);
  previewRequest.current = onPreview;
  const elapsed = useElapsedSeconds(saving);

  useEffect(() => {
    let active = true;
    const original = toDocument(artifact);
    setLoadedArtifactId(null);
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
      if (!active) return;
      setRevisions(items);
      const latest = items.at(-1);
      if (latest) {
        const document = toDocument(latest.diagram);
        setSelectedRevisionId(latest.id);
        setDraft(document);
        setHistory([document]);
      }
    }).catch((reason: unknown) => { if (active) reportError(messageOf(reason, "편집 리비전을 불러오지 못했습니다.")); })
      .finally(() => { if (active) setLoadedArtifactId(artifact.id); });
    return () => { active = false; };
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
  const deletableSelection = selection.filter(item => item.kind === "node" || item.kind === "edge");

  useEffect(() => {
    if (!editing || draft.nodes.length === 0) { setPreviewError(""); return; }
    if (JSON.stringify(draft) === JSON.stringify(toDocument(displayed))) {
      setPreview(displayed);
      setPreviewError("");
      return;
    }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void previewRequest.current(editInput(artifact, latestRevision, draft), controller.signal)
        .then((result) => {
          if (controller.signal.aborted) return;
          setPreview({ ...displayed, version: result.version, ir: result.ir, mermaidDsl: result.mermaidDsl });
          setPreviewError("");
        })
        .catch((reason: unknown) => {
          if (controller.signal.aborted) return;
          setPreviewError(messageOf(reason, "미리보기를 생성하지 못했습니다."));
        });
    }, 250);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [artifact, displayed, draft, editing, latestRevision]);

  function beginEdit() {
    if (loadingRevisions) return;
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
    if (deletableSelection.length === 0) return;
    updateDraft((current) => deleteDiagramSelection(current, deletableSelection));
    setSelection([]);
    setInlineEdit(null);
  }

  function requestInlineEdit(item: DiagramSelection) {
    const member = item.kind === "member" ? parseMemberSelectionId(item.id) : null;
    const value = item.kind === "node" ? draft.nodes.find((node) => node.id === item.id)?.label
      : item.kind === "edge" ? draft.edges.find((edge) => edge.id === item.id)?.label
      : item.kind === "annotation" ? draft.sequenceAnnotations?.find(annotation => annotation.id === item.id)?.label
      : member ? draft.nodes.find(node => node.id === member.nodeId)?.details?.[member.index] : undefined;
    if (value === undefined) return;
    setSelection([item]);
    setInlineEdit({ ...item, value });
  }

  function commitInlineEdit() {
    if (!inlineEdit) return;
    const value = inlineEdit.value.trim();
    if (!value && inlineEdit.kind !== "edge") { setInlineEdit(null); return; }
    updateDraft((current) => {
      if (inlineEdit.kind === "node")
        return { ...current, nodes: current.nodes.map(node => node.id === inlineEdit.id ? { ...node, label: value } : node) };
      if (inlineEdit.kind === "edge")
        return { ...current, edges: current.edges.map(edge => edge.id === inlineEdit.id ? { ...edge, label: value } : edge) };
      if (inlineEdit.kind === "annotation")
        return { ...current, sequenceAnnotations: current.sequenceAnnotations?.map(annotation =>
          annotation.id === inlineEdit.id ? { ...annotation, label: value } : annotation) };
      const member = parseMemberSelectionId(inlineEdit.id);
      return member ? { ...current, nodes: current.nodes.map(node => node.id === member.nodeId
        ? { ...node, details: (node.details ?? []).map((detail, index) => index === member.index ? value : detail) } : node) } : current;
    });
    setInlineEdit(null);
  }

  function handleEditorKey(event: KeyboardEvent<HTMLDivElement>) {
    if (!editing || event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement ||
        event.target instanceof HTMLSelectElement || (event.target instanceof HTMLElement && event.target.isContentEditable)) return;
    if (event.key === "Delete" || event.key === "Backspace") {
      if (deletableSelection.length > 0) { event.preventDefault(); deleteSelection(); }
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

  return <div ref={editorRef} className="structured-diagram-editor" aria-busy={loadingRevisions} tabIndex={-1} onKeyDown={handleEditorKey}>
    <div className="diagram-heading"><DiagramBadge kind={variant ?? originOf(artifact)} /><h3>{diagramName(artifact.ir.title, variant ?? originOf(artifact))}</h3></div>
    <div className="diagram-meta">
      <span>표시 리비전: v{displayed.version}{selectedRevision ? " · 구조 편집" : " · 생성 원본"}</span>
      <div className="button-row">
        {revisions.length > 0 && <label className="inline-select">리비전<select value={selectedRevisionId} disabled={editing} onChange={(event) => setSelectedRevisionId(event.target.value)}><option value="">생성 원본 v{artifact.version}</option>{revisions.map((revision) => <option key={revision.id} value={revision.id}>편집 v{revision.version}</option>)}</select></label>}
        {!editing && !canvasToolbar && <button type="button" className="secondary" disabled={loadingRevisions || (!editingLatest && revisions.length > 0)} onClick={beginEdit}>구조 편집</button>}
      </div>
    </div>
    {editing && <div className="direct-edit-toolbar">
      <button type="button" className="secondary" disabled={historyIndex <= 0} onClick={undo}>실행 취소</button>
      <button type="button" className="secondary" disabled={historyIndex >= history.length - 1} onClick={redo}>다시 실행</button>
      {directEditingAvailable
        ? <span>{selection.length > 0 ? `선택 ${selection.length}개 · 노드 ${selectedNodes.length}개 · 관계 ${selectedEdges.length}개 · 연결 관계 ${connectedEdgeCount}개` : "클릭으로 선택하고 Shift+클릭으로 다중 선택하세요. 더블클릭하면 텍스트를 편집합니다."}</span>
        : <span>이 결과는 DOM 매핑을 확정할 수 없어 아래 구조 목록에서 편집하세요.</span>}
      {deletableSelection.length > 0 && <button type="button" className="text-button danger" onClick={deleteSelection}>선택 항목 삭제</button>}
    </div>}
    {onOpenDetail && selection.filter(item => item.kind === "node").map(item => currentArtifact.ir.nodes.find(node => node.id === item.id))
      .filter(node => node?.detailPageId).map(node => <button type="button" className="secondary" key={node!.id}
        onClick={() => onOpenDetail(node!.detailPageId!)}>{node!.label} · 세부 보기</button>)}
    {editing && draft.nodes.length === 0 ? <div className="empty-state">모든 노드를 삭제했습니다. 실행 취소하거나 새 노드를 추가하세요. 저장하려면 노드가 하나 이상 필요합니다.</div> : <MermaidPreview source={withViewDirection(currentArtifact.mermaidDsl, viewDirection, currentArtifact.type)} artifact={currentArtifact} downloadName={`${downloadName}-v${displayed.version}`}
      toolbarContent={<><div className="diagram-direction-controls"><label className="inline-select">보기 방향<select aria-label="보기 방향" disabled={artifact.type === "sequence"} value={viewDirection} onChange={e => setViewDirection(e.target.value as ViewDirection)}>
          <option value="original">원본</option><option value="LR">가로</option><option value="TB">세로</option></select></label>{artifact.type === "sequence" && <span className="help">시퀀스는 세로 시간순</span>}</div>
        {canvasToolbar && !editing && <button className="secondary" disabled={loadingRevisions || (!editingLatest && revisions.length > 0)} onClick={beginEdit}>구조 편집</button>}</>}
      fitLabel={canvasToolbar ? "맞춤 보기" : undefined}
      zoomable={zoomable} interactive={editing || Boolean(onOpenDetail)} selected={selection} inlineEdit={inlineEdit} onSelect={selectItem} onEditRequest={editing ? requestInlineEdit : undefined}
      editMode={editing}
      onInlineEditChange={(value) => setInlineEdit((current) => current ? { ...current, value } : null)}
      onInlineEditCommit={commitInlineEdit} onInlineEditCancel={() => setInlineEdit(null)} onInteractionReady={setDirectEditingAvailable} />}
    {onEvidence && selection.map(item => {
      const ids = item.kind === "node" ? currentArtifact.ir.nodes.find(n => n.id === item.id)?.evidenceIds : currentArtifact.ir.edges.find(e => e.id === item.id)?.evidenceIds;
      return ids?.map((id, index) => <button key={`${item.id}-${id}`} className="secondary" onClick={() => onEvidence(id)}>선택 단계 원본 근거 {index + 1}</button>);
    })}
    {showExplanation && (collapsibleExplanation ? <details className="code-block-explanation"><summary>동작 설명과 원본 근거</summary>
      <DiagramExplanationPanel explanation={artifact.explanation} diagram={artifact.ir} edited={editing || Boolean(selectedRevision)} onEvidence={onEvidence} />
    </details> : <DiagramExplanationPanel explanation={artifact.explanation} diagram={artifact.ir} edited={editing || Boolean(selectedRevision)} onEvidence={onEvidence} />)}
    {previewError && <p className="warning">{previewError} 마지막 정상 미리보기를 유지합니다.</p>}
    <details><summary>Mermaid DSL 확인</summary><pre>{currentArtifact.mermaidDsl}</pre></details>
    {editing && <section className="structure-editor-panel">
      <div className="panel-heading"><div><h3>구조 편집</h3><p className="help">추가는 목록에서, 삭제는 다이어그램 또는 목록에서 수행합니다. 저장하면 새 리비전이 생성됩니다.</p></div><div className="button-row"><button type="button" className="secondary" disabled={saving} onClick={cancel}>취소</button><button type="button" className="primary" disabled={saving || draft.nodes.length === 0 || Boolean(previewError)} onClick={() => void save()}>{elapsedLabel("새 리비전 저장", saving, elapsed)}</button></div></div>
      <div className="field-row"><label>제목<input maxLength={200} value={draft.title} onChange={(event) => updateDraft((current) => ({ ...current, title: event.target.value }))} /></label></div>
      <StructureEditorSection title={`노드 (${draft.nodes.length})`}><div className="editor-section-heading"><button type="button" className="secondary" onClick={addNode}>노드 추가</button></div>
      <div className="edit-list">{draft.nodes.map((node, index) => <div className="edit-row" key={node.id}><input aria-label={`노드 ${index + 1} 이름`} maxLength={1000} value={node.label} onChange={(event) => updateDraft((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === node.id ? { ...item, label: event.target.value } : item) }))} /><button type="button" className="text-button" disabled={index === 0} onClick={() => move("nodes", index, -1)}>↑</button><button type="button" className="text-button" disabled={index === draft.nodes.length - 1} onClick={() => move("nodes", index, 1)}>↓</button><button type="button" className="text-button danger" onClick={() => removeNode(node.id)}>삭제</button></div>)}</div>
      </StructureEditorSection>
      {artifact.type === "class" && <StructureEditorSection title={`클래스 멤버 (${draft.nodes.reduce((count, node) => count + (node.details?.length ?? 0), 0)})`}>
        <div className="edit-list">{draft.nodes.map(node => <div className="class-member-group" key={node.id}><div className="editor-section-heading"><strong>{node.label}</strong><button type="button" className="secondary" onClick={() => updateDraft(current => ({ ...current, nodes: current.nodes.map(item => item.id === node.id ? { ...item, details: [...(item.details ?? []), "+newMember() void"] } : item) }))}>멤버 추가</button></div>
          {(node.details ?? []).map((detail, index) => <div className="edit-row member-edit-row" key={memberSelectionId(node.id, index)}><input aria-label={`${node.label} 멤버 ${index + 1}`} maxLength={500} value={detail} onChange={event => updateDraft(current => ({ ...current, nodes: current.nodes.map(item => item.id === node.id ? { ...item, details: (item.details ?? []).map((value, at) => at === index ? event.target.value : value) } : item) }))} /><button type="button" className="text-button danger" onClick={() => updateDraft(current => ({ ...current, nodes: current.nodes.map(item => item.id === node.id ? { ...item, details: (item.details ?? []).filter((_, at) => at !== index) } : item) }))}>삭제</button></div>)}
        </div>)}</div>
      </StructureEditorSection>}
      {draft.sequenceAnnotations && draft.sequenceAnnotations.length > 0 && <StructureEditorSection title={`시퀀스 조건·메모 (${draft.sequenceAnnotations.length})`}><div className="edit-list">{draft.sequenceAnnotations.map((annotation, index) => <div className="edit-row annotation-edit-row" key={annotation.id}><span>{sequenceAnnotationName(annotation.kind)}</span><input aria-label={`시퀀스 조건 메모 ${index + 1}`} maxLength={1000} value={annotation.label} onChange={event => updateDraft(current => ({ ...current, sequenceAnnotations: current.sequenceAnnotations?.map(item => item.id === annotation.id ? { ...item, label: event.target.value } : item) }))} /></div>)}</div></StructureEditorSection>}
      <StructureEditorSection title={`관계 (${draft.edges.length})`}><div className="editor-section-heading"><button type="button" className="secondary" disabled={draft.nodes.length < 2} onClick={addEdge}>관계 추가</button></div>
      <div className="edit-list">{draft.edges.map((edge, index) => <div className="edit-row edge-edit-row" key={edge.id}><select aria-label={`관계 ${index + 1} 출발 노드`} value={edge.sourceId} onChange={(event) => updateDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, sourceId: event.target.value } : item) }))}>{nodeOptions}</select><span>→</span><select aria-label={`관계 ${index + 1} 도착 노드`} value={edge.targetId} onChange={(event) => updateDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, targetId: event.target.value } : item) }))}>{nodeOptions}</select><input aria-label={`관계 ${index + 1} 이름`} maxLength={240} value={edge.label} onChange={(event) => updateDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, label: event.target.value } : item) }))} /><button type="button" className="text-button" disabled={Boolean(currentArtifact.ir.sequenceBlocks) || index === 0} onClick={() => move("edges", index, -1)}>↑</button><button type="button" className="text-button" disabled={Boolean(currentArtifact.ir.sequenceBlocks) || index === draft.edges.length - 1} onClick={() => move("edges", index, 1)}>↓</button><button type="button" className="text-button danger" onClick={() => removeEdge(edge.id)}>삭제</button></div>)}</div>
    </StructureEditorSection></section>}
  </div>;
}

function StructureEditorSection({ title, children }: { title: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  // Collapsed relations can otherwise mount thousands of node options and stall edits.
  return <details className="structure-editor-section" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>{title}</summary>{open && children}
  </details>;
}

function editInput(artifact: DiagramArtifact, latest: DiagramRevisionRecord | undefined, document: DiagramEditDocument): EditInput {
  return { rootArtifactId: artifact.id, parentRevisionId: latest?.id, expectedVersion: latest?.version ?? artifact.version, document };
}

function toDocument(artifact: DiagramArtifact): DiagramEditDocument {
  return {
    title: artifact.ir.title,
    direction: artifact.ir.direction === "TB" ? "TB" : "LR",
    nodes: artifact.ir.nodes.map((node) => ({ id: node.id, label: node.label, details: node.details ? [...node.details] : undefined })),
    edges: artifact.ir.edges.map((edge) => ({ id: edge.id, sourceId: edge.sourceId, targetId: edge.targetId, label: edge.label, type: edge.type })),
    sequenceAnnotations: editableSequenceAnnotations(artifact.ir.sequenceBlocks ?? []),
  };
}

function editableSequenceAnnotations(blocks: NonNullable<DiagramArtifact["ir"]["sequenceBlocks"]>): NonNullable<DiagramEditDocument["sequenceAnnotations"]> {
  return blocks.flatMap(block => [
    ...(block.kind === "alt" || block.kind === "loop" || block.kind === "break" || block.kind === "opt" || block.kind === "note" || block.kind === "scenario"
      ? [{ id: block.id, kind: block.kind, label: block.label } as NonNullable<DiagramEditDocument["sequenceAnnotations"]>[number]] : []),
    ...editableSequenceAnnotations(block.children),
  ]);
}

function sequenceAnnotationName(kind: string) {
  return kind === "note" || kind === "scenario" ? "메모" : kind === "loop" ? "반복 조건" : kind === "break" ? "중단 조건" : "분기 조건";
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
