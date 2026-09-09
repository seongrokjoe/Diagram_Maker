import assert from "node:assert/strict";
import test from "node:test";
import { initialDraft, normalizeDraft, reorderGroupBlock, moveBlock, mergeGroups, isSemanticPage } from "../src/codeBlockWorkspaceState.ts";
const groups = [
  { id: "a", title: "입력", blockIds: ["one", "two"], views: [{ id: "flow-a", diagramType: "flowchart", presetId: "compact", refinementInstruction: "조건 설명" }] },
  { id: "b", title: "출력", blockIds: ["three"], views: [{ id: "flow-b", diagramType: "flowchart", presetId: "detailed" }, { id: "state-b", diagramType: "state", presetId: "balanced" }] }
];
test("moving and splitting preserve every existing group output option", () => {
  const moved = moveBlock(groups, "two", groups[1]);
  assert.deepEqual(moved.map(g => g.views), groups.map(g => g.views));
  assert.deepEqual(moved.map(g => g.blockIds), [["one"], ["three", "two"]]);
  const split = moveBlock(moved, "two", { id: "c", title: "독립", blockIds: [] });
  assert.deepEqual(split.slice(0, 2).map(g => g.views), groups.map(g => g.views));
  assert.deepEqual(groups[0].blockIds, ["one", "two"]);
});
test("merge retains target presets and all distinct source output types", () => {
  const merged = mergeGroups(groups, "b", "a");
  assert.equal(merged.length, 1);
  assert.deepEqual(merged[0].views, [groups[0].views[0], groups[1].views[1]]);
});
test("old static pages and retained semantic pages are classified independently of latest failure", () => {
  assert.equal(isSemanticPage({ llmStatus: "Incomplete" }, {}), false);
  assert.equal(isSemanticPage({ llmStatus: "Deterministic" }, {}), false);
  assert.equal(isSemanticPage({ llmStatus: "Incomplete" }, { resultKind: "semantic" }), true);
});

test("new drafts require a title and start with exactly one explicit group and block", () => {
  const draft = initialDraft();
  assert.equal(draft.title, "");
  assert.equal(draft.blocks.length, 1);
  assert.deepEqual(draft.groups[0].blockIds, [draft.blocks[0].id]);
  assert.equal(draft.groups[0].enableThinking, false);
  assert.equal(draft.groups[0].enableUserRelations, false);
});

test("moving the last block preserves an empty group and each group's options", () => {
  const input = [{ ...groups[0], blockIds: ["one"], enableThinking: true, enableUserRelations: false },
    { ...groups[1], blockIds: [], enableThinking: false, enableUserRelations: true }];
  const moved = moveBlock(input, "one", input[1]);
  assert.equal(moved.length, 2);
  assert.deepEqual(moved[0].blockIds, []);
  assert.equal(moved[0].enableThinking, true);
  assert.equal(moved[1].enableThinking, false);
  const merged = mergeGroups(moved, "a", "b");
  assert.equal(merged[0].enableThinking, false);
  assert.equal(merged[0].enableUserRelations, true);
});

test("reordering a group changes only its block slots and never crosses a group boundary", () => {
  const draft = { title: "order", blocks: ["one", "three", "two"].map(id => ({ id })), groups };
  const ordered = reorderGroupBlock(draft, "a", "two", -1);
  assert.deepEqual(ordered.blocks.map(b => b.id), ["two", "three", "one"]);
  assert.deepEqual(ordered.groups[0].blockIds, ["two", "one"]);
  assert.deepEqual(ordered.groups[1], groups[1]);
  assert.equal(reorderGroupBlock(ordered, "a", "two", -1), ordered);
});

test("legacy groups and global options hydrate once while saved groups and empty groups survive", () => {
  const legacy = { title: "old", blocks: ["one", "two", "three"].map(id => ({ id })), enableThinking: true,
    relations: [{ fromBlockId: "one", toBlockId: "two" }] };
  const restored = normalizeDraft(legacy, { groups, results: [] });
  assert.equal(restored.groups.length, 2);
  assert.equal(restored.groups[0].enableThinking, true);
  assert.equal(restored.groups[0].enableUserRelations, true);
  assert.equal(restored.groups[1].enableUserRelations, false);
  assert.equal(normalizeDraft(legacy).groups.length, 1);
  const saved = { ...restored, groups: [...restored.groups, { id: "empty", title: "Empty", blockIds: [], enableThinking: false }] };
  assert.deepEqual(normalizeDraft(saved, { groups, results: [] }).groups.map(g => g.id), ["a", "b", "empty"]);
  const merged = normalizeDraft(saved, { groups: mergeGroups(restored.groups, "b", "a"), results: [] });
  assert.deepEqual(merged.groups.map(g => g.id), ["a", "empty"]);
  assert.deepEqual(merged.groups[0].blockIds, ["one", "two", "three"]);
});
