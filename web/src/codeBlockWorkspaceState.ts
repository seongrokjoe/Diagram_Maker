import type { CodeBlockDraft, CodeBlockGroup, CodeBlockRun, CodeBlockView } from "./codeBlockTypes";

export function initialDraft(): CodeBlockDraft {
  const blockId = crypto.randomUUID();
  return { title: "", blocks: [{ id: blockId, language: "cpp", title: "블럭 1", code: "", description: "" }],
    groups: [{ id: crypto.randomUUID(), title: "그룹 1", blockIds: [blockId], enableThinking: false, enableUserRelations: false }], enableThinking: false };
}

// Only hydrate legacy input from a matching run; browsing old results must never replace the draft.
export function normalizeDraft(draft: CodeBlockDraft, matchingRun?: CodeBlockRun): CodeBlockDraft {
  const merged = draft.groups?.some(g => g.blockIds.length && matchingRun?.groups.length && !matchingRun.groups.some(r => r.id === g.id)) &&
    draft.blocks.every(b => matchingRun?.groups.some(g => g.blockIds.includes(b.id)));
  const savedGroups = merged ? [...matchingRun!.groups, ...draft.groups!.filter(g => !g.blockIds.length && !matchingRun!.groups.some(r => r.id === g.id))] : draft.groups;
  const groups = savedGroups?.length ? savedGroups : matchingRun?.groups.length ? matchingRun.groups.map(g => ({ ...g,
    views: g.views?.length ? g.views : matchingRun.results.find(r => r.groupId === g.id)?.views.map(v => v.selection) }))
    : [{ id: `group-${draft.blocks[0].id}`, title: "그룹 1", blockIds: draft.blocks.map(b => b.id) }];
  return { ...draft, groups: groups.map(g => ({ ...g, enableThinking: g.enableThinking ?? draft.enableThinking,
    enableUserRelations: g.enableUserRelations ?? Boolean(draft.relations?.some(r => g.blockIds.includes(r.fromBlockId))) })) };
}

export function nextTitle(prefix: string, titles: string[]): string {
  let number = 1;
  while (titles.includes(`${prefix} ${number}`)) number++;
  return `${prefix} ${number}`;
}

export function reorderGroupBlock(draft: CodeBlockDraft, groupId: string, blockId: string, delta: number): CodeBlockDraft {
  const group = draft.groups?.find(g => g.id === groupId);
  if (!group) return draft;
  const ids = [...group.blockIds]; const index = ids.indexOf(blockId);
  if (index < 0 || index + delta < 0 || index + delta >= ids.length) return draft;
  [ids[index], ids[index + delta]] = [ids[index + delta], ids[index]];
  const ordered = ids.map(id => draft.blocks.find(b => b.id === id)!);
  let slot = 0;
  return { ...draft, groups: draft.groups?.map(g => g.id === groupId ? { ...g, blockIds: ids } : g),
    blocks: draft.blocks.map(b => ids.includes(b.id) ? ordered[slot++] : b) };
}

export function moveBlock(groups: CodeBlockGroup[], blockId: string, target: CodeBlockGroup): CodeBlockGroup[] {
  const next = groups.map(g => ({ ...g, blockIds: g.blockIds.filter(id => id !== blockId) }));
  const existing = next.find(g => g.id === target.id);
  if (existing) existing.blockIds.push(blockId);
  else next.push({ ...target, blockIds: [blockId] });
  return next;
}

export function mergeGroups(groups: CodeBlockGroup[], sourceId: string, targetId: string): CodeBlockGroup[] {
  if (sourceId === targetId) return groups;
  const source = groups.find(g => g.id === sourceId);
  const target = groups.find(g => g.id === targetId);
  if (!source || !target) return groups;
  const views = [...(target.views ?? [])];
  for (const view of source.views ?? []) if (!views.some(v => v.diagramType === view.diagramType)) views.push(view);
  return groups.filter(g => g.id !== sourceId).map(g => g.id === targetId
    ? { ...g, blockIds: [...g.blockIds, ...source.blockIds], views } : g);
}

export function isSemanticPage(view: CodeBlockView, page: CodeBlockView["pages"][number]): boolean {
  return page.resultKind ? page.resultKind === "semantic" : view.llmStatus === "Semantic";
}

export type CodeBlockLocationSelection = { group: string; view: string; page: string };
export const typeLabels: Array<[string, string]> = [["flowchart", "Flow"], ["sequence", "Sequence"], ["class", "Class"], ["state", "State"], ["code-relation", "코드 관계도"]];
