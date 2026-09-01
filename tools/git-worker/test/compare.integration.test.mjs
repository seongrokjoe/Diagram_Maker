import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import git from "isomorphic-git";
import { compare, inspectRepository } from "../index.mjs";

const author = { name: "Test", email: "test@internal" };
const testDirectory = path.dirname(fileURLToPath(import.meta.url));

function removeTree(directory) {
  if (!fs.existsSync(directory)) return;
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory() && !entry.isSymbolicLink()) removeTree(entryPath);
    else fs.unlinkSync(entryPath);
  }
  fs.rmdirSync(directory);
}

async function createRepository(t, name) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), `diagram-maker-${name}-`));
  t.after(() => removeTree(dir));
  await git.init({ fs, dir, defaultBranch: "main" });
  return dir;
}

async function commitFile(dir, filepath, content, message) {
  const absolutePath = path.join(dir, filepath);
  fs.mkdirSync(path.dirname(absolutePath), { recursive: true });
  fs.writeFileSync(absolutePath, content);
  await git.add({ fs, dir, filepath });
  return git.commit({ fs, dir, message, author });
}

test("native Git compares immutable commits and auto falls back only when Git is missing", async (t) => {
  const dir = await createRepository(t, "native");
  await commitFile(dir, "한글Context.cs", "class Context { }\n", "context");
  const base = await commitFile(dir, "Service.cs", "class Service { void Run() {} }\n", "base");
  const target = await commitFile(
    dir,
    "Service.cs",
    "class Service { void Run() { Save(); } void Save() {} }\n",
    "target",
  );
  const input = {
    repositoryPath: dir,
    baseRevision: base,
    targetRevision: target,
    maxChangedFiles: 10,
    maxTextFileBytes: 10_000,
    maxContextFiles: 10,
    maxContextFileBytes: 10_000,
  };

  const nativeResult = await compare({ ...input, backend: "native" });
  assert.equal(nativeResult.baseSha, base);
  assert.equal(nativeResult.targetSha, target);
  assert.equal(nativeResult.files.length, 1);
  assert.equal(nativeResult.files[0].changeKind, "Modified");
  assert.match(nativeResult.files[0].afterContent, /Save/);
  assert.ok(nativeResult.files[0].hunks.length > 0);
  assert.ok(nativeResult.contextFiles.some((entry) => entry.path === "한글Context.cs"));

  const fallbackResult = await compare({
    ...input,
    backend: "auto",
    gitExecutable: path.join(dir, "missing-git-executable"),
  });
  assert.deepEqual(fallbackResult, nativeResult);

  const inspection = await inspectRepository({ repositoryPath: dir, backend: "native" });
  assert.equal(inspection.defaultBranch, "main");
  assert.equal(inspection.headSha, target);
  assert.equal(inspection.headMessage, "target");
  assert.ok(inspection.branches.includes("main"));

  await assert.rejects(
    compare({ ...input, backend: "native", targetRevision: "missing-revision" }),
    (error) => error?.errorCode === "GIT_REVISION_NOT_FOUND",
  );

  const worker = spawnSync(process.execPath, [path.resolve(testDirectory, "..", "index.mjs")], {
    input: JSON.stringify({ command: "compare", ...input, backend: "native", targetRevision: "missing-revision" }),
    encoding: "utf8",
    windowsHide: true,
  });
  assert.equal(worker.status, 1);
  const failure = JSON.parse(worker.stderr);
  assert.equal(failure.errorCode, "GIT_REVISION_NOT_FOUND");
  assert.equal(failure.backend, "native");
  assert.match(failure.message, /fatal:/i);
});

test("native Git reads repositories split across multiple packfiles", async (t) => {
  const dir = await createRepository(t, "multipack");
  await commitFile(dir, "Source.cs", "class Source { void Before() {} }\n", "base");
  const base = await git.resolveRef({ fs, dir, ref: "HEAD" });
  execFileSync("git", ["-C", dir, "repack", "-a", "-d", "--no-write-bitmap-index"], {
    windowsHide: true,
    stdio: "pipe",
  });
  const target = await commitFile(dir, "Source.cs", "class Source { void After() {} }\n", "target");

  execFileSync("git", ["-C", dir, "repack", "-d", "--no-write-bitmap-index"], {
    windowsHide: true,
    stdio: "pipe",
  });
  const packDirectory = path.join(dir, ".git", "objects", "pack");
  const packCount = fs.readdirSync(packDirectory).filter((name) => name.endsWith(".pack")).length;
  assert.ok(packCount > 1, `Expected multiple packfiles, found ${packCount}.`);

  const result = await compare({
    repositoryPath: dir,
    baseRevision: base,
    targetRevision: target,
    backend: "native",
    maxChangedFiles: 10,
    maxTextFileBytes: 10_000,
    maxContextFiles: 10,
    maxContextFileBytes: 10_000,
  });
  assert.equal(result.files.length, 1);
  assert.equal(result.files[0].path, "Source.cs");
  assert.match(result.files[0].afterContent, /After/);
});
