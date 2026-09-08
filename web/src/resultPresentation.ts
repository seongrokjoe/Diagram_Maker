import type { DiagramArtifact } from "./types";

export type EvidenceItem = { id: string; number: number; contextLabel: string };
export const evidencePageSize = 20;

export function buildEvidenceItems(ids: string[], diagram?: DiagramArtifact["ir"]): EvidenceItem[] {
  const labels = new Map<string, string>();
  for (const element of [...(diagram?.nodes ?? []), ...(diagram?.edges ?? [])]) {
    for (const id of element.evidenceIds) {
      if (!labels.has(id) && element.label.trim()) labels.set(id, element.label);
    }
  }
  return [...new Set(ids)].map((id, index) => ({ id, number: index + 1, contextLabel: labels.get(id) ?? "" }));
}

export function filterEvidenceItems(items: EvidenceItem[], query: string): EvidenceItem[] {
  const normalized = query.trim().toLocaleLowerCase();
  return normalized ? items.filter(item => `근거 ${item.number} ${item.id} ${item.contextLabel}`.toLocaleLowerCase().includes(normalized)) : items;
}

export function evidencePage(items: EvidenceItem[], requestedPage: number) {
  const totalPages = Math.max(1, Math.ceil(items.length / evidencePageSize));
  const pageIndex = Math.max(0, Math.min(requestedPage, totalPages - 1));
  const offset = pageIndex * evidencePageSize;
  return { items: items.slice(offset, offset + evidencePageSize), pageIndex, totalPages,
    first: items.length ? offset + 1 : 0, last: Math.min(offset + evidencePageSize, items.length) };
}
