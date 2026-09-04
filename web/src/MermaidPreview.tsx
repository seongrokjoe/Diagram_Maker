import { useEffect, useId, useRef, useState, type MouseEvent } from "react";
import type { DiagramArtifact } from "./types";

type MermaidApi = {
  initialize: (configuration: Record<string, unknown>) => void;
  parse: (source: string, options?: { suppressErrors?: boolean }) => Promise<unknown>;
  render: (id: string, source: string) => Promise<{ svg: string }>;
};
let mermaidPromise: Promise<MermaidApi> | undefined;
let renderQueue: Promise<void> = Promise.resolve();

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

function sanitizeSvg(svg: string): string {
  const parser = new DOMParser();
  const document = parser.parseFromString(svg, "image/svg+xml");
  document.querySelectorAll("script, foreignObject, iframe, object, embed, image, a").forEach((element) => element.remove());
  document.querySelectorAll("*").forEach((element) => {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim().toLowerCase();
      const unsafeReference = (name === "href" || name === "xlink:href") && !value.startsWith("#");
      const unsafeUrl = [...value.matchAll(/url\(([^)]+)\)/g)]
        .some(([, reference]) => !reference.trim().replace(/^['"]|['"]$/g, "").startsWith("#"));
      if (name.startsWith("on") || unsafeReference || value.startsWith("javascript:") || unsafeUrl) element.removeAttribute(attribute.name);
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
  source: string;
  artifact?: DiagramArtifact;
  downloadName?: string;
  editable?: boolean;
  compact?: boolean;
  interactive?: boolean;
  selected?: { kind: "node" | "edge"; id: string } | null;
  onSelect?: (selection: { kind: "node" | "edge"; id: string } | null) => void;
  onInteractionReady?: (available: boolean) => void;
  onSaveRevision?: (source: string) => Promise<void>;
};

export function MermaidPreview({ source, artifact, downloadName = "diagram", editable = false, compact = false,
  interactive = false, selected, onSelect, onInteractionReady, onSaveRevision }: MermaidPreviewProps) {
  const id = useId().replaceAll(":", "_");
  const canvasRef = useRef<HTMLDivElement>(null);
  const [draft, setDraft] = useState(source);
  const [svg, setSvg] = useState("");
  const [error, setError] = useState("");
  const [rendering, setRendering] = useState(true);
  const [saving, setSaving] = useState(false);

  useEffect(() => setDraft(source), [source]);

  useEffect(() => {
    let active = true;
    setRendering(true);
    const timer = window.setTimeout(() => {
      const renderSource = editable ? draft : source;
      if (/```|%%|javascript:|https?:\/\/|\bclick\s/i.test(renderSource)) {
        setError("외부 링크, click, Mermaid directive 또는 코드 펜스는 사용할 수 없습니다.");
        setRendering(false);
        return;
      }
      const renderId = `diagram_${id}_${Date.now()}`;
      void loadMermaid()
        .then((mermaid) => renderMermaid(mermaid, renderId, renderSource))
        .then((result) => {
          if (!active) return;
          const decorated = decorateSvg(sanitizeSvg(result.svg), artifact, selected ?? null);
          setSvg(decorated.svg);
          onInteractionReady?.(interactive && decorated.mappingComplete);
          setError("");
        })
        .catch(() => {
          if (!active) return;
          setError("다이어그램 문법을 렌더링할 수 없습니다. 마지막 정상 미리보기를 유지합니다.");
        })
        .finally(() => { if (active) setRendering(false); });
    }, editable ? 350 : 0);
    return () => { active = false; window.clearTimeout(timer); };
  }, [artifact, draft, editable, id, interactive, onInteractionReady, selected, source]);

  function selectRenderedElement(event: MouseEvent<HTMLDivElement>) {
    if (!interactive || !onSelect) return;
    const target = event.target instanceof Element ? event.target.closest("[data-ir-id]") : null;
    if (!target || !canvasRef.current?.contains(target)) { onSelect(null); return; }
    const kind = target.getAttribute("data-ir-kind");
    const itemId = target.getAttribute("data-ir-id");
    if ((kind === "node" || kind === "edge") && itemId) onSelect({ kind, id: itemId });
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
      <button type="button" className="secondary" disabled={!svg} onClick={() => downloadSvg(svg, `${downloadName}.svg`)}>SVG 다운로드</button>
      <button type="button" className="secondary" disabled={!svg} onClick={() => void downloadPng(svg, `${downloadName}.png`)}>PNG 다운로드</button>
      {editable && <button type="button" className="secondary" disabled={draft === source || saving} onClick={() => setDraft(source)}>편집 취소</button>}
      {editable && <button type="button" className="primary" disabled={draft === source || Boolean(error) || saving || rendering} onClick={() => void saveRevision()}>{saving ? "저장 중…" : "새 리비전 저장"}</button>}
    </div>}
    {editable && <label className="mermaid-editor-label">Mermaid DSL 편집<textarea className="mermaid-editor" rows={12} value={draft} spellCheck={false} onChange={(event) => setDraft(event.target.value)} /></label>}
    {rendering && !svg && <div className="empty-state"><p>Mermaid 렌더러를 불러오는 중…</p></div>}
    {error && <div className={`error-panel ${compact ? "compact-error" : ""}`} role="alert">{error}</div>}
    <div ref={canvasRef} className={`diagram-canvas ${compact ? "compact" : ""} ${interactive ? "interactive" : ""}`}
      aria-label="생성된 다이어그램" onClick={selectRenderedElement} dangerouslySetInnerHTML={{ __html: svg }} />
  </>;
}

function decorateSvg(svg: string, artifact: DiagramArtifact | undefined, selected: MermaidPreviewProps["selected"]): { svg: string; mappingComplete: boolean } {
  if (!artifact) return { svg, mappingComplete: false };
  const parser = new DOMParser();
  const document = parser.parseFromString(svg, "image/svg+xml");
  const root = document.documentElement;
  let mappedNodes = 0;
  let mappedEdges = 0;
  const used = new Set<Element>();

  for (const node of artifact.ir.nodes) {
    const candidates = rootsForDataId(root, alias(node.id), "node");
    if (candidates.length === 0) continue;
    candidates.forEach((element) => tag(element, "node", node.id, selected, node.changeMarker));
    candidates.forEach((element) => used.add(element));
    mappedNodes++;
  }

  const remainingMessages = [...root.querySelectorAll('[data-et="message"]')];
  const genericEdges = uniqueRoots(root.querySelectorAll("g.edgePath, .edgePaths > path, path.relation, line.messageLine0, line.messageLine1"), "edge")
    .filter((element) => !used.has(element));
  let genericIndex = 0;
  for (const edge of artifact.ir.edges) {
    let candidates = rootsForDataId(root, alias(edge.id), "edge");
    if (candidates.length === 0 && artifact.type === "sequence") {
      const source = alias(edge.sourceId);
      const target = alias(edge.targetId);
      const index = remainingMessages.findIndex((element) =>
        element.getAttribute("data-from") === source && element.getAttribute("data-to") === target);
      if (index >= 0) candidates = [remainingMessages.splice(index, 1)[0]];
    }
    if (candidates.length === 0 && genericEdges.length === artifact.ir.edges.length) {
      candidates = [genericEdges[genericIndex++]];
    }
    if (candidates.length === 0) continue;
    candidates.forEach((element) => tag(element, "edge", edge.id, selected, edge.changeMarker));
    mappedEdges++;
  }

  if (artifact.ir.nodes.some((node) => node.changeMarker) || artifact.ir.edges.some((edge) => edge.changeMarker)) {
    appendLegend(document, root);
  }
  return {
    svg: new XMLSerializer().serializeToString(root),
    mappingComplete: mappedNodes === artifact.ir.nodes.length && mappedEdges === artifact.ir.edges.length,
  };
}

function rootsForDataId(root: Element, dataId: string, kind: "node" | "edge"): Element[] {
  const matches = [...root.querySelectorAll("[data-id]")].filter((element) => element.getAttribute("data-id") === dataId);
  return uniqueRoots(matches, kind);
}

function uniqueRoots(elements: Iterable<Element>, kind: "node" | "edge"): Element[] {
  const selector = kind === "node"
    ? "g.node, g.actor, g[class*='node'], g[class*='actor']"
    : "g.edgePath, g.edgeLabel, g[class*='edge'], path, line";
  return [...new Set([...elements].map((element) => element.closest(selector) ?? element))];
}

function tag(element: Element, kind: "node" | "edge", id: string, selected: MermaidPreviewProps["selected"], marker?: DiagramArtifact["ir"]["nodes"][number]["changeMarker"]) {
  element.setAttribute("data-ir-kind", kind);
  element.setAttribute("data-ir-id", id);
  element.setAttribute("tabindex", "0");
  element.setAttribute("role", "button");
  element.setAttribute("style", `${element.getAttribute("style") ?? ""};cursor:pointer;`);
  if (marker) applyMarkerStyle(element, marker.kind === "Deleted" || marker.precision === "Symbol");
  if (selected?.kind === kind && selected.id === id) {
    element.setAttribute("style", `${element.getAttribute("style") ?? ""};filter:drop-shadow(0 0 5px #2563eb);`);
  }
}

function applyMarkerStyle(element: Element, dashed: boolean) {
  const targets = [element, ...element.querySelectorAll("path, line, polygon, rect, circle, ellipse")];
  for (const target of targets) {
    const name = target.tagName.toLowerCase();
    const style = `${target.getAttribute("style") ?? ""};stroke:#dc2626;stroke-width:3px;${dashed ? "stroke-dasharray:6 4;" : ""}`;
    target.setAttribute("style", name === "rect" || name === "circle" || name === "ellipse" ? `${style}fill:#fee2e2;` : style);
    recolorMarker(target);
  }
}

function recolorMarker(element: Element) {
  const value = element.getAttribute("marker-end");
  const markerId = value?.match(/^url\(["']?#([^"')]+)["']?\)$/)?.[1];
  if (!markerId) return;
  const document = element.ownerDocument;
  const original = [...document.querySelectorAll("marker")].find((marker) => marker.id === markerId);
  if (!original) return;
  const cloneId = `${markerId}-changed`;
  const existing = [...document.querySelectorAll("marker")].find((marker) => marker.id === cloneId);
  if (!existing) {
    const cloned = original.cloneNode(true) as SVGMarkerElement;
    cloned.id = cloneId;
    cloned.querySelectorAll("path, polygon").forEach((part) => part.setAttribute("style", "fill:#dc2626;stroke:#dc2626"));
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
  background.setAttribute("width", String(Math.min(Math.max(width - 16, 420), 720)));
  background.setAttribute("height", "36");
  background.setAttribute("rx", "6");
  background.setAttribute("style", "fill:#fff7f7;stroke:#fecaca");
  group.appendChild(background);
  const text = document.createElementNS(ns, "text");
  text.setAttribute("x", "12");
  text.setAttribute("y", "23");
  text.setAttribute("style", "font:600 12px Arial,sans-serif;fill:#991b1b");
  text.textContent = "+ 추가   ~ 수정   − 삭제(점선)   심볼 수준(점선)";
  group.appendChild(text);
  root.appendChild(group);
}

function alias(id: string) { return `n_${id.replace(/[^a-zA-Z0-9_]/g, "_")}`; }

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
