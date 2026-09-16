import type { AnalysisGroupSelection } from "./types";

// Keep newly created empty drafts; remove only a group emptied by this action.
export function assignChange(groups: AnalysisGroupSelection[], changeId: string, destinationId?: string) {
  if (destinationId && !groups.some(group => group.id === destinationId)) return groups;
  return groups.flatMap(group => {
    const ids = group.changeIds.filter(id => id !== changeId);
    if (group.id === destinationId) ids.push(changeId);
    return group.changeIds.length > 0 && ids.length === 0 ? [] : [{ ...group, changeIds: ids }];
  });
}

export type GroupMemory = {
  groups: Record<string, AnalysisGroupSelection>;
  assignments: Record<string, string>;
  order: string[];
};
export const emptyGroupMemory = (): GroupMemory => ({ groups: {}, assignments: {}, order: [] });

export function rememberGroups(memory: GroupMemory, groups: AnalysisGroupSelection[]): GroupMemory {
  const next = { groups: { ...memory.groups }, assignments: { ...memory.assignments }, order: [...memory.order] };
  for (const group of groups) {
    next.groups[group.id] = { ...group, changeIds: [] };
    if (!next.order.includes(group.id)) next.order.push(group.id);
    for (const changeId of group.changeIds) next.assignments[changeId] = group.id;
  }
  return next;
}

export function restoreChange(groups: AnalysisGroupSelection[], changeId: string, memory: GroupMemory,
  fallback: AnalysisGroupSelection): AnalysisGroupSelection[] {
  if (groups.some(group => group.changeIds.includes(changeId))) return groups;
  const destinationId = memory.assignments[changeId];
  const destination = groups.find(group => group.id === destinationId) ?? memory.groups[destinationId] ?? fallback;
  if (groups.some(group => group.id === destination.id)) return assignChange(groups, changeId, destination.id);
  const restored = { ...destination, changeIds: [changeId] };
  const position = memory.order.indexOf(restored.id);
  const insert = position < 0 ? -1 : groups.findIndex(group => {
    const order = memory.order.indexOf(group.id);
    return order < 0 || order > position;
  });
  return insert < 0 ? [...groups, restored] : [...groups.slice(0, insert), restored, ...groups.slice(insert)];
}

export function forgetGroups(memory: GroupMemory, ids: string[], mergedInto?: string): GroupMemory {
  return {
    groups: Object.fromEntries(Object.entries(memory.groups).filter(([id]) => !ids.includes(id))),
    order: memory.order.filter(id => !ids.includes(id)),
    assignments: Object.fromEntries(Object.entries(memory.assignments).flatMap(([change, group]) =>
      ids.includes(group) ? mergedInto ? [[change, mergedInto]] : [] : [[change, group]])),
  };
}
export function sidebarWidth(value: number, containerWidth: number) {
  return Math.max(220, Math.min(560, Math.max(220, containerWidth - 352), Number.isFinite(value) ? value : 280));
}
