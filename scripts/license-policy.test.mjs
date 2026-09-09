import assert from "node:assert/strict";
import { test } from "node:test";
import { selectLicense, inspectNpmRoots } from "./license-policy.mjs";
import fs from "node:fs";
import { rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
test("SPDX preserves grouping, conjunctions and selectable permissive branches", () => {
  assert.equal(selectLicense("(MPL-2.0 OR Apache-2.0)"), "Apache-2.0");
  assert.equal(selectLicense("MIT AND Zlib"), "(MIT AND Zlib)");
  assert.throws(() => selectLicense("GPL-3.0 AND (MIT OR Apache-2.0)"));
  assert.throws(() => selectLicense("MIT AND (GPL-3.0 OR AGPL-3.0)"));
  for (const value of ["", "()", "MIT OR", "(MIT", "MIT WITH anything", "MIT/ISC", "MIT garbage"])
    assert.throws(() => selectLicense(value));
});
test("CC BY exception is restricted to the reviewed reference dataset version", () => {
  assert.equal(selectLicense("CC-BY-4.0", "caniuse-lite@1.0.30001810"), "CC-BY-4.0");
  assert.throws(() => selectLicense("CC-BY-4.0", "unreviewed-code@1.0"));
});
test("Missing dependency trees fail closed", () => assert.throws(() => inspectNpmRoots(["artifacts/nonexistent-license-test-tree"])));

test("installed dependency evidence survives an absent optional duplicate", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-licenses-"));
  t.after(() => rm(root, { recursive: true }));
  const roots = ["first", "second"].map(name => path.join(root, name, "node_modules"));
  for (const [index, directory] of roots.entries()) {
    fs.mkdirSync(path.join(directory, "required"), { recursive: true });
    fs.writeFileSync(path.join(directory, "required/package.json"), JSON.stringify({ name: "required", version: "1", license: "MIT" }));
    fs.writeFileSync(path.join(directory, "required/LICENSE"), "MIT evidence");
    if (index === 0) {
      fs.mkdirSync(path.join(directory, "optional"));
      fs.writeFileSync(path.join(directory, "optional/package.json"), JSON.stringify({ name: "optional", version: "1", license: "MIT" }));
      fs.writeFileSync(path.join(directory, "optional/LICENSE"), "MIT evidence");
    }
    fs.writeFileSync(path.join(directory, "../package-lock.json"), JSON.stringify({ lockfileVersion: 3, packages: {
      "node_modules/required": { version: "1", license: "MIT" },
      "node_modules/optional": { version: "1", license: "MIT", optional: true }
    } }));
  }
  assert.equal(inspectNpmRoots(roots).find(p => p.name === "optional").installed, true);
  fs.writeFileSync(path.join(roots[0], "required/package.json"), JSON.stringify({ name: "required", version: "2", license: "MIT" }));
  assert.throws(() => inspectNpmRoots(roots), /differs from lock/);
});
