import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import git from "isomorphic-git";
import { compare, createHunks, inspectRepository, listCommits, prepare, readEvidence } from "../index.mjs";

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

async function commitFile(dir, filepath, content, message, commitAuthor = author) {
  const absolutePath = path.join(dir, filepath);
  fs.mkdirSync(path.dirname(absolutePath), { recursive: true });
  fs.writeFileSync(absolutePath, content);
  await git.add({ fs, dir, filepath });
  return git.commit({ fs, dir, message, author: commitAuthor });
}

test("diff hunks retain only contiguous actual changed line ranges", () => {
  const hunks = createHunks("Service.cs",
    "one\nremove-a\nremove-b\ncontext\nold-tail\n",
    "one\nadd-a\ncontext\nnew-tail-a\nnew-tail-b\n");

  assert.deepEqual(hunks.flatMap((hunk) => hunk.changedRanges), [
    { oldStartLine: 2, oldLineCount: 2, newStartLine: 2, newLineCount: 1 },
    { oldStartLine: 5, oldLineCount: 1, newStartLine: 4, newLineCount: 2 },
  ]);
});

test("diff hunks expose whole-file additions without a base blob", () => {
  const ranges = createHunks("Added.cs", null, "first\nsecond\n").flatMap((hunk) => hunk.changedRanges);
  assert.deepEqual(ranges, [{ oldStartLine: null, oldLineCount: 0, newStartLine: 1, newLineCount: 2 }]);
});

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

test("prepares a Visual Studio C++ project with resolved calls and commit metadata", async (t) => {
  const dir = await createRepository(t, "cpp-plan");
  const project = `<Project><ItemGroup><ClCompile Include="src\\Service.cpp" /></ItemGroup></Project>`;
  fs.mkdirSync(path.join(dir, "src"), { recursive: true });
  fs.writeFileSync(path.join(dir, "Demo.vcxproj"), project);
  fs.writeFileSync(path.join(dir, "src", "Service.cpp"), `
    namespace Demo {
      class Service { public: void Run(); void Save(); };
      void Service::Run() { Save(); }
      void Service::Save() { }
    }
  `);
  await git.add({ fs, dir, filepath: "Demo.vcxproj" });
  await git.add({ fs, dir, filepath: "src/Service.cpp" });
  const base = await git.commit({ fs, dir, message: "base C++ project", author });
  fs.writeFileSync(path.join(dir, "src", "Service.cpp"), `
    namespace Demo {
      class Service { public: void Run(); void Save(); };
      void Service::Run() { Save(); Save(); }
      void Service::Save() { }
    }
  `);
  await git.add({ fs, dir, filepath: "src/Service.cpp" });
  const target = await git.commit({ fs, dir, message: "call Save", author });

  const result = await prepare({
    repositoryPath: dir,
    baseRevision: base,
    targetRevision: target,
    backend: "native",
    maxChangedFiles: 10,
    maxTextFileBytes: 100_000,
    maxContextFiles: 10,
    maxContextFileBytes: 100_000,
    maxIndexedFiles: 100,
    maxIndexedBytes: 10_000_000,
    maxSourceFileBytes: 1_000_000,
  });
  assert.deepEqual(result.cppIndex.projectPaths, ["Demo.vcxproj"]);
  assert.ok(result.cppIndex.targetSymbols.some((symbol) => symbol.qualifiedName.endsWith("Service::Run")));
  assert.ok(result.cppIndex.targetEdges.some((edge) => edge.type === "calls"));
  assert.ok(result.cppIndex.baseEdges.some((edge) => edge.type === "calls"));
  assert.match(result.cppIndex.parserVersion, /index-v4$/);
  assert.equal(result.cppIndex.ambiguousCallCount, 0);

  const commits = await listCommits({ repositoryPath: dir, backend: "native", limit: 2 });
  assert.equal(commits[0].sha, target);
  assert.deepEqual(commits[0].parentShas, [base]);
  assert.equal(commits[0].message, "call Save");
  assert.equal(commits[0].authorName, author.name);
  assert.equal(commits[0].authorEmail, author.email);

  const evidence = await readEvidence({
    repositoryPath: dir,
    backend: "native",
    revision: target,
    filePath: "src/Service.cpp",
    startLine: 3,
    endLine: 5,
    maxTextFileBytes: 100_000,
  });
  assert.equal(evidence.revisionSha, target);
  assert.match(evidence.content, /Service/);
  await assert.rejects(
    readEvidence({ repositoryPath: dir, backend: "native", revision: target, filePath: "../secret.txt" }),
    (error) => error?.errorCode === "GIT_OBJECT_UNREADABLE",
  );
});

test("lists and searches commits beyond the initial page on both Git backends", async (t) => {
  const dir = await createRepository(t, "commit-pages");
  const shas = [];
  let historicSha = "";
  for (let index = 0; index < 65; index += 1) {
    const historic = index === 4;
    const commitAuthor = historic
      ? { name: "Archive User", email: "archive@internal" }
      : author;
    const sha = await commitFile(
      dir,
      "history.txt",
      `${index}\n`,
      historic ? "historic architecture needle" : `routine change ${index}`,
      commitAuthor,
    );
    shas.push(sha);
    if (historic) historicSha = sha;
  }

  for (const backend of ["native", "isomorphic"]) {
    const older = await listCommits({ repositoryPath: dir, backend, revision: "main", skip: 50, limit: 10 });
    assert.equal(older.length, 10);
    assert.equal(older[0].sha, shas[14]);

    const byMessage = await listCommits({ repositoryPath: dir, backend, revision: "main", query: "architecture needle", limit: 5 });
    assert.deepEqual(byMessage.map((commit) => commit.sha), [historicSha]);

    const byAuthor = await listCommits({ repositoryPath: dir, backend, revision: "main", query: "archive@internal", limit: 5 });
    assert.deepEqual(byAuthor.map((commit) => commit.sha), [historicSha]);

    const bySha = await listCommits({ repositoryPath: dir, backend, revision: "main", query: historicSha.slice(0, 12), limit: 5 });
    assert.deepEqual(bySha.map((commit) => commit.sha), [historicSha]);
  }

  const resolved = await listCommits({
    repositoryPath: dir,
    backend: "native",
    revision: historicSha.slice(0, 12),
    exactRevision: true,
    limit: 1,
  });
  assert.equal(resolved[0].sha, historicSha);
});

test("does not index unrelated C++ projects for a non-C++ change", async (t) => {
  const dir = await createRepository(t, "non-cpp-plan");
  fs.mkdirSync(path.join(dir, "native"), { recursive: true });
  fs.writeFileSync(
    path.join(dir, "Demo.vcxproj"),
    '<Project><ItemGroup><ClCompile Include="native\\Service.cpp" /></ItemGroup></Project>',
  );
  fs.writeFileSync(path.join(dir, "native", "Service.cpp"), "void Unrelated() {}\n");
  fs.writeFileSync(path.join(dir, "Service.cs"), "class Service { void Before() {} }\n");
  await git.add({ fs, dir, filepath: "Demo.vcxproj" });
  await git.add({ fs, dir, filepath: "native/Service.cpp" });
  await git.add({ fs, dir, filepath: "Service.cs" });
  const base = await git.commit({ fs, dir, message: "mixed project", author });

  fs.writeFileSync(path.join(dir, "Service.cs"), "class Service { void After() {} }\n");
  await git.add({ fs, dir, filepath: "Service.cs" });
  const target = await git.commit({ fs, dir, message: "C# only change", author });

  const result = await prepare({
    repositoryPath: dir,
    baseRevision: base,
    targetRevision: target,
    backend: "native",
    maxChangedFiles: 10,
    maxTextFileBytes: 100_000,
    maxContextFiles: 10,
    maxContextFileBytes: 100_000,
    maxIndexedFiles: 100,
    maxIndexedBytes: 10_000_000,
    maxSourceFileBytes: 1_000_000,
  });

  assert.equal(result.cppIndex.indexedFileCount, 0);
  assert.deepEqual(result.cppIndex.targetSymbols, []);
  assert.deepEqual(result.cppIndex.projectPaths, []);
});
