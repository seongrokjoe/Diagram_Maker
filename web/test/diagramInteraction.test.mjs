import assert from "node:assert/strict";
import test from "node:test";
import { canStartCanvasPan, clampZoom, deleteDiagramSelection, memberSelectionId, parseMemberSelectionId,
  renderElementMap, zoomScrollDelta, isZoomWheel } from "../src/diagramInteraction.ts";

test("ordinary scrolling never zooms; Ctrl and a vertical wheel delta are required", () => {
  assert.equal(isZoomWheel({ ctrlKey: false, deltaY: 100 }), false);
  assert.equal(isZoomWheel({ ctrlKey: true, deltaY: 0 }), false);
  assert.equal(isZoomWheel({ ctrlKey: true, deltaY: -100 }), true);
  assert.equal(isZoomWheel({ ctrlKey: true, deltaY: 100 }), true);
});

test("view drag pans everywhere while edit drag requires blank space or Space", () => {
  const base = { zoomable: true, compact: false, button: 0, spaceHeld: false };
  assert.equal(canStartCanvasPan({ ...base, editMode: false, overDiagramElement: true }), true);
  assert.equal(canStartCanvasPan({ ...base, editMode: true, overDiagramElement: false }), true);
  assert.equal(canStartCanvasPan({ ...base, editMode: true, overDiagramElement: true }), false);
  assert.equal(canStartCanvasPan({ ...base, editMode: true, overDiagramElement: true, spaceHeld: true }), true);
  assert.equal(canStartCanvasPan({ ...base, editMode: false, overDiagramElement: false, button: 2 }), false);
  assert.equal(canStartCanvasPan({ ...base, editMode: false, overDiagramElement: false, compact: true }), false);
});

test("class member selection IDs preserve node IDs containing separators", () => {
  const id = memberSelectionId("namespace:type::member", 12);
  assert.deepEqual(parseMemberSelectionId(id), { nodeId: "namespace:type::member", index: 12 });
  assert.equal(parseMemberSelectionId("ordinary-node"), null);
});

for (const type of ["flowchart", "sequence", "class", "code-relation", "state"]) {
  test(`${type}: deleting nodes removes every incident edge, including self calls, and permits empty draft`, () => {
    const original = { title: type, nodes: [{ id: "a", label: "same" }, { id: "b", label: "same" }], edges: [
      { id: "ab", sourceId: "a", targetId: "b" }, { id: "ba", sourceId: "b", targetId: "a" }, { id: "aa", sourceId: "a", targetId: "a" }] };
    const next = deleteDiagramSelection(original, [{ kind: "node", id: "a" }]);
    assert.deepEqual(next.nodes, [original.nodes[1]]);
    assert.deepEqual(next.edges, []);
    assert.equal(original.nodes.length, 2); // Undo can retain the immutable original.
    assert.deepEqual(deleteDiagramSelection(next, [{ kind: "node", id: "b" }]).nodes, []);
  });
}

test("zoom keeps the same diagram point under the cursor after centering/layout changes", () => {
  const delta = zoomScrollDelta({ left: 100, top: 60 }, { left: 50, top: 60 }, { x: 300, y: 260 }, 1, 2);
  assert.deepEqual(delta, { x: 150, y: 200 });
  assert.equal(50 - delta.x + 200 * 2, 300);
  assert.equal(60 - delta.y + 200 * 2, 260);
  assert.equal(clampZoom(20), 16);
  assert.equal(clampZoom(0), 0.01);
});

test("render map uses block emission order for repeated equal-label calls", () => {
  const nodes = [{ id: "a", label: "same" }, { id: "b", label: "same" }];
  const edge = id => ({ id, sourceId: "a", targetId: "b", label: "same" });
  const message = edgeId => ({ id: edgeId, kind: "message", edgeId, children: [] });
  const ir = { type: "sequence", nodes, edges: [edge("second"), edge("first")], sequenceBlocks: [message("first"), message("second")] };
  const map = renderElementMap(ir);
  assert.deepEqual(map.edges.map(edge => edge.id), ["first", "second"]);
  assert.notEqual(map.nodes[0].alias, map.nodes[1].alias);
  assert.equal(renderElementMap({ ...ir, sequenceBlocks: [message("first")] }), null);
  assert.equal(renderElementMap({ ...ir, sequenceBlocks: [message("first"), message("first")] }), null);
});
