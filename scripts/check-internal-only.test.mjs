import assert from "node:assert/strict";
import fs from "node:fs";
import { rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { checkPackage, scanInternalOnly } from "./check-internal-only.mjs";

test("release scan detects removed adapters in assemblies and bundled UI", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-package-"));
  t.after(() => rm(root, { recursive: true }));
  fs.writeFileSync(path.join(root, "old.dll"), Buffer.from("CodexCliCompletionTransport", "utf16le"));
  fs.writeFileSync(path.join(root, "app.js"), "SampleTestWorkspace");
  assert.equal(scanInternalOnly(root, { packaged: true }).length, 2);
  assert.ok(checkPackage(root).some(message => message.startsWith("Missing")));
});

test("release scan rejects nested runtime data and actual policy files", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "diagram-package-"));
  t.after(() => rm(root, { recursive: true }));
  fs.mkdirSync(path.join(root, "config/data"), { recursive: true });
  fs.writeFileSync(path.join(root, "config/network-policy.json"), "{}");
  fs.writeFileSync(path.join(root, "config/network-policy.example.json"), "{}");
  assert.equal(scanInternalOnly(root, { packaged: true }).length, 2);
});
