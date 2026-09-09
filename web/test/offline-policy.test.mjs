import assert from "node:assert/strict";
import fs from "node:fs";
import { rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { assertApprovedBuildPath, assertLocalBuildPath } from "../offline-policy.mjs";

test("frontend build policy rejects cloud, relative and linked paths", async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-build-policy-"));
  t.after(() => rm(root, { recursive: true }));
  assert.throws(() => assertLocalBuildPath("relative/path"));
  assert.throws(() => assertLocalBuildPath(path.join(root, "OneDrive/source")));
  fs.mkdirSync(path.join(root, "actual"));
  fs.symlinkSync(path.join(root, "actual"), path.join(root, "linked"), "junction");
  assert.throws(() => assertLocalBuildPath(path.join(root, "linked/output")));
});

test("frontend build policy requires a file and confines output to exact roots", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-build-policy-"));
  t.after(() => rm(root, { recursive: true }));
  const previous = process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH;
  t.after(() => { if (previous === undefined) delete process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH; else process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH = previous; });
  process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH = path.join(root, "network.json");
  assert.throws(() => assertApprovedBuildPath(root));
  fs.writeFileSync(process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH, JSON.stringify({ LocalRoots: [root] }));
  assert.doesNotThrow(() => assertApprovedBuildPath(path.join(root, "output")));
  assert.throws(() => assertApprovedBuildPath(root + "-sibling"));
  fs.writeFileSync(process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH, JSON.stringify({ LocalRoots: [] }));
  assert.throws(() => assertApprovedBuildPath(root));
});
