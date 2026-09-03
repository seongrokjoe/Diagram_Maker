import { useEffect, useMemo, useState } from "react";
import { api } from "./api";
import { MermaidPreview } from "./MermaidPreview";
import { elapsedLabel, useElapsedSeconds } from "./useElapsedSeconds";
import type { DiagramArtifact, DiagramEditDocument, DiagramRevisionRecord } from "./types";

export function DiagramEditor({ artifact, downloadName, onSave, reportError }: {
  artifact: DiagramArtifact;
  downloadName: string;
  onSave: (input: { rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument }) => Promise<DiagramRevisionRecord>;
  reportError: (message: string) => void;
}) {
  const [revisions, setRevisions] = useState<DiagramRevisionRecord[]>([]);
  const [selectedRevisionId, setSelectedRevisionId] = useState("");
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [draft, setDraft] = useState<DiagramEditDocument>(() => toDocument(artifact));
  const elapsed = useElapsedSeconds(saving);

  useEffect(() => {
    setRevisions([]);
    setSelectedRevisionId("");
    setEditing(false);
    setDraft(toDocument(artifact));
    void api.listDiagramRevisions(artifact.id).then((items) => {
      setRevisions(items);
      const latest = items.at(-1);
      if (latest) { setSelectedRevisionId(latest.id); setDraft(toDocument(latest.diagram)); }
    }).catch((reason: unknown) => reportError(messageOf(reason, "편집 리비전을 불러오지 못했습니다.")));
  }, [artifact.id, reportError]);

  const selectedRevision = revisions.find((item) => item.id === selectedRevisionId);
  const latestRevision = revisions.at(-1);
  const displayed = selectedRevision?.diagram ?? artifact;
  const editingLatest = !selectedRevisionId || selectedRevisionId === latestRevision?.id;
  const nodeOptions = useMemo(() => draft.nodes.map((node) => <option key={node.id} value={node.id}>{node.label}</option>), [draft.nodes]);

  function beginEdit() {
    const latest = latestRevision?.diagram ?? artifact;
    setSelectedRevisionId(latestRevision?.id ?? "");
    setDraft(toDocument(latest));
    setEditing(true);
  }

  function addNode() {
    const id = uniqueId("manual_node", draft.nodes.map((node) => node.id));
    setDraft((current) => ({ ...current, nodes: [...current.nodes, { id, label: "새 노드" }] }));
  }

  function removeNode(id: string) {
    setDraft((current) => ({
      ...current,
      nodes: current.nodes.filter((node) => node.id !== id),
      edges: current.edges.filter((edge) => edge.sourceId !== id && edge.targetId !== id),
    }));
  }

  function addEdge() {
    if (draft.nodes.length < 2) return;
    const id = uniqueId("manual_edge", draft.edges.map((edge) => edge.id));
    setDraft((current) => ({ ...current, edges: [...current.edges, {
      id, sourceId: current.nodes[0].id, targetId: current.nodes[1].id, label: "호출", type: defaultEdgeType(artifact.type),
    }] }));
  }

  function move(kind: "nodes" | "edges", index: number, offset: number) {
    setDraft((current) => {
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
      const created = await onSave({
        rootArtifactId: artifact.id,
        parentRevisionId: latestRevision?.id,
        expectedVersion: latestRevision?.version ?? artifact.version,
        document: draft,
      });
      setRevisions((current) => [...current, created]);
      setSelectedRevisionId(created.id);
      setDraft(toDocument(created.diagram));
      setEditing(false);
    } catch (reason) {
      reportError(messageOf(reason, "구조 편집 리비전을 저장하지 못했습니다."));
    } finally { setSaving(false); }
  }

  return <div className="structured-diagram-editor">
    <div className="diagram-meta">
      <span>표시 리비전: v{displayed.version}{selectedRevision ? " · 구조 편집" : " · 생성 원본"}</span>
      <div className="button-row">
        {revisions.length > 0 && <label className="inline-select">리비전<select value={selectedRevisionId} disabled={editing} onChange={(event) => setSelectedRevisionId(event.target.value)}><option value="">생성 원본 v{artifact.version}</option>{revisions.map((revision) => <option key={revision.id} value={revision.id}>편집 v{revision.version}</option>)}</select></label>}
        {!editing && <button type="button" className="secondary" disabled={!editingLatest && revisions.length > 0} onClick={beginEdit}>구조 편집</button>}
      </div>
    </div>
    <MermaidPreview source={displayed.mermaidDsl} downloadName={`${downloadName}-v${displayed.version}`} />
    <details><summary>Mermaid DSL 확인</summary><pre>{displayed.mermaidDsl}</pre></details>
    {editing && <section className="structure-editor-panel">
      <div className="panel-heading"><div><h3>구조 편집</h3><p className="help">현재 리비전에서만 노드·관계를 추가, 삭제, 이름 변경 및 순서 조정합니다.</p></div><div className="button-row"><button type="button" className="secondary" disabled={saving} onClick={() => setEditing(false)}>취소</button><button type="button" className="primary" disabled={saving || draft.nodes.length === 0} onClick={() => void save()}>{elapsedLabel("새 리비전 저장", saving, elapsed)}</button></div></div>
      <div className="field-row"><label>제목<input maxLength={200} value={draft.title} onChange={(event) => setDraft((current) => ({ ...current, title: event.target.value }))} /></label><label>방향<select value={draft.direction ?? "LR"} onChange={(event) => setDraft((current) => ({ ...current, direction: event.target.value as "LR" | "TB" }))}><option value="LR">가로 (LR)</option><option value="TB">세로 (TB)</option></select></label></div>
      <div className="editor-section-heading"><h4>노드 ({draft.nodes.length})</h4><button type="button" className="secondary" onClick={addNode}>노드 추가</button></div>
      <div className="edit-list">{draft.nodes.map((node, index) => <div className="edit-row" key={node.id}><code>{node.id}</code><input aria-label={`${node.id} 이름`} maxLength={240} value={node.label} onChange={(event) => setDraft((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === node.id ? { ...item, label: event.target.value } : item) }))} /><button type="button" className="text-button" disabled={index === 0} onClick={() => move("nodes", index, -1)}>↑</button><button type="button" className="text-button" disabled={index === draft.nodes.length - 1} onClick={() => move("nodes", index, 1)}>↓</button><button type="button" className="text-button danger" onClick={() => removeNode(node.id)}>삭제</button></div>)}</div>
      <div className="editor-section-heading"><h4>관계 ({draft.edges.length})</h4><button type="button" className="secondary" disabled={draft.nodes.length < 2} onClick={addEdge}>관계 추가</button></div>
      <div className="edit-list">{draft.edges.map((edge, index) => <div className="edit-row edge-edit-row" key={edge.id}><select aria-label={`${edge.id} 출발 노드`} value={edge.sourceId} onChange={(event) => setDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, sourceId: event.target.value } : item) }))}>{nodeOptions}</select><span>→</span><select aria-label={`${edge.id} 도착 노드`} value={edge.targetId} onChange={(event) => setDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, targetId: event.target.value } : item) }))}>{nodeOptions}</select><input aria-label={`${edge.id} 관계 이름`} maxLength={240} value={edge.label} onChange={(event) => setDraft((current) => ({ ...current, edges: current.edges.map((item) => item.id === edge.id ? { ...item, label: event.target.value } : item) }))} /><button type="button" className="text-button" disabled={index === 0} onClick={() => move("edges", index, -1)}>↑</button><button type="button" className="text-button" disabled={index === draft.edges.length - 1} onClick={() => move("edges", index, 1)}>↓</button><button type="button" className="text-button danger" onClick={() => setDraft((current) => ({ ...current, edges: current.edges.filter((item) => item.id !== edge.id) }))}>삭제</button></div>)}</div>
    </section>}
  </div>;
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
