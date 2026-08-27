import test from "node:test";
import assert from "node:assert/strict";
import { classifyChanges } from "../index.mjs";

test("classifies exact blob rename deterministically", () => {
  const base = new Map([["old.cs", { oid: "abc", mode: 0o100644 }]]);
  const target = new Map([["new.cs", { oid: "abc", mode: 0o100644 }]]);
  assert.deepEqual(classifyChanges(base, target), [{
    path: "new.cs",
    previousPath: "old.cs",
    changeKind: "Renamed",
    before: base.get("old.cs"),
    after: target.get("new.cs"),
  }]);
});

test("classifies add delete and modify", () => {
  const base = new Map([
    ["same.cs", { oid: "old", mode: 0o100644 }],
    ["gone.cs", { oid: "gone", mode: 0o100644 }],
  ]);
  const target = new Map([
    ["same.cs", { oid: "new", mode: 0o100644 }],
    ["added.cs", { oid: "added", mode: 0o100644 }],
  ]);
  const result = classifyChanges(base, target);
  assert.equal(result.find((item) => item.path === "same.cs").changeKind, "Modified");
  assert.equal(result.find((item) => item.path === "gone.cs").changeKind, "Deleted");
  assert.equal(result.find((item) => item.path === "added.cs").changeKind, "Added");
});
