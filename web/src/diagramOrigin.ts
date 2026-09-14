import type { DiagramArtifact } from "./types";

export type DiagramVariant = "ai" | "code";
export type DiagramResultCounts = { aiCompleted: number; aiFailed: number; aiPending: number; codeCompleted: number; reusedAi?: number };
export function originOf(artifact: DiagramArtifact): DiagramVariant {
  return artifact.explanation?.status === "Semantic" || artifact.ir.provenance?.some(p => p.startsWith("natural")) ? "ai" : "code";
}
export function originOfPage(page: { resultKind?: string; diagram: DiagramArtifact }): DiagramVariant {
  return page.resultKind === "semantic" ? "ai" : page.resultKind === "static" ? "code" : originOf(page.diagram);
}
export function baseDiagramName(title: string) { return title.replace(/^(?:AI_|Code_)/i, ""); }
export function diagramName(title: string, kind: DiagramVariant) { return `${kind === "ai" ? "AI" : "Code"}_${baseDiagramName(title)}`; }
export function matchesDiagramName(title: string, query: string) { return baseDiagramName(title).toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()); }
export function resultCountsText(c: DiagramResultCounts) {
  return `AI 완료 ${c.aiCompleted}개 · AI 실패 ${c.aiFailed}개 · 대기 ${c.aiPending}개 · Code 완료 ${c.codeCompleted}개${c.reusedAi ? ` · 이전 AI 유지 ${c.reusedAi}개` : ""}`;
}
