import { useEffect, useId, useState } from "react";
import mermaid from "mermaid";

mermaid.initialize({
  startOnLoad: false,
  securityLevel: "strict",
  htmlLabels: false,
  maxEdges: 500,
  maxTextSize: 50_000,
  theme: "base",
  themeVariables: {
    primaryColor: "#e7f0ff",
    primaryTextColor: "#10213a",
    primaryBorderColor: "#4b72a9",
    lineColor: "#52709a",
    fontFamily: "Arial, sans-serif",
  },
});

function sanitizeSvg(svg: string): string {
  const parser = new DOMParser();
  const document = parser.parseFromString(svg, "image/svg+xml");
  document.querySelectorAll("script, foreignObject, iframe, object, embed, image, a").forEach((element) => element.remove());
  document.querySelectorAll("*").forEach((element) => {
    for (const attribute of [...element.attributes]) {
      const name = attribute.name.toLowerCase();
      const value = attribute.value.trim().toLowerCase();
      if (name.startsWith("on") || name === "href" || name === "xlink:href" || value.startsWith("javascript:")) {
        element.removeAttribute(attribute.name);
      }
    }
  });
  return new XMLSerializer().serializeToString(document.documentElement);
}

export function MermaidPreview({ source }: { source: string }) {
  const id = useId().replaceAll(":", "_");
  const [svg, setSvg] = useState("");
  const [error, setError] = useState("");

  useEffect(() => {
    let active = true;
    void mermaid
      .render(`diagram_${id}_${Date.now()}`, source)
      .then((result) => {
        if (!active) return;
        setSvg(sanitizeSvg(result.svg));
        setError("");
      })
      .catch(() => {
        if (!active) return;
        setSvg("");
        setError("다이어그램 문법을 렌더링할 수 없습니다.");
      });
    return () => {
      active = false;
    };
  }, [id, source]);

  if (error) return <div className="error-panel">{error}</div>;
  return <div className="diagram-canvas" aria-label="생성된 다이어그램" dangerouslySetInnerHTML={{ __html: svg }} />;
}
