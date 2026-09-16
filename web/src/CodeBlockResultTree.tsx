import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import type { CodeBlockRun } from "./codeBlockTypes";
import { isSemanticPage, typeLabels, type CodeBlockLocationSelection } from "./codeBlockWorkspaceState";
import { DiagramBadge } from "./DiagramBadge";
import { baseDiagramName, matchesDiagramName, type DiagramVariant } from "./diagramOrigin";

type Row = { id: string; parent?: string; label: string; level: number; branch?: boolean; active?: boolean; location?: CodeBlockLocationSelection; kind?: DiagramVariant; status?: boolean };
export function CodeBlockResultTree({ run, active, showStatic, onSelect, query = "" }: {
  run: Pick<CodeBlockRun, "id" | "results">; active: CodeBlockLocationSelection | null; showStatic: boolean; onSelect: (value: CodeBlockLocationSelection) => void; query?: string;
}) {
  const storageKey = `code-block-tree:${run.id}`;
  const [closed, setClosed] = useState<string[]>(() => { try { return JSON.parse(sessionStorage.getItem(storageKey) ?? "[]"); } catch { return []; } });
  const [focusId, setFocusId] = useState("");
  const tree = useRef<HTMLDivElement>(null);
  useEffect(() => { try { sessionStorage.setItem(storageKey, JSON.stringify(closed)); } catch { /* Storage can be unavailable. */ } }, [closed, storageKey]);
  useEffect(() => {
    if (!active) return;
    const type = run.results.find(g => g.groupId === active.group)?.views.find(v => v.viewId === active.view)?.selection.diagramType;
    const viewId = `view:${active.group}/${active.view}`;
    setClosed(values => values.filter(id => id !== `type:${type}` && id !== viewId && id !== `${viewId}:${active.variant ?? "ai"}`));
  }, [active?.group, active?.view, active?.page, active?.variant]);
  const rows: Row[] = [];
  for (const [type, label] of typeLabels) {
    const items = run.results.flatMap(g => g.views.filter(v => v.selection.diagramType === type && (!query.trim() || v.pages.some(p => matchesDiagramName(p.title, query)))).map(v => ({ g, v })));
    if (!items.length) continue;
    const parent = `type:${type}`;
    rows.push({ id: parent, label, level: 1, branch: true });
    if (!query.trim() && closed.includes(parent)) continue;
    for (const { g, v } of items) {
      const id = `view:${g.groupId}/${v.viewId}`;
      rows.push({ id, parent, label: g.title, level: 2, branch: true });
      if (!query.trim() && closed.includes(id)) continue;
      for (const kind of ["ai", ...(showStatic ? ["code"] : [])] as DiagramVariant[]) {
        const pages = v.pages.filter(p => matchesDiagramName(p.title, query) && (kind === "ai" ? isSemanticPage(v, p) : Boolean(p.codeArtifactId) || !isSemanticPage(v, p)));
        const showAiStatus = kind === "ai" && v.state !== "Completed";
        if (!pages.length && !showAiStatus) continue;
        const category = `${id}:${kind}`;
        rows.push({ id: category, parent: id, label: kind === "ai" ? "AI 다이어그램" : "Code 다이어그램", level: 3, branch: true });
        if (!query.trim() && closed.includes(category)) continue;
        for (const p of pages) rows.push({
          id: `${id}:${p.id}:${kind}`, parent: category, level: 4, kind,
          label: `${baseDiagramName(p.title)}${kind === "ai" && p.aiState === "Partial" ? " · 부분 완료" : ""}`,
          active: active?.group === g.groupId && active.view === v.viewId && active.page === p.id && (active.variant ?? (isSemanticPage(v, p) ? "ai" : "code")) === kind,
          location: { group: g.groupId, view: v.viewId, page: p.id, variant: kind }
        });
        if (showAiStatus) rows.push({ id: `${id}:ai-status`, parent: category, level: 4,
          label: v.reused && v.pages.some(p => isSemanticPage(v, p)) ? "최신 생성 실패 · 이전 AI 유지" :
            pages.some(p => p.aiState === "Partial") ? "검증된 설명은 유지 · 나머지 AI 실패" : "AI 생성 미완료",
          status: true, active: active?.group === g.groupId && active.view === v.viewId && active.page === "",
          location: { group: g.groupId, view: v.viewId, page: "", variant: "ai" } });
      }
    }
  }
  const tabId = rows.some(r => r.id === focusId) ? focusId : rows.find(r => r.active)?.id ?? rows[0]?.id;
  function toggle(id: string) { setClosed(values => values.includes(id) ? values.filter(v => v !== id) : [...values, id]); }
  function focus(id?: string) { if (!id) return; setFocusId(id); tree.current?.querySelectorAll<HTMLElement>('[role="treeitem"]').forEach(e => { if (e.dataset.rowId === id) e.focus(); }); }
  function key(event: KeyboardEvent, row: Row, index: number) {
    switch (event.key) {
      case "ArrowDown": focus(rows[index + 1]?.id); break;
      case "ArrowUp": focus(rows[index - 1]?.id); break;
      case "Home": focus(rows[0]?.id); break;
      case "End": focus(rows.at(-1)?.id); break;
      case "ArrowRight": if (row.branch && closed.includes(row.id)) toggle(row.id); else if (row.branch) focus(rows[index + 1]?.id); break;
      case "ArrowLeft": if (row.branch && !closed.includes(row.id)) toggle(row.id); else focus(row.parent); break;
      case "Enter": case " ": if (row.branch) toggle(row.id); else if (row.location) onSelect(row.location); break;
      default: return;
    }
    event.preventDefault();
  }
  return <div ref={tree} role="tree" aria-label="다이어그램 결과 트리" className="code-block-result-tree">{rows.map((row, index) =>
    <div key={row.id} role="treeitem" aria-level={row.level} aria-expanded={row.branch ? Boolean(query.trim()) || !closed.includes(row.id) : undefined}
      aria-selected={row.branch ? undefined : Boolean(row.active)} tabIndex={row.id === tabId ? 0 : -1} data-row-id={row.id}
      style={{ paddingLeft: `${(row.level - 1) * 16 + 8}px` }} onFocus={() => setFocusId(row.id)} onKeyDown={e => key(e, row, index)}
      onClick={() => { setFocusId(row.id); if (row.branch) toggle(row.id); else if (row.location) onSelect(row.location); }}>
      <span className="tree-marker" aria-hidden="true">{row.branch ? !query.trim() && closed.includes(row.id) ? "▸" : "▾" : ""}</span>{row.kind && <DiagramBadge kind={row.kind} />}<span className={`tree-label ${row.status ? "tree-status" : ""}`}>{row.label}</span>
    </div>)}</div>;
}
