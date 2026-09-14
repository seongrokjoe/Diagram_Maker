import type { DiagramArtifact, DiagramEditDocument } from "./types";

export type ElementSelection = { kind: "node" | "edge"; id: string };
export const renderAlias = (id: string) => `n_${id.replace(/[^a-zA-Z0-9_]/g, "_")}`;
export const clampZoom = (value: number) => Math.min(16, Math.max(0.01, Math.round(value * 10000) / 10000));

export function deleteDiagramSelection(document: DiagramEditDocument, selected: ElementSelection[]): DiagramEditDocument {
  const nodes = new Set(selected.filter(item => item.kind === "node").map(item => item.id));
  const edges = new Set(selected.filter(item => item.kind === "edge").map(item => item.id));
  return { ...document, nodes: document.nodes.filter(node => !nodes.has(node.id)),
    edges: document.edges.filter(edge => !edges.has(edge.id) && !nodes.has(edge.sourceId) && !nodes.has(edge.targetId)) };
}

export function zoomScrollDelta(before: { left: number; top: number }, after: { left: number; top: number },
  cursor: { x: number; y: number }, oldZoom: number, newZoom: number) {
  return { x: after.left + (cursor.x - before.left) / oldZoom * newZoom - cursor.x,
    y: after.top + (cursor.y - before.top) / oldZoom * newZoom - cursor.y };
}

// Identity and emission order come from the IR, never from displayed label text.
export function renderElementMap(ir: DiagramArtifact["ir"]) {
  type Block = NonNullable<DiagramArtifact["ir"]["sequenceBlocks"]>[number];
  const visit = (blocks: Block[]): string[] => blocks.flatMap(block =>
    [...(block.kind === "message" && block.edgeId ? [block.edgeId] : []), ...visit(block.children)]);
  const nodeMap = ir.nodes.map(node => ({ id: node.id, alias: renderAlias(node.id) }));
  const order = ir.type === "sequence" ? ir.sequenceBlocks ? visit(ir.sequenceBlocks) :
    [...ir.edges].sort((a, b) => (a.sequenceIndex ?? Number.MAX_SAFE_INTEGER) - (b.sequenceIndex ?? Number.MAX_SAFE_INTEGER)).map(edge => edge.id) : ir.edges.map(edge => edge.id);
  if (new Set(nodeMap.map(node => node.alias)).size !== nodeMap.length || new Set(order).size !== ir.edges.length || order.length !== ir.edges.length)
    return null;
  const edges = order.map(id => ir.edges.find(edge => edge.id === id));
  if (edges.some(edge => !edge)) return null;
  return { nodes: nodeMap, edges: edges.map(edge => ({ id: edge!.id, alias: renderAlias(edge!.id),
    sourceAlias: renderAlias(edge!.sourceId), targetAlias: renderAlias(edge!.targetId) })) };
}
