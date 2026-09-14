import type { DiagramVariant } from "./diagramOrigin";

export function DiagramBadge({ kind }: { kind: DiagramVariant }) {
  return <svg className={`diagram-origin-badge ${kind}`} role="img" aria-label={kind === "ai" ? "AI" : "CODE"} viewBox="0 0 40 20" width="40" height="20">
    <rect width="40" height="20" rx="5" fill="currentColor" />
    <text x="20" y="14" textAnchor="middle" fill="white" fontSize="10" fontWeight="700">{kind === "ai" ? "AI" : "CODE"}</text>
  </svg>;
}
