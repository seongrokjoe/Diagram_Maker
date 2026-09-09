import { useEffect, useId, useRef, useState, type CSSProperties, type MouseEvent, type ReactNode } from "react";
import type { DiagramArtifact } from "./types";
import { clampZoom, renderAlias as alias, renderElementMap, zoomScrollDelta } from "./diagramInteraction";
import { mermaidSafetyError } from "./mermaidSafety";
import { prepareMermaidDisplay } from "./mermaidDisplay";

export type DiagramSelection = { kind: "node" | "edge"; id: string };
export type DiagramInlineEdit = DiagramSelection & { value: string };

type MermaidApi = {
  initialize: (configuration: Record<string, unknown>) => void;
  parse: (source: string, options?: { suppressErrors?: boolean }) => Promise<unknown>;
  render: (id: string, source: string) => Promise<{ svg: string }>;
};
let mermaidPromise: Promise<MermaidApi> | undefined;
let renderQueue: Promise<void> = Promise.resolve();
const emptySelections: DiagramSelection[] = [];

function loadMermaid(): Promise<MermaidApi> {
  mermaidPromise ??= new Promise<MermaidApi>((resolve, reject) => {
    const existing = (window as Window & { mermaid?: MermaidApi }).mermaid;
    if (existing) { resolve(existing); return; }
    const script = document.createElement("script");
    script.src = "/vendor/mermaid.min.js";
    script.async = true;
    script.onload = () => {
      const mermaid = (window as Window & { mermaid?: MermaidApi }).mermaid;
      if (!mermaid) { reject(new Error("Mermaid runtime did not initialize.")); return; }
      mermaid.initialize({ startOnLoad: false, securityLevel: "strict", htmlLabels: false, maxEdges: 500, maxTextSize: 50_000, theme: "base", themeVariables: { primaryColor: "#e7f0ff", primaryTextColor: "#10213a", primaryBorderColor: "#4b72a9", lineColor: "#52709a", fontFamily: "Arial, sans-serif" } });
      resolve(mermaid);
    };
    script.onerror = () => reject(new Error("Mermaid runtime could not be loaded."));
    document.head.appendChild(script);
  });
  return mermaidPromise;
}

function sanitizeSvg(svg: string, displayMarker = ""): string {
  const parser = new DOMParser();
  const document = parser.parseFromString(svg, "image/svg+xml");
  document.querySelectorAll("script, foreignObject, iframe, object, embed, image, animate, animateMotion, animateTransform, set").forEach((element) => element.remove());
  // Exported SVG has no application CSP. Remove CSS imports, escaped tokens and
  // every URL except local fragment references before rendering or downloading.
  document.querySelectorAll("style").forEach(element => {
    const css = element.textContent ?? "";
    if (/[@\\]/.test(css) || [...css.matchAll(/url\(([^)]+)\)/gi)]
      .some(([, ref]) => !ref.trim().replace(/^['"]|['"]$/g, "").startsWith("#"))) element.remove();
  });
  // Some renderers auto-link ordinary URL text. Keep the visible label while
  // discarding the link element and all of its navigation behavior.
  document.querySelectorAll("a").forEach(element => element.replaceWith(...element.childNodes));
  if (displayMarker) {
    const text = document.createTreeWalker(document.documentElement, NodeFilter.SHOW_TEXT);
    while (text.nextNode()) text.currentNode.nodeValue = text.currentNode.nodeValue?.split(displayMarker).join("") ?? "";
  }
  document.querySelectorAll("*").forEach((element) => {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim().toLowerCase();
      const unsafeReference = (name === "href" || name === "xlink:href") && !value.startsWith("#");
      const unsafeUrl = [...value.matchAll(/url\(([^)]+)\)/g)]
        .some(([, reference]) => !reference.trim().replace(/^['"]|['"]$/g, "").startsWith("#"));
      if (name.startsWith("on") || unsafeReference || value.startsWith("javascript:") || unsafeUrl ||
        (name === "style" && /[@\\]/.test(value))) element.removeAttribute(attribute.name);
    }
  });
  return new XMLSerializer().serializeToString(document.documentElement);
}

function renderMermaid(mermaid: MermaidApi, id: string, source: string): Promise<{ svg: string }> {
  const run = renderQueue.then(async () => {
    try {
      const parsed = await mermaid.parse(source, { suppressErrors: true });
      if (parsed === false) throw new Error("Invalid Mermaid syntax.");
      return await mermaid.render(id, source);
    } finally {
      document.getElementById(`d${id}`)?.remove();
      document.querySelectorAll(`[data-mermaid-id="${id}"]`).forEach((element) => element.remove());
    }
  });
  renderQueue = run.then(() => undefined, () => undefined);
  return run;
}

type MermaidPreviewProps = {
  toolbarContent?: ReactNode;
  fitLabel?: string;
  source: string;
  artifact?: DiagramArtifact;
  downloadName?: string;
  editable?: boolean;
  compact?: boolean;
  zoomable?: boolean;
  interactive?: boolean;
  selected?: DiagramSelection[];
  inlineEdit?: DiagramInlineEdit | null;
  onSelect?: (selection: DiagramSelection | null, additive: boolean) => void;
  onEditRequest?: (selection: DiagramSelection) => void;
  onInlineEditChange?: (value: string) => void;
  onInlineEditCommit?: () => void;
  onInlineEditCancel?: () => void;
  onInteractionReady?: (available: boolean) => void;
  onSaveRevision?: (source: string) => Promise<void>;
};

export function MermaidPreview({ source, artifact, downloadName = "diagram", editable = false, compact = false,
  zoomable = false, interactive = false, selected = emptySelections, inlineEdit, onSelect, onEditRequest, onInlineEditChange,
  onInlineEditCommit, onInlineEditCancel, onInteractionReady, onSaveRevision, toolbarContent, fitLabel = "100%로 초기화" }: MermaidPreviewProps) {
  const id = useId().replaceAll(":", "_");
  const canvasRef = useRef<HTMLDivElement>(null);
  const [draft, setDraft] = useState(source);
  const [svg, setSvg] = useState("");
  const [error, setError] = useState("");
  const [rendering, setRendering] = useState(true);
  const [saving, setSaving] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [baseSize, setBaseSize] = useState({ width: 800, height: 500 });
  const [editAnchor, setEditAnchor] = useState<{ left: number; top: number; width: number; height: number } | null>(null);
  const interactionReady = useRef(onInteractionReady);
  interactionReady.current = onInteractionReady;

  useEffect(() => setDraft(source), [source]);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const handle = (event: WheelEvent) => zoomDiagram(event);
    canvas.addEventListener("wheel", handle, { passive: false });
    return () => canvas.removeEventListener("wheel", handle);
  }, [zoom, zoomable, compact, svg]);
  useEffect(() => { setZoom(1); setEditAnchor(null); }, [artifact?.id]);
  useEffect(() => {
    canvasRef.current?.querySelectorAll("[data-ir-id]").forEach(element => {
      element.classList.toggle("ir-selected", selected.some(item => item.id === element.getAttribute("data-ir-id") && item.kind === element.getAttribute("data-ir-kind")));
    });
  }, [selected, svg]);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !svg) return;
    const observer = new ResizeObserver(() => setBaseSize(fittedSvgSize(svg, canvas, compact)));
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [svg, compact]);

  useEffect(() => {
    let active = true;
    setRendering(true);
    const timer = window.setTimeout(() => {
      const renderSource = editable ? draft : source;
      const blocked = mermaidSafetyError(renderSource);
      if (blocked) {
        setError(blocked);
        interactionReady.current?.(false);
        setRendering(false);
        return;
      }
      const renderId = `diagram_${id}_${Date.now()}`;
      const display = prepareMermaidDisplay(renderSource);
      void loadMermaid()
        .then((mermaid) => renderMermaid(mermaid, renderId, display.source))
        .then((result) => {
          if (!active) return;
          const decorated = decorateSvg(sanitizeSvg(result.svg, display.marker), artifact, []);
          setSvg(decorated.svg);
          setBaseSize(fittedSvgSize(decorated.svg, canvasRef.current, compact));
          interactionReady.current?.(interactive && decorated.mappingComplete);
          setError("");
        })
        .catch(() => {
          if (!active) return;
          setError("다이어그램 문법을 렌더링할 수 없습니다. 마지막 정상 미리보기를 유지합니다.");
        })
        .finally(() => { if (active) setRendering(false); });
    }, editable ? 350 : 0);
    return () => { active = false; window.clearTimeout(timer); };
  }, [artifact?.id, artifact?.version, draft, editable, id, interactive, source]);

  function selectionFromTarget(target: EventTarget | null): { selection: DiagramSelection; element: Element } | null {
    const element = target instanceof Element ? target.closest("[data-ir-id]") : null;
    if (!element || !canvasRef.current?.contains(element)) return null;
    const kind = element.getAttribute("data-ir-kind");
    const itemId = element.getAttribute("data-ir-id");
    return (kind === "node" || kind === "edge") && itemId ? { selection: { kind, id: itemId }, element } : null;
  }

  function selectRenderedElement(event: MouseEvent<HTMLDivElement>) {
    if (!interactive || !onSelect) return;
    const matched = selectionFromTarget(event.target);
    onSelect(matched?.selection ?? null, event.shiftKey);
  }

  function editRenderedElement(event: MouseEvent<HTMLDivElement>) {
    if (!interactive || !onEditRequest) return;
    const matched = selectionFromTarget(event.target);
    if (!matched || !canvasRef.current) return;
    event.preventDefault();
    const elementBounds = matched.element.getBoundingClientRect();
    const canvasBounds = canvasRef.current.getBoundingClientRect();
    setEditAnchor({
      left: elementBounds.left - canvasBounds.left + canvasRef.current.scrollLeft,
      top: elementBounds.top - canvasBounds.top + canvasRef.current.scrollTop,
      width: Math.max(180, elementBounds.width),
      height: Math.max(matched.selection.kind === "node" ? 72 : 34, elementBounds.height),
    });
    onEditRequest(matched.selection);
  }

  function zoomDiagram(event: WheelEvent) {
    if (!zoomable || compact || !svg) return;
    event.preventDefault();
    const canvas = canvasRef.current;
    if (!canvas) return;
    const next = clampZoom(zoom + (event.deltaY < 0 ? 0.1 : -0.1));
    if (next === zoom) return;
    zoomAt(next, event.clientX, event.clientY);
  }

  function zoomFromCenter(delta: number) {
    if (!canvasRef.current) return;
    const canvas = canvasRef.current;
    const next = clampZoom(zoom + delta);
    if (next === zoom) return;
    const bounds = canvas.getBoundingClientRect();
    zoomAt(next, bounds.left + canvas.clientWidth / 2, bounds.top + canvas.clientHeight / 2);
  }

  function zoomAt(next: number, clientX: number, clientY: number) {
    const canvas = canvasRef.current;
    const layer = canvas?.querySelector(".diagram-transform-layer");
    if (!canvas || !layer) return;
    const before = layer.getBoundingClientRect();
    setZoom(next);
    window.requestAnimationFrame(() => {
      const after = layer.getBoundingClientRect();
      const delta = zoomScrollDelta(before, after, { x: clientX, y: clientY }, zoom, next);
      canvas.scrollLeft += delta.x;
      canvas.scrollTop += delta.y;
    });
  }

  async function saveRevision() {
    if (!onSaveRevision) return;
    setSaving(true);
    try {
      await onSaveRevision(draft);
    } catch {
      // The parent displays the API validation error.
    } finally {
      setSaving(false);
    }
  }

  return <>
    {!compact && <div className="diagram-actions">
      {toolbarContent}
      {zoomable && <><button type="button" className="secondary zoom-button" disabled={zoom <= 0.5} onClick={() => zoomFromCenter(-0.1)} aria-label="축소">−</button><span className="zoom-status">{Math.round(zoom * 100)}%</span><button type="button" className="secondary zoom-button" disabled={zoom >= 3} onClick={() => zoomFromCenter(0.1)} aria-label="확대">＋</button><button type="button" className="secondary" onClick={() => { setZoom(1); canvasRef.current?.scrollTo(0, 0); }}>{fitLabel}</button></>}
      <button type="button" className="secondary" disabled={!svg} onClick={() => downloadSvg(svg, `${downloadName}.svg`)}>SVG 다운로드</button>
      <button type="button" className="secondary" disabled={!svg} onClick={() => void downloadPng(svg, `${downloadName}.png`)}>PNG 다운로드</button>
      {editable && <button type="button" className="secondary" disabled={draft === source || saving} onClick={() => setDraft(source)}>편집 취소</button>}
      {editable && <button type="button" className="primary" disabled={draft === source || Boolean(error) || saving || rendering} onClick={() => void saveRevision()}>{saving ? "저장 중…" : "새 리비전 저장"}</button>}
    </div>}
    {editable && <label className="mermaid-editor-label">Mermaid DSL 편집<textarea className="mermaid-editor" rows={12} value={draft} spellCheck={false} onChange={(event) => setDraft(event.target.value)} /></label>}
    {rendering && !svg && <div className="empty-state"><p>Mermaid 렌더러를 불러오는 중…</p></div>}
    {error && <div className={`error-panel ${compact ? "compact-error" : ""}`} role="alert">{error}</div>}
    <div ref={canvasRef} className={`diagram-canvas ${compact ? "compact" : ""} ${interactive ? "interactive" : ""} ${zoomable ? "zoomable" : ""}`}
      aria-label="생성된 다이어그램" onClick={selectRenderedElement} onDoubleClick={editRenderedElement}>
      <div className="diagram-zoom-layer" style={compact ? undefined : { width: baseSize.width * zoom, height: baseSize.height * zoom } as CSSProperties}>
        <div className="diagram-transform-layer" style={compact ? { width: baseSize.width, height: baseSize.height } : {
          width: baseSize.width, height: baseSize.height, transform: `scale(${zoom})`, transformOrigin: "top left",
        } as CSSProperties} dangerouslySetInnerHTML={{ __html: svg }} />
      </div>
      {inlineEdit && editAnchor && <div className={`diagram-inline-editor ${inlineEdit.kind}`} style={editAnchor}
        onClick={(event) => event.stopPropagation()} onDoubleClick={(event) => event.stopPropagation()}>
        {inlineEdit.kind === "node"
          ? <textarea autoFocus maxLength={1000} value={inlineEdit.value} onChange={(event) => onInlineEditChange?.(event.target.value)}
            onBlur={() => onInlineEditCommit?.()} onKeyDown={(event) => {
              if (event.key === "Escape") { event.preventDefault(); onInlineEditCancel?.(); }
              else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); onInlineEditCommit?.(); }
            }} />
          : <input autoFocus maxLength={240} value={inlineEdit.value} onChange={(event) => onInlineEditChange?.(event.target.value)}
            onBlur={() => onInlineEditCommit?.()} onKeyDown={(event) => {
              if (event.key === "Escape") { event.preventDefault(); onInlineEditCancel?.(); }
              else if (event.key === "Enter") { event.preventDefault(); onInlineEditCommit?.(); }
            }} />}
      </div>}
    </div>
  </>;
}

function decorateSvg(svg: string, artifact: DiagramArtifact | undefined, selected: DiagramSelection[]): { svg: string; mappingComplete: boolean } {
  if (!artifact) return { svg, mappingComplete: false };
  const mapping = renderElementMap(artifact.ir);
  if (!mapping) return { svg, mappingComplete: false };
  const parser = new DOMParser();
  const document = parser.parseFromString(svg, "image/svg+xml");
  const root = document.documentElement;
  root.setAttribute("width", "100%");
  root.setAttribute("height", "100%");
  root.setAttribute("style", `${root.getAttribute("style") ?? ""};width:100%;height:100%;max-width:none;`);
  let mappedNodes = 0;
  const used = new Set<Element>();

  for (const node of artifact.ir.nodes) {
    const candidates = rootsForNode(root, node.id, used);
    if (candidates.length === 0) continue;
    candidates.forEach((element) => tag(element, "node", node.id, selected, node.changeMarker));
    candidates.forEach((element) => used.add(element));
    mappedNodes++;
  }

  const remainingMessages = [...root.querySelectorAll('[data-et="message"]')];
  const remainingMessageLabels = [...root.querySelectorAll("text.messageText, g.messageText")];
  const genericEdges = uniqueRoots(root.querySelectorAll("g.edgePath, .edgePaths > path, path.relation, line.messageLine0, line.messageLine1"), "edge")
    .filter((element) => !used.has(element));
  const genericLabels = uniqueRoots(root.querySelectorAll("g.edgeLabel"), "edge");
  for (const [edgeOrdinal, mapped] of mapping.edges.entries()) {
    const edge = artifact.ir.edges.find(edge => edge.id === mapped.id)!;
    let candidates = rootsForDataId(root, alias(edge.id), "edge");
    if (candidates.length === 0 && artifact.type === "sequence") {
      const source = alias(edge.sourceId);
      const target = alias(edge.targetId);
      const message = remainingMessages[edgeOrdinal];
      if (remainingMessages.length === mapping.edges.length && message?.getAttribute("data-from") === source && message.getAttribute("data-to") === target) {
        candidates = [message];
        const label = remainingMessageLabels.length === mapping.edges.length ? remainingMessageLabels[edgeOrdinal] : undefined;
        if (label) candidates.push(label);
      }
    }
    if (artifact.type !== "sequence" && candidates.length === 0 && genericEdges.length === mapping.edges.length) {
      candidates = [genericEdges[edgeOrdinal]];
    }
    if (genericLabels.length === artifact.ir.edges.length && genericLabels[edgeOrdinal]) candidates.push(genericLabels[edgeOrdinal]);
    if (candidates.length === 0) continue;
    candidates.forEach((element) => tag(element, "edge", edge.id, selected, edge.changeMarker));
  }

  if (artifact.ir.nodes.some((node) => node.changeMarker) || artifact.ir.edges.some((edge) => edge.changeMarker)) {
    appendLegend(document, root);
  }
  return {
    svg: new XMLSerializer().serializeToString(root),
    mappingComplete: mappedNodes === artifact.ir.nodes.length,
  };
}

function rootsForDataId(root: Element, dataId: string, kind: "node" | "edge"): Element[] {
  const matches = [...root.querySelectorAll("[data-id]")].filter((element) => element.getAttribute("data-id") === dataId);
  return uniqueRoots(matches, kind);
}

function rootsForNode(root: Element, id: string, used: Set<Element>): Element[] {
  const dataId = alias(id);
  const direct = rootsForDataId(root, dataId, "node");
  const named = [...root.querySelectorAll("[name]")].filter(element => element.getAttribute("name") === dataId &&
    (element.matches("rect.actor, text.actor, .actor-line") || element.getAttribute("data-et") === "participant"));
  // Mermaid places an unkeyed text.actor immediately after each named actor
  // rectangle. Include that label so clicking its text selects the participant.
  // The keyed rectangle establishes identity even when participant labels repeat.
  const actorLabels = named.filter(element => element.matches("rect.actor"))
    .map(element => element.nextElementSibling)
    .filter((element): element is Element => Boolean(element?.matches("text.actor") &&
      (!element.getAttribute("name") || element.getAttribute("name") === dataId)));
  if (direct.length > 0 || named.length > 0) return [...new Set([...direct, ...named, ...actorLabels])];
  const byDomId = uniqueRoots([...root.querySelectorAll("[id]")].filter((element) => {
    const value = element.id;
    return value === dataId || value.includes(`-${dataId}-`) || value.endsWith(`-${dataId}`) || value.startsWith(`${dataId}-`);
  }), "node").filter((element) => !used.has(element));
  if (byDomId.length > 0) return byDomId;
  // Never guess identity from a label or DOM order: equal labels are legal.
  return [];
}

function uniqueRoots(elements: Iterable<Element>, kind: "node" | "edge"): Element[] {
  const selector = kind === "node"
    ? "g.node, g.actor, g.classGroup, g.statediagram-state, g[data-et='participant']"
    : "g.edgePath, g.edgeLabel, g[class*='edge'], path, line";
  return [...new Set([...elements].map((element) => element.closest(selector) ?? element))];
}

function fittedSvgSize(svg: string, canvas: HTMLDivElement | null, compact: boolean) {
  const root = new DOMParser().parseFromString(svg, "image/svg+xml").documentElement;
  const viewBox = root.getAttribute("viewBox")?.split(/\s+/).map(Number);
  const naturalWidth = Math.max(1, viewBox?.[2] || Number.parseFloat(root.getAttribute("width") ?? "800") || 800);
  const naturalHeight = Math.max(1, viewBox?.[3] || Number.parseFloat(root.getAttribute("height") ?? "500") || 500);
  const availableWidth = Math.max(1, (canvas?.clientWidth ?? naturalWidth) - (compact ? 10 : 40));
  const availableHeight = compact ? 98 : canvas?.closest(".code-block-result-content") ? Math.max(320, window.innerHeight - 300) : Number.POSITIVE_INFINITY;
  const fit = Math.min(1, availableWidth / naturalWidth, availableHeight / naturalHeight);
  return { width: Math.round(naturalWidth * fit), height: Math.round(naturalHeight * fit) };
}

function tag(element: Element, kind: "node" | "edge", id: string, selected: DiagramSelection[], marker?: DiagramArtifact["ir"]["nodes"][number]["changeMarker"]) {
  element.setAttribute("data-ir-kind", kind);
  element.setAttribute("data-ir-id", id);
  element.setAttribute("tabindex", "0");
  element.setAttribute("role", "button");
  element.setAttribute("style", `${element.getAttribute("style") ?? ""};cursor:pointer;`);
  if (marker) applyMarkerStyle(element, marker.kind);
  if (selected.some((item) => item.kind === kind && item.id === id)) {
    element.setAttribute("style", `${element.getAttribute("style") ?? ""};filter:drop-shadow(0 0 5px #2563eb);`);
  }
}

function applyMarkerStyle(element: Element, kind: "Added" | "Modified" | "Deleted") {
  const colors = markerColors(kind);
  // Stroke only geometry. A stroke on a group/text is inherited by its glyphs
  // and makes changed labels look bold or illegible, including in SVG exports.
  const shapes = "path, line, polygon, rect, circle, ellipse";
  const targets = [...(element.matches(shapes) ? [element] : []), ...element.querySelectorAll(shapes)];
  for (const target of targets) {
    const name = target.tagName.toLowerCase();
    const style = `${target.getAttribute("style") ?? ""};stroke:${colors.stroke};stroke-width:3px;`;
    target.setAttribute("style", name === "rect" || name === "circle" || name === "ellipse" ? `${style}fill:${colors.fill};` : style);
    recolorMarker(target, colors.stroke, kind.toLowerCase());
  }
}

function recolorMarker(element: Element, color: string, suffix: string) {
  const value = element.getAttribute("marker-end");
  const markerId = value?.match(/^url\(["']?#([^"')]+)["']?\)$/)?.[1];
  if (!markerId) return;
  const document = element.ownerDocument;
  const original = [...document.querySelectorAll("marker")].find((marker) => marker.id === markerId);
  if (!original) return;
  const cloneId = `${markerId}-${suffix}`;
  const existing = [...document.querySelectorAll("marker")].find((marker) => marker.id === cloneId);
  if (!existing) {
    const cloned = original.cloneNode(true) as SVGMarkerElement;
    cloned.id = cloneId;
    cloned.querySelectorAll("path, polygon").forEach((part) => part.setAttribute("style", `fill:${color};stroke:${color}`));
    original.parentElement?.appendChild(cloned);
  }
  element.setAttribute("marker-end", `url(#${cloneId})`);
}

function appendLegend(document: Document, root: Element) {
  const ns = "http://www.w3.org/2000/svg";
  const viewBox = (root.getAttribute("viewBox") ?? "0 0 1200 800").split(/\s+/).map(Number);
  if (viewBox.length !== 4 || viewBox.some((value) => !Number.isFinite(value))) return;
  const [x, y, width, height] = viewBox;
  root.setAttribute("viewBox", `${x} ${y} ${width} ${height + 56}`);
  const group = document.createElementNS(ns, "g");
  group.setAttribute("data-diagram-legend", "git-changes");
  group.setAttribute("transform", `translate(${x + 8} ${y + height + 12})`);
  const background = document.createElementNS(ns, "rect");
  background.setAttribute("width", String(Math.min(Math.max(width - 16, 360), 640)));
  background.setAttribute("height", "36");
  background.setAttribute("rx", "6");
  background.setAttribute("style", "fill:#f8fafc;stroke:#cbd5e1");
  group.appendChild(background);
  const entries: Array<{ label: string; color: string }> = [
    { label: "추가", color: "#2563eb" },
    { label: "수정", color: "#16a34a" },
    { label: "삭제", color: "#dc2626" },
  ];
  entries.forEach((entry, index) => {
    const offset = 14 + index * 92;
    const swatch = document.createElementNS(ns, "rect");
    swatch.setAttribute("x", String(offset));
    swatch.setAttribute("y", "10");
    swatch.setAttribute("width", "18");
    swatch.setAttribute("height", "16");
    swatch.setAttribute("rx", "3");
    swatch.setAttribute("style", `fill:${entry.color}22;stroke:${entry.color};stroke-width:2px`);
    group.appendChild(swatch);
    const text = document.createElementNS(ns, "text");
    text.setAttribute("x", String(offset + 25));
    text.setAttribute("y", "23");
    text.setAttribute("style", "font:600 12px Arial,sans-serif;fill:#334155");
    text.textContent = entry.label;
    group.appendChild(text);
  });
  root.appendChild(group);
}

function markerColors(kind: "Added" | "Modified" | "Deleted") {
  if (kind === "Added") return { stroke: "#2563eb", fill: "#dbeafe" };
  if (kind === "Modified") return { stroke: "#16a34a", fill: "#dcfce7" };
  return { stroke: "#dc2626", fill: "#fee2e2" };
}

function downloadSvg(svg: string, filename: string) {
  const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml;charset=utf-8" }));
  triggerDownload(url, filename);
  URL.revokeObjectURL(url);
}

async function downloadPng(svg: string, filename: string) {
  const url = URL.createObjectURL(new Blob([svg], { type: "image/svg+xml;charset=utf-8" }));
  try {
    const image = new Image();
    image.src = url;
    await new Promise<void>((resolve, reject) => { image.onload = () => resolve(); image.onerror = reject; });
    const root = new DOMParser().parseFromString(svg, "image/svg+xml").documentElement;
    const viewBox = root.getAttribute("viewBox")?.split(/\s+/).map(Number);
    const width = Math.max(1, viewBox?.[2] || Number.parseFloat(root.getAttribute("width") ?? "1200") || 1200);
    const height = Math.max(1, viewBox?.[3] || Number.parseFloat(root.getAttribute("height") ?? "800") || 800);
    const canvas = document.createElement("canvas");
    canvas.width = Math.ceil(width * 2);
    canvas.height = Math.ceil(height * 2);
    const context = canvas.getContext("2d");
    if (!context) throw new Error("canvas unavailable");
    context.fillStyle = "#ffffff";
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(image, 0, 0, canvas.width, canvas.height);
    const png = await new Promise<Blob>((resolve, reject) => canvas.toBlob(value => value ? resolve(value) : reject(new Error("PNG 생성 실패")), "image/png"));
    const pngUrl = URL.createObjectURL(png);
    triggerDownload(pngUrl, filename);
    URL.revokeObjectURL(pngUrl);
  } catch {
    window.alert("다이어그램 PNG 생성에 실패했습니다.");
  } finally {
    URL.revokeObjectURL(url);
  }
}

function triggerDownload(url: string, filename: string) {
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}
