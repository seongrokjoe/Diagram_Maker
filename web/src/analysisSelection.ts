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
export function sidebarWidth(value: number, containerWidth: number) {
  return Math.max(220, Math.min(560, Math.max(220, containerWidth - 352), Number.isFinite(value) ? value : 280));
}
