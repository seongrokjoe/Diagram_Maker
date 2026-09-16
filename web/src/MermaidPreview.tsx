import { useEffect, useId, useMemo, useRef, useState, type CSSProperties, type MouseEvent, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import type { DiagramArtifact } from "./types";
import { clampZoom, steppedZoom, zoomPresets, renderAlias as alias, renderElementMap, zoomScrollDelta, isZoomWheel,
  canStartCanvasPan, memberSelectionId, type ElementSelection } from "./diagramInteraction";
import { maximumMermaidCharacters, mermaidSafetyError } from "./mermaidSafety";
import { prepareMermaidDisplay } from "./mermaidDisplay";
import { sanitizeSvg } from "./svgSafety";
import { fitZoom } from "./diagramViewSettings";
import { prepareSequenceLayout } from "./sequenceLayout";

export type DiagramSelection = ElementSelection;
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
      mermaid.initialize({ startOnLoad: false, securityLevel: "strict", htmlLabels: false, maxEdges: 500, maxTextSize: maximumMermaidCharacters, theme: "base", themeVariables: { primaryColor: "#e7f0ff", primaryTextColor: "#10213a", primaryBorderColor: "#4b72a9", lineColor: "#52709a", fontFamily: '"Pretendard Variable", "Malgun Gothic", sans-serif' } });
      resolve(mermaid);
    };
    script.onerror = () => reject(new Error("Mermaid runtime could not be loaded."));
    document.head.appendChild(script);
  });
  return mermaidPromise;
}

function renderMermaid(mermaid: MermaidApi, id: string, source: string): Promise<{ svg: string }> {
  const run = renderQueue.then(async () => {
    try {
      await document.fonts.ready;
      const context = document.createElement("canvas").getContext("2d");
      if (context) context.font = '16px "Pretendard Variable", "Malgun Gothic", sans-serif';
      const display = prepareSequenceLayout(source, text => context?.measureText(text).width ?? text.length * 16);
      // Rendering is serialized: per-diagram dimensions cannot leak into another preview.
      mermaid.initialize({ startOnLoad: false, securityLevel: "strict", htmlLabels: false, maxEdges: 500,
        maxTextSize: maximumMermaidCharacters, theme: "base", sequence: display.sequence,
        themeVariables: { primaryColor: "#e7f0ff", primaryTextColor: "#10213a", primaryBorderColor: "#4b72a9", lineColor: "#52709a", fontFamily: '"Pretendard Variable", "Malgun Gothic", sans-serif' } });
      const parsed = await mermaid.parse(display.source, { suppressErrors: true });
      if (parsed === false) throw new Error("Invalid Mermaid syntax.");
      return await mermaid.render(id, display.source);
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
  editMode?: boolean;
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
  onInlineEditCommit, onInlineEditCancel, onInteractionReady, onSaveRevision, toolbarContent, fitLabel = "맞춤 보기", editMode = false }: MermaidPreviewProps) {
  const id = useId().replace(/[^A-Za-z0-9_-]/g, character => `_${character.codePointAt(0)!.toString(16)}_`);
  const canvasRef = useRef<HTMLDivElement>(null);
  const [draft, setDraft] = useState(source);
  const [svg, setSvg] = useState("");
  const [error, setError] = useState("");
  const [rendering, setRendering] = useState(true);
  const [saving, setSaving] = useState(false);
  const [zoom, setZoom] = useState(1);
  const [fitting, setFitting] = useState(true);
  const [baseSize, setBaseSize] = useState({ width: 800, height: 500 });
  const [editAnchor, setEditAnchor] = useState<{ left: number; top: number; width: number; height: number } | null>(null);
  const [panning, setPanning] = useState(false);
  // Selection and drag state must not replace the clicked SVG DOM between the
  // second click and the browser's dblclick event.
  const svgMarkup = useMemo(() => ({ __html: svg }), [svg]);
  const pan = useRef<{ pointerId: number; x: number; y: number; left: number; top: number; dragged: boolean } | null>(null);
  const spaceHeld = useRef(false);
  const suppressClick = useRef(false);
  const interactionReady = useRef(onInteractionReady);
  const mappingComplete = useRef(false);
  interactionReady.current = onInteractionReady;
  useEffect(() => { interactionReady.current?.(interactive && mappingComplete.current); }, [interactive, svg]);

  useEffect(() => setDraft(source), [source]);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const handle = (event: WheelEvent) => zoomDiagram(event);
    canvas.addEventListener("wheel", handle, { passive: false });
    return () => canvas.removeEventListener("wheel", handle);
  }, [zoom, zoomable, compact, svg]);
  useEffect(() => { setZoom(1); setFitting(true); setEditAnchor(null); }, [artifact?.id]);
  useEffect(() => {
    const keyDown = (event: globalThis.KeyboardEvent) => {
      if (event.code !== "Space" || !editMode || event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement) return;
      spaceHeld.current = true;
      event.preventDefault();
    };
    const keyUp = (event: globalThis.KeyboardEvent) => { if (event.code === "Space") spaceHeld.current = false; };
    const blur = () => { spaceHeld.current = false; };
    window.addEventListener("keydown", keyDown);
    window.addEventListener("keyup", keyUp);
    window.addEventListener("blur", blur);
    return () => { window.removeEventListener("keydown", keyDown); window.removeEventListener("keyup", keyUp); window.removeEventListener("blur", blur); };
  }, [editMode]);
  useEffect(() => {
    canvasRef.current?.querySelectorAll("[data-ir-id]").forEach(element => {
      element.classList.toggle("ir-selected", selected.some(item => item.id === element.getAttribute("data-ir-id") && item.kind === element.getAttribute("data-ir-kind")));
    });
  }, [selected, svg]);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || !svg) return;
    const resize = () => {
      const size = fittedSvgSize(svg, canvas, compact);
      setBaseSize(size);
      if (!compact && fitting) setZoom(fitZoom(size.width, size.height, canvas.clientWidth - 40, Math.max(320, window.innerHeight - 300)));
    };
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(canvas);
    return () => observer.disconnect();
  }, [svg, compact, fitting]);

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
          mappingComplete.current = decorated.mappingComplete;
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
  }, [artifact?.id, artifact?.version, draft, editable, id, source]);

  function selectionFromTarget(target: EventTarget | null): { selection: DiagramSelection; element: Element } | null {
    const element = target instanceof Element ? target.closest("[data-ir-id]") : null;
    if (!element || !canvasRef.current?.contains(element)) return null;
    const kind = element.getAttribute("data-ir-kind");
    const itemId = element.getAttribute("data-ir-id");
    return (kind === "node" || kind === "edge" || kind === "member" || kind === "annotation") && itemId
      ? { selection: { kind, id: itemId }, element } : null;
  }

  function selectRenderedElement(event: MouseEvent<HTMLDivElement>) {
    if (suppressClick.current) { suppressClick.current = false; event.preventDefault(); return; }
    if (!interactive || !onSelect) return;
    const matched = selectionFromTarget(event.target);
    onSelect(matched?.selection ?? null, event.shiftKey);
  }

  function beginPan(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) return;
    const rendered = event.target instanceof Element ? event.target.closest("[data-ir-id]") : null;
    if (!canStartCanvasPan({ zoomable, compact, editMode, spaceHeld: spaceHeld.current,
      overDiagramElement: Boolean(rendered), button: event.button })) return;
    const canvas = canvasRef.current;
    if (!canvas) return;
    pan.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY,
      left: canvas.scrollLeft, top: canvas.scrollTop, dragged: false };
    canvas.setPointerCapture(event.pointerId);
  }

  function movePan(event: ReactPointerEvent<HTMLDivElement>) {
    const start = pan.current;
    const canvas = canvasRef.current;
    if (!start || !canvas || start.pointerId !== event.pointerId) return;
    const x = event.clientX - start.x;
    const y = event.clientY - start.y;
    if (!start.dragged && Math.hypot(x, y) < 3) return;
    start.dragged = true;
    setPanning(true);
    canvas.scrollLeft = start.left - x;
    canvas.scrollTop = start.top - y;
    event.preventDefault();
  }

  function endPan(event: ReactPointerEvent<HTMLDivElement>) {
    const start = pan.current;
    const canvas = canvasRef.current;
    if (!start || start.pointerId !== event.pointerId) return;
    suppressClick.current = start.dragged;
    pan.current = null;
    setPanning(false);
    if (canvas?.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
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
    if (!zoomable || compact || !svg || !isZoomWheel(event)) return;
    event.preventDefault();
    const canvas = canvasRef.current;
    if (!canvas) return;
    const next = steppedZoom(zoom, event.deltaY < 0 ? 1 : -1);
    if (next === zoom && !fitting) return;
    zoomAt(next, event.clientX, event.clientY);
  }

  function zoomFromCenter(value: number) {
    if (!canvasRef.current) return;
    const canvas = canvasRef.current;
    const next = clampZoom(value);
    if (next === zoom && !fitting) return;
    const bounds = canvas.getBoundingClientRect();
    zoomAt(next, bounds.left + canvas.clientWidth / 2, bounds.top + canvas.clientHeight / 2);
  }

  function zoomAt(next: number, clientX: number, clientY: number) {
    const canvas = canvasRef.current;
    const layer = canvas?.querySelector(".diagram-transform-layer");
    if (!canvas || !layer) return;
    const before = layer.getBoundingClientRect();
    setFitting(false);
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
      {zoomable && <><button type="button" className="secondary zoom-button" disabled={zoom <= 0.01} onClick={() => zoomFromCenter(steppedZoom(zoom, -1))} aria-label="축소">−</button><span className="zoom-status">{fitting ? "맞춤 · " : ""}{Math.round(zoom * 100)}%</span><button type="button" className="secondary zoom-button" disabled={zoom >= 16} onClick={() => zoomFromCenter(steppedZoom(zoom, 1))} aria-label="확대">＋</button>
        <label className="inline-select">원본 기준 확대<select aria-label="원본 기준 확대" value={fitting ? "fit" : String(zoom)} onChange={e => zoomFromCenter(Number(e.target.value))}>
          {fitting && <option value="fit">맞춤</option>}{!zoomPresets.includes(zoom) && !fitting && <option value={zoom}>{Math.round(zoom * 100)}%</option>}
          {zoomPresets.map(value => <option key={value} value={value}>{value * 100}%</option>)}</select></label>
        <button type="button" className="secondary" onClick={() => { setFitting(true); setZoom(fitZoom(baseSize.width, baseSize.height, (canvasRef.current?.clientWidth ?? baseSize.width) - 40, Math.max(320, window.innerHeight - 300))); canvasRef.current?.scrollTo(0, 0); }}>{fitLabel}</button></>}
      <button type="button" className="secondary" disabled={!svg} onClick={() => downloadSvg(svg, `${downloadName}.svg`)}>SVG 다운로드</button>
      <button type="button" className="secondary" disabled={!svg} onClick={() => void downloadPng(svg, `${downloadName}.png`)}>PNG 다운로드</button>
      {editable && <button type="button" className="secondary" disabled={draft === source || saving} onClick={() => setDraft(source)}>편집 취소</button>}
      {editable && <button type="button" className="primary" disabled={draft === source || Boolean(error) || saving || rendering} onClick={() => void saveRevision()}>{saving ? "저장 중…" : "새 리비전 저장"}</button>}
    </div>}
    {editable && <label className="mermaid-editor-label">Mermaid DSL 편집<textarea className="mermaid-editor" rows={12} value={draft} spellCheck={false} onChange={(event) => setDraft(event.target.value)} /></label>}
    {rendering && !svg && <div className="empty-state"><p>Mermaid 렌더러를 불러오는 중…</p></div>}
    {error && <div className={`error-panel ${compact ? "compact-error" : ""}`} role="alert">{error}</div>}
    <div ref={canvasRef} aria-busy={rendering} className={`diagram-canvas ${compact ? "compact" : ""} ${interactive ? "interactive" : ""} ${zoomable ? "zoomable pannable" : ""} ${editMode ? "edit-mode" : "view-mode"} ${panning ? "panning" : ""}`}
      aria-label="생성된 다이어그램" title={zoomable && !compact ? editMode ? "빈 공간 드래그 또는 Space + 드래그: 이동 · Ctrl + 휠: 확대·축소" : "좌클릭 드래그: 이동 · Ctrl + 휠: 확대·축소" : undefined}
      onPointerDown={beginPan} onPointerMove={movePan} onPointerUp={endPan} onPointerCancel={endPan}
      onClick={selectRenderedElement} onDoubleClick={editRenderedElement}>
      <div className="diagram-zoom-layer" style={compact ? undefined : { width: baseSize.width * zoom, height: baseSize.height * zoom } as CSSProperties}>
        <div className="diagram-transform-layer" style={compact ? { width: baseSize.width, height: baseSize.height } : {
          width: baseSize.width, height: baseSize.height, transform: `scale(${zoom})`, transformOrigin: "top left",
        } as CSSProperties} dangerouslySetInnerHTML={svgMarkup} />
      </div>
      {inlineEdit && editAnchor && <div className={`diagram-inline-editor ${inlineEdit.kind}`} style={editAnchor}
        onClick={(event) => event.stopPropagation()} onDoubleClick={(event) => event.stopPropagation()}>
        {inlineEdit.kind === "node"
          ? <textarea autoFocus maxLength={1000} value={inlineEdit.value} onChange={(event) => onInlineEditChange?.(event.target.value)}
            onBlur={() => onInlineEditCommit?.()} onKeyDown={(event) => {
              if (event.key === "Escape") { event.preventDefault(); onInlineEditCancel?.(); }
              else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); onInlineEditCommit?.(); }
            }} />
          : <input autoFocus maxLength={inlineEdit.kind === "member" ? 500 : inlineEdit.kind === "annotation" ? 1000 : 240} value={inlineEdit.value} onChange={(event) => onInlineEditChange?.(event.target.value)}
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
    tagClassMembers(candidates, node.id, node.details ?? [], selected);
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

  tagSequenceAnnotations(root, artifact.ir.sequenceBlocks ?? [], selected);

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
  return { width: naturalWidth * (compact ? fit : 1), height: naturalHeight * (compact ? fit : 1) };
}

function tag(element: Element, kind: DiagramSelection["kind"], id: string, selected: DiagramSelection[], marker?: DiagramArtifact["ir"]["nodes"][number]["changeMarker"]) {
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

function tagClassMembers(candidates: Element[], nodeId: string, details: string[], selected: DiagramSelection[]) {
  if (details.length === 0) return;
  const groups = [...new Set(candidates.map(element => element.closest("g.node, g.classGroup") ?? element)
    .filter(element => element.matches("g.node, g.classGroup")))];
  if (groups.length !== 1) return;
  // Mermaid separates attributes and methods, preserving emission order inside
  // each compartment. Keep the original member indices when it reorders them.
  for (const [selector, method] of [[".members-group > g.label", false], [".methods-group > g.label", true]] as const) {
    const indices = details.flatMap((detail, index) => (detail.indexOf(")") > 0) === method ? [index] : []);
    const labels = [...groups[0].querySelectorAll(selector)];
    if (labels.length !== indices.length) continue;
    labels.forEach((element, index) => tag(element, "member", memberSelectionId(nodeId, indices[index]), selected));
  }
}

function tagSequenceAnnotations(root: Element, blocks: NonNullable<DiagramArtifact["ir"]["sequenceBlocks"]>, selected: DiagramSelection[]) {
  const annotations = sequenceAnnotations(blocks);
  if (annotations.length === 0) return;
  const candidates = [...new Set([...root.querySelectorAll("text.loopText, text.noteText, text.labelText, g.note text, text[class*='loop'], text[class*='note']")]
    .map(element => element.closest("text") ?? element))];
  let cursor = 0;
  for (const annotation of annotations) {
    const expected = normalizeRenderedText(annotation.label);
    const offset = candidates.slice(cursor).findIndex(element => normalizeRenderedText(element.textContent ?? "").includes(expected));
    if (offset < 0) continue;
    const index = cursor + offset;
    tag(candidates[index], "annotation", annotation.id, selected);
    cursor = index + 1;
  }
}

function sequenceAnnotations(blocks: NonNullable<DiagramArtifact["ir"]["sequenceBlocks"]>): Array<{ id: string; label: string }> {
  return blocks.flatMap(block => [
    ...(block.kind === "alt" || block.kind === "loop" || block.kind === "break" || block.kind === "opt" || block.kind === "note" || block.kind === "scenario"
      ? [{ id: block.id, label: block.label }] : []),
    ...sequenceAnnotations(block.children),
  ]);
}

function normalizeRenderedText(value: string) { return value.replace(/\s+/g, " ").trim(); }

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
  const legendWidth = Math.min(Math.max(width - 16, 360), 640);
  root.setAttribute("viewBox", `${x} ${y} ${Math.max(width, legendWidth + 16)} ${height + 56}`);
  const group = document.createElementNS(ns, "g");
  group.setAttribute("data-diagram-legend", "git-changes");
  group.setAttribute("transform", `translate(${x + 8} ${y + height + 12})`);
  const background = document.createElementNS(ns, "rect");
  background.setAttribute("width", String(legendWidth));
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
