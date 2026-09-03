import { useEffect, useId, useState } from "react";

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
  downloadName?: string;
  editable?: boolean;
  compact?: boolean;
  onSaveRevision?: (source: string) => Promise<void>;
};

export function MermaidPreview({ source, downloadName = "diagram", editable = false, compact = false, onSaveRevision }: MermaidPreviewProps) {
  const id = useId().replaceAll(":", "_");
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
          setSvg(sanitizeSvg(result.svg));
          setError("");
        })
        .catch(() => {
          if (!active) return;
          setError("다이어그램 문법을 렌더링할 수 없습니다. 마지막 정상 미리보기를 유지합니다.");
        })
        .finally(() => { if (active) setRendering(false); });
    }, editable ? 350 : 0);
    return () => { active = false; window.clearTimeout(timer); };
  }, [draft, editable, id, source]);

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
    <div className={`diagram-canvas ${compact ? "compact" : ""}`} aria-label="생성된 다이어그램" dangerouslySetInnerHTML={{ __html: svg }} />
  </>;
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
