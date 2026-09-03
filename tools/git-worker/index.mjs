import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { spawn } from "node:child_process";
import { pathToFileURL } from "node:url";
import git from "isomorphic-git";
import { structuredPatch } from "diff";
import { parseCppFile, resolveCppCalls } from "./cpp-indexer.mjs";

const MAX_STDERR = 8_000;
const MAX_GIT_STDERR = 64_000;
const DEFAULT_PROCESS_OUTPUT = 128 * 1024 * 1024;
const textDecoder = new TextDecoder("utf-8", { fatal: false });

export class WorkerError extends Error {
  constructor(errorCode, message, backend = null, cause = undefined) {
    super(message, { cause });
    this.name = "WorkerError";
    this.errorCode = errorCode;
    this.backend = backend;
  }
}

async function readInput() {
  let value = "";
  for await (const chunk of process.stdin) value += chunk;
  return JSON.parse(value);
}

function resolveGitdir(dir) {
  const marker = path.join(dir, ".git");
  if (fs.existsSync(marker) && fs.statSync(marker).isDirectory()) return marker;
  if (fs.existsSync(marker) && fs.statSync(marker).isFile()) {
    const match = /^gitdir:\s*(.+)$/im.exec(fs.readFileSync(marker, "utf8"));
    if (!match) throw new Error("The .git file is invalid");
    return path.resolve(dir, match[1].trim());
  }
  if (fs.existsSync(path.join(dir, "HEAD")) && fs.existsSync(path.join(dir, "objects"))) return dir;
  throw new Error("The directory is not a Git repository");
}

function normalizeBackend(value) {
  const backend = typeof value === "string" ? value.trim().toLowerCase() : "auto";
  if (["auto", "native", "isomorphic"].includes(backend)) return backend;
  throw new WorkerError("GIT_WORKER_FAILED", `Unsupported Git backend '${value}'.`);
}

function validateRepositoryDirectory(repositoryPath) {
  const dir = path.resolve(repositoryPath);
  let stat;
  try {
    stat = fs.statSync(dir);
  } catch (error) {
    throw new WorkerError("GIT_REPOSITORY_INVALID", `Repository path is unavailable: ${dir}`, null, error);
  }
  if (!stat.isDirectory()) {
    throw new WorkerError("GIT_REPOSITORY_INVALID", "Repository path is not a directory");
  }
  return dir;
}

function processError(error, executable) {
  if (error?.code === "ENOENT") {
    return new WorkerError("GIT_EXECUTABLE_NOT_FOUND", `Git executable was not found: ${executable}`, "native", error);
  }
  return new WorkerError("GIT_EXECUTABLE_UNAVAILABLE", `Git executable could not be started: ${error?.message ?? error}`, "native", error);
}

function runProcess(executable, args, options = {}) {
  const maxOutputBytes = options.maxOutputBytes ?? DEFAULT_PROCESS_OUTPUT;
  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawn(executable, args, {
        cwd: options.cwd,
        env: options.env,
        shell: false,
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"],
      });
    } catch (error) {
      reject(processError(error, executable));
      return;
    }

    const stdoutChunks = [];
    const stderrChunks = [];
    let stdoutLength = 0;
    let stderrLength = 0;
    let outputError = null;

    child.on("error", (error) => {
      if (!outputError) outputError = processError(error, executable);
    });
    child.stdout.on("data", (chunk) => {
      if (outputError) return;
      stdoutLength += chunk.length;
      if (stdoutLength > maxOutputBytes) {
        outputError = new WorkerError(
          "GIT_OUTPUT_LIMIT",
          `Git command output exceeded the ${maxOutputBytes} byte safety limit.`,
          "native",
        );
        child.kill();
        return;
      }
      stdoutChunks.push(chunk);
    });
    child.stderr.on("data", (chunk) => {
      if (stderrLength >= MAX_GIT_STDERR) return;
      const remaining = MAX_GIT_STDERR - stderrLength;
      const value = chunk.length <= remaining ? chunk : chunk.subarray(0, remaining);
      stderrChunks.push(value);
      stderrLength += value.length;
    });
    child.on("close", (exitCode) => {
      if (outputError) {
        reject(outputError);
        return;
      }
      resolve({
        exitCode: exitCode ?? -1,
        stdout: Buffer.concat(stdoutChunks, stdoutLength),
        stderr: Buffer.concat(stderrChunks, stderrLength).toString("utf8").trim(),
      });
    });

    child.stdin.on("error", () => { });
    child.stdin.end(options.input ?? undefined);
  });
}

async function runGit(input, dir, args, options = {}) {
  const executable = input.gitExecutable || "git";
  const gitArgs = dir ? ["-C", dir, ...args] : args;
  const environment = {
    ...process.env,
    GIT_OPTIONAL_LOCKS: "0",
    GIT_TERMINAL_PROMPT: "0",
    GIT_NO_REPLACE_OBJECTS: "1",
  };
  const result = await runProcess(executable, gitArgs, {
    input: options.input,
    maxOutputBytes: options.maxOutputBytes,
    env: environment,
  });
  const allowedExitCodes = options.allowedExitCodes ?? [0];
  if (!allowedExitCodes.includes(result.exitCode)) {
    const detail = result.stderr || `Git exited with code ${result.exitCode}.`;
    throw new WorkerError(options.errorCode ?? "GIT_COMMAND_FAILED", detail, "native");
  }
  return result;
}

function normalizePageNumber(value, fallback, maximum = Number.MAX_SAFE_INTEGER) {
  const number = Number(value ?? fallback);
  if (!Number.isFinite(number)) return fallback;
  return Math.max(0, Math.min(Math.trunc(number), maximum));
}

function parseCommitRecord(record) {
  const normalized = record.replace(/^\r?\n/, "").trim();
  if (!normalized) return null;
  const [sha, parents, authoredAt, message, authorName, authorEmail] = normalized.split("\x1f");
  if (!sha) return null;
  return {
    sha,
    parentShas: parents ? parents.split(" ").filter(Boolean) : [],
    authoredAt,
    message: message ?? "",
    authorName: authorName ?? "",
    authorEmail: authorEmail ?? "",
  };
}

function normalizedSearchValue(value) {
  return String(value ?? "").normalize("NFKC").toLocaleLowerCase();
}

function commitMatches(commit, query) {
  const needle = normalizedSearchValue(query);
  return [commit.sha, commit.message, commit.authorName, commit.authorEmail]
    .some((value) => normalizedSearchValue(value).includes(needle));
}

function searchCommitsNative(input, dir, revision, query, skip, limit) {
  const executable = input.gitExecutable || "git";
  const args = [
    "-C", dir, "log", "--date=iso-strict",
    "--format=%H%x1f%P%x1f%aI%x1f%s%x1f%an%x1f%ae%x1e",
    "--end-of-options", revision,
  ];
  const environment = {
    ...process.env,
    GIT_OPTIONAL_LOCKS: "0",
    GIT_TERMINAL_PROMPT: "0",
    GIT_NO_REPLACE_OBJECTS: "1",
  };
  return new Promise((resolve, reject) => {
    let child;
    try {
      child = spawn(executable, args, {
        env: environment,
        shell: false,
        windowsHide: true,
        stdio: ["ignore", "pipe", "pipe"],
      });
    } catch (error) {
      reject(processError(error, executable));
      return;
    }

    const matches = [];
    const stderrChunks = [];
    let stderrLength = 0;
    let pending = "";
    let matchedCount = 0;
    let stoppedAfterLimit = false;
    let processFailure = null;

    function accept(record) {
      const commit = parseCommitRecord(record);
      if (!commit || !commitMatches(commit, query)) return;
      if (matchedCount >= skip) matches.push(commit);
      matchedCount += 1;
      if (matches.length >= limit && !stoppedAfterLimit) {
        stoppedAfterLimit = true;
        child.kill();
      }
    }

    child.on("error", (error) => { processFailure ??= processError(error, executable); });
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => {
      if (stoppedAfterLimit) return;
      pending += chunk;
      let separator = pending.indexOf("\x1e");
      while (separator >= 0) {
        accept(pending.slice(0, separator));
        pending = pending.slice(separator + 1);
        if (stoppedAfterLimit) break;
        separator = pending.indexOf("\x1e");
      }
    });
    child.stderr.on("data", (chunk) => {
      if (stderrLength >= MAX_GIT_STDERR) return;
      const remaining = MAX_GIT_STDERR - stderrLength;
      const value = chunk.length <= remaining ? chunk : chunk.subarray(0, remaining);
      stderrChunks.push(value);
      stderrLength += value.length;
    });
    child.on("close", (exitCode) => {
      if (processFailure) {
        reject(processFailure);
        return;
      }
      if (!stoppedAfterLimit && pending) accept(pending);
      if (!stoppedAfterLimit && exitCode !== 0) {
        const detail = Buffer.concat(stderrChunks, stderrLength).toString("utf8").trim()
          || `Git exited with code ${exitCode ?? -1}.`;
        reject(new WorkerError("GIT_REVISION_NOT_FOUND", detail, "native"));
        return;
      }
      resolve(matches.slice(0, limit));
    });
  });
}

async function resolveRevisionNative(input, dir, revision) {
  if (!revision || typeof revision !== "string") {
    throw new WorkerError("GIT_REVISION_NOT_FOUND", "Revision is required.", "native");
  }
  const result = await runGit(
    input,
    dir,
    ["rev-parse", "--verify", "--end-of-options", `${revision}^{commit}`],
    { errorCode: "GIT_REVISION_NOT_FOUND", maxOutputBytes: 4_096 },
  );
  const oid = result.stdout.toString("ascii").trim().toLowerCase();
  if (!/^[0-9a-f]{40,64}$/.test(oid)) {
    throw new WorkerError("GIT_REVISION_NOT_FOUND", `Git returned an invalid object ID for revision '${revision}'.`, "native");
  }
  return oid;
}

async function listTreeNative(input, dir, revisionSha) {
  const result = await runGit(
    input,
    dir,
    ["ls-tree", "-r", "-l", "-z", "--full-tree", revisionSha],
    { errorCode: "GIT_OBJECT_UNREADABLE" },
  );
  const entries = new Map();
  for (const record of result.stdout.toString("utf8").split("\0")) {
    if (!record) continue;
    const separator = record.indexOf("\t");
    if (separator < 0) {
      throw new WorkerError("GIT_OBJECT_UNREADABLE", "Git returned an invalid tree record.", "native");
    }
    const [mode, type, oid, rawSize] = record.slice(0, separator).split(/\s+/);
    if (type !== "blob") continue;
    entries.set(record.slice(separator + 1).replaceAll("\\", "/"), {
      oid,
      mode,
      size: /^\d+$/.test(rawSize) ? Number(rawSize) : null,
    });
  }
  return entries;
}

function isText(buffer) {
  const sample = buffer.subarray(0, Math.min(buffer.length, 8_192));
  return !sample.includes(0);
}

function isSourceFile(filepath) {
  return /\.(cs|c|cc|cpp|cxx|h|hh|hpp)$/i.test(filepath);
}

function isCppSourceFile(filepath) {
  return /\.(c|cc|cpp|cxx|h|hh|hpp)$/i.test(filepath);
}

function normalizeRequestedTreePath(value) {
  if (typeof value !== "string" || value.length === 0 || value.length > 4_096) {
    throw new WorkerError("GIT_OBJECT_UNREADABLE", "A valid repository file path is required.");
  }
  const normalized = path.posix.normalize(value.replaceAll("\\", "/"));
  if (normalized === "." || normalized.startsWith("../") || path.posix.isAbsolute(normalized)) {
    throw new WorkerError("GIT_OBJECT_UNREADABLE", "The repository file path is outside the immutable tree.");
  }
  return normalized;
}

function createEvidenceSnippet(revisionSha, filepath, oid, content, requestedStart, requestedEnd) {
  const lines = content.replaceAll("\r", "").split("\n");
  const startLine = Math.min(lines.length, Math.max(1, Number(requestedStart) || 1));
  const endLine = Math.min(lines.length, Math.max(startLine, Number(requestedEnd) || startLine), startLine + 199);
  return {
    revisionSha,
    blobOid: oid,
    filePath: filepath,
    startLine,
    endLine,
    content: lines.slice(startLine - 1, endLine).join("\n"),
  };
}

function createHunks(pathname, before, after) {
  if (before === null || after === null) return [];
  return structuredPatch(pathname, pathname, before, after, "base", "target", { context: 3 }).hunks.map((hunk) => ({
    oldStart: hunk.oldStart,
    oldLines: hunk.oldLines,
    newStart: hunk.newStart,
    newLines: hunk.newLines,
    header: `@@ -${hunk.oldStart},${hunk.oldLines} +${hunk.newStart},${hunk.newLines} @@`,
  }));
}

export function classifyChanges(baseEntries, targetEntries) {
  const changed = [];
  const deleted = [];
  const added = [];
  const paths = new Set([...baseEntries.keys(), ...targetEntries.keys()]);

  for (const filepath of [...paths].sort()) {
    const before = baseEntries.get(filepath);
    const after = targetEntries.get(filepath);
    if (before?.oid === after?.oid) continue;
    if (!before && after) added.push({ path: filepath, after });
    else if (before && !after) deleted.push({ path: filepath, before });
    else changed.push({ path: filepath, changeKind: "Modified", before, after });
  }

  const usedAdded = new Set();
  const usedDeleted = new Set();
  for (const oldEntry of deleted) {
    const index = added.findIndex((candidate, candidateIndex) =>
      !usedAdded.has(candidateIndex) && candidate.after.oid === oldEntry.before.oid);
    if (index < 0) continue;
    usedAdded.add(index);
    usedDeleted.add(oldEntry.path);
    changed.push({
      path: added[index].path,
      previousPath: oldEntry.path,
      changeKind: "Renamed",
      before: oldEntry.before,
      after: added[index].after,
    });
  }

  for (const entry of deleted) {
    if (!usedDeleted.has(entry.path)) changed.push({ path: entry.path, changeKind: "Deleted", before: entry.before });
  }
  added.forEach((entry, index) => {
    if (!usedAdded.has(index)) changed.push({ path: entry.path, changeKind: "Added", after: entry.after });
  });
  return changed.sort((left, right) => left.path.localeCompare(right.path));
}

function contextCandidates(entries, excludedPaths, maxFiles) {
  const candidates = [...entries.entries()]
    .filter(([filepath]) => isSourceFile(filepath) && !excludedPaths.has(filepath))
    .sort(([left], [right]) => left.localeCompare(right));
  return { values: candidates.slice(0, maxFiles), truncated: candidates.length > maxFiles };
}

async function readNativeBlobs(input, dir, requests) {
  const requestedLimits = new Map();
  for (const [oid, maxBytes] of requests) {
    requestedLimits.set(oid, Math.max(requestedLimits.get(oid) ?? 0, maxBytes));
  }
  if (requestedLimits.size === 0) return { buffers: new Map(), sizes: new Map() };

  const oids = [...requestedLimits.keys()];
  const batchInput = Buffer.from(`${oids.join("\n")}\n`, "ascii");
  const check = await runGit(
    input,
    dir,
    ["cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)"],
    {
      input: batchInput,
      errorCode: "GIT_OBJECT_UNREADABLE",
      maxOutputBytes: Math.max(1_048_576, oids.length * 160),
    },
  );
  const lines = check.stdout.toString("ascii").trimEnd().split(/\r?\n/);
  if (lines.length !== oids.length) {
    throw new WorkerError("GIT_OBJECT_UNREADABLE", "Git returned an incomplete batch-check response.", "native");
  }

  const sizes = new Map();
  const readableOids = [];
  for (let index = 0; index < oids.length; index += 1) {
    const fields = lines[index].trim().split(" ");
    if (fields.length !== 3 || fields[1] !== "blob") {
      throw new WorkerError("GIT_OBJECT_UNREADABLE", `Git object ${oids[index]} is missing or is not a blob.`, "native");
    }
    const size = Number(fields[2]);
    if (!Number.isSafeInteger(size) || size < 0) {
      throw new WorkerError("GIT_OBJECT_UNREADABLE", `Git returned an invalid blob size for ${oids[index]}.`, "native");
    }
    sizes.set(oids[index], size);
    if (size <= requestedLimits.get(oids[index])) readableOids.push(oids[index]);
  }

  const buffers = new Map();
  if (readableOids.length === 0) return { buffers, sizes };
  const maximumOutput = readableOids.reduce((total, oid) => total + sizes.get(oid) + 128, 1_024);
  const content = await runGit(
    input,
    dir,
    ["cat-file", "--batch"],
    {
      input: Buffer.from(`${readableOids.join("\n")}\n`, "ascii"),
      errorCode: "GIT_OBJECT_UNREADABLE",
      maxOutputBytes: maximumOutput,
    },
  );

  let cursor = 0;
  for (const expectedOid of readableOids) {
    const newline = content.stdout.indexOf(10, cursor);
    if (newline < 0) {
      throw new WorkerError("GIT_OBJECT_UNREADABLE", "Git returned an invalid cat-file header.", "native");
    }
    const header = content.stdout.subarray(cursor, newline).toString("ascii").split(" ");
    const size = Number(header[2]);
    if (header.length !== 3 || header[0] !== expectedOid || header[1] !== "blob" || size !== sizes.get(expectedOid)) {
      throw new WorkerError("GIT_OBJECT_UNREADABLE", `Git returned an unexpected cat-file response for ${expectedOid}.`, "native");
    }
    const start = newline + 1;
    const end = start + size;
    if (end >= content.stdout.length || content.stdout[end] !== 10) {
      throw new WorkerError("GIT_OBJECT_UNREADABLE", `Git returned truncated content for ${expectedOid}.`, "native");
    }
    buffers.set(expectedOid, content.stdout.subarray(start, end));
    cursor = end + 1;
  }
  return { buffers, sizes };
}

function nativeText(blobResult, oid, maxBytes) {
  if (!oid || (blobResult.sizes.get(oid) ?? Number.MAX_SAFE_INTEGER) > maxBytes) return null;
  const buffer = blobResult.buffers.get(oid);
  if (!buffer || !isText(buffer)) return null;
  return textDecoder.decode(buffer);
}

async function compareNative(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const baseSha = await resolveRevisionNative(input, dir, input.baseRevision);
  const targetSha = await resolveRevisionNative(input, dir, input.targetRevision);
  const [baseEntries, targetEntries] = await Promise.all([
    listTreeNative(input, dir, baseSha),
    listTreeNative(input, dir, targetSha),
  ]);
  const rawChanges = classifyChanges(baseEntries, targetEntries);
  if (rawChanges.length > input.maxChangedFiles) {
    throw new WorkerError(
      "GIT_CHANGED_FILE_LIMIT",
      `Changed file limit exceeded: ${rawChanges.length} > ${input.maxChangedFiles}`,
      "native",
    );
  }

  const excludedPaths = new Set();
  for (const change of rawChanges) {
    excludedPaths.add(change.path);
    if (change.previousPath) excludedPaths.add(change.previousPath);
  }
  const maxContextFiles = input.maxContextFiles ?? 200;
  const maxContextFileBytes = input.maxContextFileBytes ?? input.maxTextFileBytes;
  const baseContext = contextCandidates(baseEntries, excludedPaths, maxContextFiles);
  const targetContext = contextCandidates(targetEntries, excludedPaths, maxContextFiles);

  const blobRequests = [];
  for (const change of rawChanges) {
    if (change.before) blobRequests.push([change.before.oid, input.maxTextFileBytes]);
    if (change.after) blobRequests.push([change.after.oid, input.maxTextFileBytes]);
  }
  for (const [, entry] of [...baseContext.values, ...targetContext.values]) {
    blobRequests.push([entry.oid, maxContextFileBytes]);
  }
  const blobs = await readNativeBlobs(input, dir, blobRequests);

  const files = rawChanges.map((change) => {
    const beforeContent = nativeText(blobs, change.before?.oid, input.maxTextFileBytes);
    const afterContent = nativeText(blobs, change.after?.oid, input.maxTextFileBytes);
    return {
      path: change.path,
      previousPath: change.previousPath ?? null,
      changeKind: change.changeKind,
      beforeBlobOid: change.before?.oid ?? null,
      afterBlobOid: change.after?.oid ?? null,
      hunks: createHunks(change.path, beforeContent, afterContent),
      beforeContent,
      afterContent,
    };
  });
  const contextFiles = [
    ...baseContext.values.map(([filepath, entry]) => ({
      path: filepath,
      revisionSha: baseSha,
      blobOid: entry.oid,
      content: nativeText(blobs, entry.oid, maxContextFileBytes),
    })),
    ...targetContext.values.map(([filepath, entry]) => ({
      path: filepath,
      revisionSha: targetSha,
      blobOid: entry.oid,
      content: nativeText(blobs, entry.oid, maxContextFileBytes),
    })),
  ].filter((entry) => entry.content !== null);

  return {
    baseSha,
    targetSha,
    files,
    contextFiles,
    contextFilesTruncated: baseContext.truncated || targetContext.truncated,
  };
}

function normalizedTreePath(basePath, value) {
  const normalized = path.posix.normalize(path.posix.join(
    path.posix.dirname(basePath),
    value.replaceAll("\\", "/"),
  ));
  return normalized.startsWith("../") || path.posix.isAbsolute(normalized) ? null : normalized;
}

function parseVcxProject(projectPath, content) {
  const members = [];
  const references = [];
  for (const match of content.matchAll(/<(ClCompile|ClInclude)\b[^>]*\bInclude\s*=\s*["']([^"']+)["']/gi)) {
    const member = normalizedTreePath(projectPath, match[2]);
    if (member) members.push(member);
  }
  for (const match of content.matchAll(/<ProjectReference\b[^>]*\bInclude\s*=\s*["']([^"']+)["']/gi)) {
    const reference = normalizedTreePath(projectPath, match[1]);
    if (reference) references.push(reference);
  }
  return { projectPath, members: [...new Set(members)], references: [...new Set(references)] };
}

async function readSelectedNativeFiles(input, dir, entries, paths, maxFileBytes) {
  const requests = paths
    .map((filepath) => [filepath, entries.get(filepath)])
    .filter(([, entry]) => entry && (entry.size === null || entry.size <= maxFileBytes));
  const blobs = await readNativeBlobs(input, dir, requests.map(([, entry]) => [entry.oid, maxFileBytes]));
  return requests.map(([filepath, entry]) => ({
    filepath,
    entry,
    content: nativeText(blobs, entry.oid, maxFileBytes),
  })).filter((item) => item.content !== null);
}

async function buildCppIndexNative(input, dir, comparison) {
  const changedCppPaths = comparison.files
    .flatMap((file) => [file.path, file.previousPath].filter(Boolean))
    .filter(isCppSourceFile);
  if (changedCppPaths.length === 0) {
    return {
      parserVersion: "tree-sitter-cpp-0.23.4/index-v2",
      targetSymbols: [],
      targetEdges: [],
      beforeChangedSymbols: [],
      diagnostics: [],
      ambiguousCallCount: 0,
      indexedFileCount: 0,
      indexedBytes: 0,
      truncated: false,
      projectPaths: [],
      excludedCalls: [],
      excludedCallCount: 0,
      excludedCallsTruncated: false,
    };
  }

  const targetEntries = await listTreeNative(input, dir, comparison.targetSha);
  const projectPaths = [...targetEntries.keys()].filter((filepath) => filepath.toLowerCase().endsWith(".vcxproj"));
  const projectFiles = await readSelectedNativeFiles(input, dir, targetEntries, projectPaths, 2_000_000);
  const projects = projectFiles.map((item) => parseVcxProject(item.filepath, item.content));
  const projectByPath = new Map(projects.map((project) => [project.projectPath, project]));
  const activeProjects = new Set(projects
    .filter((project) => project.members.some((member) => changedCppPaths.includes(member)))
    .map((project) => project.projectPath));
  const queue = [...activeProjects];
  while (queue.length > 0) {
    const project = projectByPath.get(queue.shift());
    for (const reference of project?.references ?? []) {
      if (projectByPath.has(reference) && !activeProjects.has(reference)) {
        activeProjects.add(reference);
        queue.push(reference);
      }
    }
  }

  const projectForFile = new Map();
  for (const project of projects) {
    if (activeProjects.size > 0 && !activeProjects.has(project.projectPath)) continue;
    for (const member of project.members) projectForFile.set(member, project.projectPath);
  }
  let candidates = [...projectForFile.keys()].filter((filepath) => isCppSourceFile(filepath) && targetEntries.has(filepath));
  if (candidates.length === 0 && changedCppPaths.length > 0) {
    const roots = new Set(changedCppPaths.map((filepath) => filepath.split("/", 1)[0]));
    candidates = [...targetEntries.keys()].filter((filepath) =>
      isCppSourceFile(filepath) && roots.has(filepath.split("/", 1)[0]));
  }
  candidates.sort((left, right) => left.localeCompare(right));

  const maxFiles = Math.max(1, Math.min(Number(input.maxIndexedFiles ?? 10_000), 10_000));
  const maxBytes = Math.max(1_000_000, Math.min(Number(input.maxIndexedBytes ?? 268_435_456), 268_435_456));
  const maxFileBytes = Math.max(1_024, Math.min(Number(input.maxSourceFileBytes ?? 1_000_000), 1_000_000));
  let selectedBytes = 0;
  const selected = [];
  for (const filepath of candidates) {
    const size = targetEntries.get(filepath)?.size;
    if (size === null || size === undefined || size > maxFileBytes || selectedBytes + size > maxBytes) continue;
    selected.push(filepath);
    selectedBytes += size;
    if (selected.length >= maxFiles) break;
  }

  const selectedFiles = await readSelectedNativeFiles(input, dir, targetEntries, selected, maxFileBytes);
  const parsedTarget = [];
  for (const file of selectedFiles) {
    parsedTarget.push(await parseCppFile(file.filepath, file.content, projectForFile.get(file.filepath) ?? null));
  }
  const target = resolveCppCalls(parsedTarget, input.indirectCallRules ?? []);
  const parsedBefore = [];
  for (const file of comparison.files) {
    const beforePath = file.previousPath ?? file.path;
    if (!isCppSourceFile(beforePath) || file.beforeContent === null) continue;
    parsedBefore.push(await parseCppFile(beforePath, file.beforeContent, projectForFile.get(file.path) ?? null));
  }

  return {
    parserVersion: "tree-sitter-cpp-0.23.4/index-v2",
    targetSymbols: target.symbols,
    targetEdges: target.edges,
    beforeChangedSymbols: parsedBefore.flatMap((file) => file.symbols),
    diagnostics: target.diagnostics,
    ambiguousCallCount: target.ambiguousCallCount,
    indexedFileCount: selected.length,
    indexedBytes: selectedBytes,
    truncated: selected.length < candidates.length,
    projectPaths: [...activeProjects].sort(),
    excludedCalls: target.excludedCalls,
    excludedCallCount: target.excludedCallCount,
    excludedCallsTruncated: target.excludedCallsTruncated,
  };
}

async function prepareNative(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const comparison = await compareNative(input);
  const cppIndex = await buildCppIndexNative(input, dir, comparison);
  return { comparison, cppIndex };
}

async function listCommitsNative(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const limit = Math.max(1, normalizePageNumber(input.limit, 50, 100));
  const skip = normalizePageNumber(input.skip, 0);
  const revision = input.exactRevision
    ? await resolveRevisionNative(input, dir, input.revision)
    : (input.revision || "HEAD");
  const query = String(input.query ?? "").trim().slice(0, 200);
  if (query) return searchCommitsNative(input, dir, revision, query, skip, limit);
  const args = ["log", `--max-count=${limit}`, `--skip=${skip}`, "--date=iso-strict", "--format=%H%x1f%P%x1f%aI%x1f%s%x1f%an%x1f%ae%x1e"];
  args.push("--end-of-options", revision);
  const result = await runGit(input, dir, args, { errorCode: "GIT_REVISION_NOT_FOUND", maxOutputBytes: 2_000_000 });
  return result.stdout.toString("utf8").split("\x1e").map(parseCommitRecord).filter(Boolean);
}

async function readEvidenceNative(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const revisionSha = await resolveRevisionNative(input, dir, input.revision);
  const filepath = normalizeRequestedTreePath(input.filePath);
  const entries = await listTreeNative(input, dir, revisionSha);
  const entry = entries.get(filepath);
  if (!entry) throw new WorkerError("GIT_OBJECT_UNREADABLE", "The evidence file does not exist in the selected revision.", "native");
  const maxBytes = Math.max(1_024, Math.min(Number(input.maxTextFileBytes ?? 1_000_000), 2_000_000));
  const blobs = await readNativeBlobs(input, dir, [[entry.oid, maxBytes]]);
  const content = nativeText(blobs, entry.oid, maxBytes);
  if (content === null) throw new WorkerError("GIT_OBJECT_UNREADABLE", "The evidence file is binary or exceeds the text limit.", "native");
  return createEvidenceSnippet(revisionSha, filepath, entry.oid, content, input.startLine, input.endLine);
}

async function inspectRepositoryNative(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const bareResult = await runGit(
    input,
    dir,
    ["rev-parse", "--is-bare-repository"],
    { errorCode: "GIT_REPOSITORY_INVALID", maxOutputBytes: 4_096 },
  );
  const branchesResult = await runGit(
    input,
    dir,
    ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
    { errorCode: "GIT_REPOSITORY_INVALID", maxOutputBytes: 16 * 1024 * 1024 },
  );
  const currentResult = await runGit(
    input,
    dir,
    ["symbolic-ref", "--quiet", "--short", "HEAD"],
    { errorCode: "GIT_REPOSITORY_INVALID", allowedExitCodes: [0, 1], maxOutputBytes: 16_384 },
  );
  const branches = branchesResult.stdout.toString("utf8").split(/\r?\n/).filter(Boolean).sort();
  const currentBranch = currentResult.exitCode === 0 ? currentResult.stdout.toString("utf8").trim() : null;
  const defaultBranch = currentBranch || branches[0] || "main";
  const headSha = await resolveRevisionNative(input, dir, "HEAD");
  const messageResult = await runGit(
    input,
    dir,
    ["show", "-s", "--format=%s", headSha],
    { errorCode: "GIT_OBJECT_UNREADABLE", maxOutputBytes: 1_048_576 },
  );
  return {
    normalizedPath: fs.realpathSync(dir),
    isBare: bareResult.stdout.toString("ascii").trim() === "true",
    defaultBranch,
    headSha,
    headMessage: messageResult.stdout.toString("utf8").trim().split(/\r?\n/, 1)[0] ?? "",
    branches,
  };
}

async function resolveRevisionIsomorphic(dir, gitdir, revision) {
  if (!revision || typeof revision !== "string") {
    throw new WorkerError("GIT_REVISION_NOT_FOUND", "Revision is required.", "isomorphic");
  }
  try {
    return await git.resolveRef({ fs, dir, gitdir, ref: revision });
  } catch (resolveError) {
    try {
      return await git.expandOid({ fs, dir, gitdir, oid: revision });
    } catch (expandError) {
      throw new WorkerError(
        "GIT_REVISION_NOT_FOUND",
        `Revision '${revision}' could not be resolved: ${expandError.message}`,
        "isomorphic",
        resolveError,
      );
    }
  }
}

async function listTreeIsomorphic(dir, gitdir, ref) {
  const entries = new Map();
  await git.walk({
    fs,
    dir,
    gitdir,
    trees: [git.TREE({ ref })],
    map: async (filepath, [entry]) => {
      if (filepath === "." || !entry) return;
      const type = await entry.type();
      if (type !== "blob") return;
      entries.set(filepath.replaceAll("\\", "/"), {
        oid: await entry.oid(),
        mode: await entry.mode(),
      });
    },
  });
  return entries;
}

async function readTextIsomorphic(dir, gitdir, commitOid, filepath, maxBytes) {
  try {
    const { blob } = await git.readBlob({ fs, dir, gitdir, oid: commitOid, filepath });
    if (blob.length > maxBytes || !isText(blob)) return null;
    return textDecoder.decode(blob);
  } catch {
    return null;
  }
}

async function collectContextFilesIsomorphic(dir, gitdir, revisionSha, candidates, maxBytes) {
  const files = [];
  for (const [filepath, entry] of candidates.values) {
    const content = await readTextIsomorphic(dir, gitdir, revisionSha, filepath, maxBytes);
    if (content !== null) files.push({ path: filepath, revisionSha, blobOid: entry.oid, content });
  }
  return { files, truncated: candidates.truncated };
}

async function compareIsomorphic(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const gitdir = resolveGitdir(dir);
  const baseSha = await resolveRevisionIsomorphic(dir, gitdir, input.baseRevision);
  const targetSha = await resolveRevisionIsomorphic(dir, gitdir, input.targetRevision);
  const [baseEntries, targetEntries] = await Promise.all([
    listTreeIsomorphic(dir, gitdir, baseSha),
    listTreeIsomorphic(dir, gitdir, targetSha),
  ]);
  const rawChanges = classifyChanges(baseEntries, targetEntries);
  if (rawChanges.length > input.maxChangedFiles) {
    throw new WorkerError(
      "GIT_CHANGED_FILE_LIMIT",
      `Changed file limit exceeded: ${rawChanges.length} > ${input.maxChangedFiles}`,
      "isomorphic",
    );
  }

  const files = [];
  for (const change of rawChanges) {
    const beforePath = change.previousPath ?? change.path;
    const [beforeContent, afterContent] = await Promise.all([
      change.before ? readTextIsomorphic(dir, gitdir, baseSha, beforePath, input.maxTextFileBytes) : null,
      change.after ? readTextIsomorphic(dir, gitdir, targetSha, change.path, input.maxTextFileBytes) : null,
    ]);
    files.push({
      path: change.path,
      previousPath: change.previousPath ?? null,
      changeKind: change.changeKind,
      beforeBlobOid: change.before?.oid ?? null,
      afterBlobOid: change.after?.oid ?? null,
      hunks: createHunks(change.path, beforeContent, afterContent),
      beforeContent,
      afterContent,
    });
  }

  const excludedPaths = new Set();
  for (const file of files) {
    excludedPaths.add(file.path);
    if (file.previousPath) excludedPaths.add(file.previousPath);
  }
  const baseCandidates = contextCandidates(baseEntries, excludedPaths, input.maxContextFiles ?? 200);
  const targetCandidates = contextCandidates(targetEntries, excludedPaths, input.maxContextFiles ?? 200);
  const [baseContext, targetContext] = await Promise.all([
    collectContextFilesIsomorphic(
      dir,
      gitdir,
      baseSha,
      baseCandidates,
      input.maxContextFileBytes ?? input.maxTextFileBytes,
    ),
    collectContextFilesIsomorphic(
      dir,
      gitdir,
      targetSha,
      targetCandidates,
      input.maxContextFileBytes ?? input.maxTextFileBytes,
    ),
  ]);

  return {
    baseSha,
    targetSha,
    files,
    contextFiles: [...baseContext.files, ...targetContext.files],
    contextFilesTruncated: baseContext.truncated || targetContext.truncated,
  };
}

async function inspectRepositoryIsomorphic(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const gitdir = resolveGitdir(dir);
  const branches = (await git.listBranches({ fs, dir, gitdir })).sort();
  const headText = fs.readFileSync(path.join(gitdir, "HEAD"), "utf8").trim();
  const headRef = /^ref:\s*refs\/heads\/(.+)$/i.exec(headText)?.[1] ?? null;
  const currentBranch = await git.currentBranch({ fs, dir, gitdir, fullname: false });
  const defaultBranch = currentBranch ?? headRef ?? branches[0] ?? "main";
  const headSha = await resolveRevisionIsomorphic(dir, gitdir, "HEAD");
  const { commit } = await git.readCommit({ fs, dir, gitdir, oid: headSha });
  return {
    normalizedPath: fs.realpathSync(dir),
    isBare: gitdir === dir,
    defaultBranch,
    headSha,
    headMessage: commit.message.trim().split(/\r?\n/, 1)[0] ?? "",
    branches,
  };
}

async function readEvidenceIsomorphic(input) {
  const dir = validateRepositoryDirectory(input.repositoryPath);
  const gitdir = resolveGitdir(dir);
  const revisionSha = await resolveRevisionIsomorphic(dir, gitdir, input.revision);
  const filepath = normalizeRequestedTreePath(input.filePath);
  const entries = await listTreeIsomorphic(dir, gitdir, revisionSha);
  const entry = entries.get(filepath);
  if (!entry) throw new WorkerError("GIT_OBJECT_UNREADABLE", "The evidence file does not exist in the selected revision.", "isomorphic");
  const maxBytes = Math.max(1_024, Math.min(Number(input.maxTextFileBytes ?? 1_000_000), 2_000_000));
  const content = await readTextIsomorphic(dir, gitdir, revisionSha, filepath, maxBytes);
  if (content === null) throw new WorkerError("GIT_OBJECT_UNREADABLE", "The evidence file is binary or exceeds the text limit.", "isomorphic");
  return createEvidenceSnippet(revisionSha, filepath, entry.oid, content, input.startLine, input.endLine);
}

function normalizeIsomorphicError(error) {
  if (error instanceof WorkerError) return error;
  const message = error instanceof Error ? error.message : String(error);
  if (/Could not read packfile|packfile may be missing|Packfile trailer mismatch/i.test(message)) {
    return new WorkerError("GIT_PACK_UNREADABLE", message, "isomorphic", error);
  }
  if (/not a Git repository|\.git file is invalid/i.test(message)) {
    return new WorkerError("GIT_REPOSITORY_INVALID", message, "isomorphic", error);
  }
  return new WorkerError("GIT_WORKER_FAILED", message, "isomorphic", error);
}

async function executeWithBackend(input, nativeOperation, isomorphicOperation) {
  const backend = normalizeBackend(input.backend);
  if (backend === "isomorphic") {
    try {
      return await isomorphicOperation(input);
    } catch (error) {
      throw normalizeIsomorphicError(error);
    }
  }
  try {
    return await nativeOperation(input);
  } catch (error) {
    if (backend === "auto" && error instanceof WorkerError && error.errorCode === "GIT_EXECUTABLE_NOT_FOUND") {
      try {
        return await isomorphicOperation(input);
      } catch (fallbackError) {
        throw normalizeIsomorphicError(fallbackError);
      }
    }
    throw error;
  }
}

export async function compare(input) {
  return executeWithBackend(input, compareNative, compareIsomorphic);
}

export async function inspectRepository(input) {
  return executeWithBackend(input, inspectRepositoryNative, inspectRepositoryIsomorphic);
}

export async function prepare(input) {
  return executeWithBackend(input, prepareNative, async (fallbackInput) => ({
    comparison: await compareIsomorphic(fallbackInput),
    cppIndex: {
      parserVersion: "fallback-none",
      targetSymbols: [], targetEdges: [], beforeChangedSymbols: [], diagnostics: ["C++ project indexing requires native Git."],
      ambiguousCallCount: 0, indexedFileCount: 0, indexedBytes: 0, truncated: true, projectPaths: [],
      excludedCalls: [], excludedCallCount: 0, excludedCallsTruncated: false,
    },
  }));
}

export async function listCommits(input) {
  return executeWithBackend(input, listCommitsNative, async (fallbackInput) => {
    const dir = validateRepositoryDirectory(fallbackInput.repositoryPath);
    const gitdir = resolveGitdir(dir);
    const limit = Math.max(1, normalizePageNumber(fallbackInput.limit, 50, 100));
    const skip = normalizePageNumber(fallbackInput.skip, 0);
    const ref = fallbackInput.exactRevision
      ? await resolveRevisionIsomorphic(dir, gitdir, fallbackInput.revision)
      : (fallbackInput.revision || "HEAD");
    const query = String(fallbackInput.query ?? "").trim().slice(0, 200);
    let depth = Math.max(skip + limit, 200);
    let values;
    let mapped;
    do {
      values = await git.log({ fs, dir, gitdir, ref, depth });
      mapped = values.map((item) => ({
      sha: item.oid,
      parentShas: item.commit.parent,
      authoredAt: new Date(item.commit.author.timestamp * 1000).toISOString(),
      message: item.commit.message.trim().split(/\r?\n/, 1)[0] ?? "",
        authorName: item.commit.author.name ?? "",
        authorEmail: item.commit.author.email ?? "",
      }));
      if (!query || mapped.filter((commit) => commitMatches(commit, query)).length >= skip + limit || values.length < depth) break;
      depth = Math.min(depth * 2, Number.MAX_SAFE_INTEGER);
    } while (true);
    const filtered = query ? mapped.filter((commit) => commitMatches(commit, query)) : mapped;
    return filtered.slice(skip, skip + limit);
  });
}

export async function readEvidence(input) {
  return executeWithBackend(input, readEvidenceNative, readEvidenceIsomorphic);
}

function serializeError(error) {
  const normalized = error instanceof WorkerError
    ? error
    : new WorkerError("GIT_WORKER_FAILED", error instanceof Error ? error.message : String(error));
  return JSON.stringify({
    errorCode: normalized.errorCode,
    backend: normalized.backend,
    message: normalized.message.slice(0, MAX_STDERR),
  });
}

async function main() {
  try {
    const input = await readInput();
    const result = input.command === "compare"
      ? await compare(input)
      : input.command === "inspect"
        ? await inspectRepository(input)
        : input.command === "prepare"
          ? await prepare(input)
          : input.command === "commits"
            ? await listCommits(input)
            : input.command === "evidence"
              ? await readEvidence(input)
            : (() => { throw new WorkerError("GIT_WORKER_FAILED", "Unsupported command"); })();
    process.stdout.write(JSON.stringify(result));
  } catch (error) {
    process.stderr.write(serializeError(error));
    process.exitCode = 1;
  }
}

const isEntrypoint = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isEntrypoint) {
  await main();
}
