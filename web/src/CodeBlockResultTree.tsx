import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import type { CodeBlockRun } from "./codeBlockTypes";
import { isSemanticPage, typeLabels, type CodeBlockLocationSelection } from "./codeBlockWorkspaceState";

type Row = { id: string; parent?: string; label: string; level: number; branch?: boolean; active?: boolean; location?: CodeBlockLocationSelection };
export function CodeBlockResultTree({ run, active, showStatic, onSelect }: {
  run: CodeBlockRun; active: CodeBlockLocationSelection | null; showStatic: boolean; onSelect: (value: CodeBlockLocationSelection) => void;
}) {
  const storageKey = `code-block-tree:${run.id}`;
  const [closed, setClosed] = useState<string[]>(() => { try { return JSON.parse(sessionStorage.getItem(storageKey) ?? "[]"); } catch { return []; } });
  const [focusId, setFocusId] = useState("");
  const tree = useRef<HTMLDivElement>(null);
  useEffect(() => { try { sessionStorage.setItem(storageKey, JSON.stringify(closed)); } catch { /* Storage can be unavailable. */ } }, [closed, storageKey]);
  useEffect(() => {
    if (!active) return;
    const type = run.results.find(g => g.groupId === active.group)?.views.find(v => v.viewId === active.view)?.selection.diagramType;
    setClosed(values => values.filter(id => id !== `type:${type}` && id !== `view:${active.view}`));
  }, [active?.group, active?.view, active?.page]);
  const rows: Row[] = [];
  for (const [type, label] of typeLabels) {
    const items = run.results.flatMap(g => g.views.filter(v => v.selection.diagramType === type).map(v => ({ g, v })));
    if (!items.length) continue;
    const parent = `type:${type}`;
    rows.push({ id: parent, label, level: 1, branch: true });
    if (closed.includes(parent)) continue;
    for (const { g, v } of items) {
      const id = `view:${v.viewId}`;
      rows.push({ id, parent, label: g.title, level: 2, branch: true });
      if (closed.includes(id)) continue;
      for (const p of v.pages.filter(p => showStatic || isSemanticPage(v, p))) rows.push({
        id: `${v.viewId}:${p.id}`, parent: id, level: 3,
        label: `${isSemanticPage(v, p) ? (p.level ?? (p.id === "overview" ? "summary" : "detail")) === "detail" ? "상세" : "요약" : "정적 구조"} · ${p.title}`,
        active: active?.group === g.groupId && active.view === v.viewId && active.page === p.id,
        location: { group: g.groupId, view: v.viewId, page: p.id }
      });
      if (v.state !== "Completed") rows.push({ id: `${v.viewId}:failure`, parent: id, level: 3,
        label: v.reused && v.pages.some(p => isSemanticPage(v, p)) ? "최신 생성 실패 · 이전 성공 유지" : "의미 생성 미완료",
        active: active?.view === v.viewId && active.page === "", location: { group: g.groupId, view: v.viewId, page: "" } });
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
    <div key={row.id} role="treeitem" aria-level={row.level} aria-expanded={row.branch ? !closed.includes(row.id) : undefined}
      aria-selected={row.branch ? undefined : Boolean(row.active)} tabIndex={row.id === tabId ? 0 : -1} data-row-id={row.id}
      style={{ paddingLeft: `${(row.level - 1) * 16 + 8}px` }} onFocus={() => setFocusId(row.id)} onKeyDown={e => key(e, row, index)}
      onClick={() => { setFocusId(row.id); if (row.branch) toggle(row.id); else if (row.location) onSelect(row.location); }}>
      <span aria-hidden="true">{row.branch ? closed.includes(row.id) ? "▸ " : "▾ " : ""}</span>{row.label}
    </div>)}</div>;
}
