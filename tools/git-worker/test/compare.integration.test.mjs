import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import git from "isomorphic-git";
import { compare, inspectRepository } from "../index.mjs";

test("compares two immutable commits without checkout", async () => {
  const testDirectory = path.dirname(fileURLToPath(import.meta.url));
  const dir = path.join(testDirectory, ".test-worktree");
  fs.mkdirSync(dir, { recursive: true });
  if (!fs.existsSync(path.join(dir, ".git"))) {
    await git.init({ fs, dir, defaultBranch: "main" });
  }
  fs.writeFileSync(path.join(dir, "Service.cs"), "class Service { void Run() {} }\n");
  await git.add({ fs, dir, filepath: "Service.cs" });
  const base = await git.commit({ fs, dir, message: "base", author: { name: "Test", email: "test@internal" } });

  fs.writeFileSync(path.join(dir, "Service.cs"), "class Service { void Run() { Save(); } void Save() {} }\n");
  await git.add({ fs, dir, filepath: "Service.cs" });
  const target = await git.commit({ fs, dir, message: "target", author: { name: "Test", email: "test@internal" } });

  const result = await compare({
    repositoryPath: dir,
    baseRevision: base,
    targetRevision: target,
    maxChangedFiles: 10,
    maxTextFileBytes: 10_000,
  });
  assert.equal(result.baseSha, base);
  assert.equal(result.targetSha, target);
  assert.equal(result.files.length, 1);
  assert.equal(result.files[0].changeKind, "Modified");
  assert.match(result.files[0].afterContent, /Save/);
  assert.ok(result.files[0].hunks.length > 0);

  const inspection = await inspectRepository({ repositoryPath: dir });
  assert.equal(inspection.defaultBranch, "main");
  assert.equal(inspection.headSha, target);
  assert.equal(inspection.headMessage, "target");
  assert.ok(inspection.branches.includes("main"));
});
