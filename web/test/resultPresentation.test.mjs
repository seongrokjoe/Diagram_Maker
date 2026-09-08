import assert from "node:assert/strict";
import test from "node:test";
import { buildEvidenceItems, evidencePage, filterEvidenceItems } from "../src/resultPresentation.ts";

test("evidence deduplication preserves first occurrence and stable display numbers", () => {
  assert.deepEqual(buildEvidenceItems(["first", "second", "first", "third"]), [
    { id: "first", number: 1, contextLabel: "" },
    { id: "second", number: 2, contextLabel: "" },
    { id: "third", number: 3, contextLabel: "" },
  ]);
});

test("evidence context labels come only from linked nodes or edges", () => {
  const diagram = { nodes: [{ label: "CSV 내보내기", evidenceIds: ["shared"] }, { label: "검증", evidenceIds: ["shared"] }],
    edges: [{ label: "GetField 읽기", evidenceIds: ["call"] }] };
  assert.deepEqual(buildEvidenceItems(["shared", "call", "unknown"], diagram).map(item => item.contextLabel),
    ["CSV 내보내기", "GetField 읽기", ""]);
});

test("all 603 references remain accessible in bounded pages", () => {
  const items = buildEvidenceItems(Array.from({ length: 603 }, (_, index) => `evidence-${index + 1}`));
  const first = evidencePage(items, 0);
  assert.equal(first.items.length, 20);
  assert.equal(first.totalPages, 31);
  const visited = Array.from({ length: first.totalPages }, (_, pageIndex) => evidencePage(items, pageIndex).items).flat();
  assert.deepEqual(visited, items);
  const last = evidencePage(items, 30);
  assert.deepEqual([last.first, last.last, last.items.length], [601, 603, 3]);
});

test("search finds context, ID and original number without renumbering", () => {
  const items = buildEvidenceItems(["alpha", "beta"], { nodes: [{ label: "CSV Export", evidenceIds: ["beta"] }], edges: [] });
  for (const query of [" csv ", "BETA", "근거 2"]) {
    assert.deepEqual(filterEvidenceItems(items, query), [items[1]]);
  }
  assert.deepEqual(filterEvidenceItems(items, "missing"), []);
  assert.deepEqual(filterEvidenceItems(items, "  "), items);
});

test("empty and changed search results clamp the current page safely", () => {
  const empty = evidencePage([], 30);
  assert.deepEqual([empty.first, empty.last, empty.pageIndex, empty.totalPages], [0, 0, 0, 1]);
  const items = buildEvidenceItems(["single"]);
  assert.equal(evidencePage(items, 30).pageIndex, 0);
  assert.deepEqual(evidencePage(items, -1).items, items);
});
