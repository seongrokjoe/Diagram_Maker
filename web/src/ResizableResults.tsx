import { Children, useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { sidebarWidth } from "./analysisSelection";

export function ResizableResults({ children, preference = "diagram-maker.code-results-width", label = "생성 이력 영역 너비" }: {
  children: ReactNode; preference?: string; label?: string;
}) {
  const root = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; width: number } | null>(null);
  const [width, setWidth] = useState(() => {
    try { return Number(localStorage.getItem(preference)) || 280; } catch { return 280; }
  });
  const [available, setAvailable] = useState(1200);
  const actual = sidebarWidth(width, available);
  useEffect(() => {
    if (!root.current) return;
    const observer = new ResizeObserver(([entry]) => setAvailable(entry.contentRect.width));
    observer.observe(root.current); return () => observer.disconnect();
  }, []);
  useEffect(() => { try { localStorage.setItem(preference, String(width)); } catch { /* Optional browser preference. */ } }, [width, preference]);
  const [sidebar, content] = Children.toArray(children);
  return <div ref={root} className="resizable-results" style={{ "--result-sidebar-width": `${actual}px` } as CSSProperties}>
    {sidebar}
    <div role="separator" aria-label={label} aria-orientation="vertical" tabIndex={0}
      aria-valuemin={220} aria-valuemax={sidebarWidth(560, available)} aria-valuenow={Math.round(actual)}
      title="드래그 또는 좌우 방향키로 조절 · 두 번 클릭하면 기본 너비" className="result-resizer"
      onDoubleClick={() => setWidth(280)}
      onPointerDown={e => { drag.current = { x: e.clientX, width: actual }; e.currentTarget.setPointerCapture(e.pointerId); e.preventDefault(); }}
      onPointerMove={e => { if (drag.current) setWidth(sidebarWidth(drag.current.width + e.clientX - drag.current.x, available)); }}
      onPointerUp={e => { drag.current = null; e.currentTarget.releasePointerCapture(e.pointerId); }}
      onLostPointerCapture={() => { drag.current = null; }}
      onKeyDown={e => {
        const next = e.key === "Home" ? 220 : e.key === "End" ? 560 : e.key === "Enter" ? 280 :
          e.key === "ArrowLeft" ? actual - (e.shiftKey ? 50 : 10) : e.key === "ArrowRight" ? actual + (e.shiftKey ? 50 : 10) : null;
        if (next !== null) { e.preventDefault(); setWidth(sidebarWidth(next, available)); }
      }} />
    {content}
  </div>;
}
