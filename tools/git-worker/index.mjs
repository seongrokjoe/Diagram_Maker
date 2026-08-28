import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";
import git from "isomorphic-git";
import { structuredPatch } from "diff";

const MAX_STDERR = 8_000;

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

async function resolveRevision(dir, gitdir, revision) {
  if (!revision || typeof revision !== "string") throw new Error("Revision is required");
  try {
    return await git.resolveRef({ fs, dir, gitdir, ref: revision });
  } catch {
    return await git.expandOid({ fs, dir, gitdir, oid: revision });
  }
}

async function listTree(dir, gitdir, ref) {
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

function isText(buffer) {
  const sample = buffer.subarray(0, Math.min(buffer.length, 8_192));
  return !sample.includes(0);
}

function isSourceFile(filepath) {
  return /\.(cs|c|cc|cpp|cxx|h|hh|hpp)$/i.test(filepath);
}

async function readText(dir, gitdir, commitOid, filepath, maxBytes) {
  try {
    const { blob } = await git.readBlob({ fs, dir, gitdir, oid: commitOid, filepath });
    if (blob.length > maxBytes || !isText(blob)) return null;
    return new TextDecoder("utf-8", { fatal: false }).decode(blob);
  } catch {
    return null;
  }
}

async function collectContextFiles(dir, gitdir, revisionSha, entries, excludedPaths, maxFiles, maxBytes) {
  const candidates = [...entries.entries()]
    .filter(([filepath]) => isSourceFile(filepath) && !excludedPaths.has(filepath))
    .sort(([left], [right]) => left.localeCompare(right));
  const files = [];
  for (const [filepath, entry] of candidates.slice(0, maxFiles)) {
    const content = await readText(dir, gitdir, revisionSha, filepath, maxBytes);
    if (content !== null) files.push({ path: filepath, revisionSha, blobOid: entry.oid, content });
  }
  return { files, truncated: candidates.length > maxFiles };
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

export async function compare(input) {
  const dir = path.resolve(input.repositoryPath);
  const stat = fs.statSync(dir);
  if (!stat.isDirectory()) throw new Error("Repository path is not a directory");
  const gitdir = resolveGitdir(dir);
  const baseSha = await resolveRevision(dir, gitdir, input.baseRevision);
  const targetSha = await resolveRevision(dir, gitdir, input.targetRevision);
  const [baseEntries, targetEntries] = await Promise.all([listTree(dir, gitdir, baseSha), listTree(dir, gitdir, targetSha)]);
  const rawChanges = classifyChanges(baseEntries, targetEntries);
  if (rawChanges.length > input.maxChangedFiles) {
    throw new Error(`Changed file limit exceeded: ${rawChanges.length} > ${input.maxChangedFiles}`);
  }

  const files = [];
  for (const change of rawChanges) {
    const beforePath = change.previousPath ?? change.path;
    const [beforeContent, afterContent] = await Promise.all([
      change.before ? readText(dir, gitdir, baseSha, beforePath, input.maxTextFileBytes) : null,
      change.after ? readText(dir, gitdir, targetSha, change.path, input.maxTextFileBytes) : null,
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
  const [baseContext, targetContext] = await Promise.all([
    collectContextFiles(dir, gitdir, baseSha, baseEntries, excludedPaths, input.maxContextFiles ?? 200, input.maxContextFileBytes ?? input.maxTextFileBytes),
    collectContextFiles(dir, gitdir, targetSha, targetEntries, excludedPaths, input.maxContextFiles ?? 200, input.maxContextFileBytes ?? input.maxTextFileBytes),
  ]);

  return {
    baseSha,
    targetSha,
    files,
    contextFiles: [...baseContext.files, ...targetContext.files],
    contextFilesTruncated: baseContext.truncated || targetContext.truncated,
  };
}

export async function inspectRepository(input) {
  const dir = path.resolve(input.repositoryPath);
  const stat = fs.statSync(dir);
  if (!stat.isDirectory()) throw new Error("Repository path is not a directory");
  const gitdir = resolveGitdir(dir);
  const branches = (await git.listBranches({ fs, dir, gitdir })).sort();
  const headText = fs.readFileSync(path.join(gitdir, "HEAD"), "utf8").trim();
  const headRef = /^ref:\s*refs\/heads\/(.+)$/i.exec(headText)?.[1] ?? null;
  const currentBranch = await git.currentBranch({ fs, dir, gitdir, fullname: false });
  const defaultBranch = currentBranch ?? headRef ?? branches[0] ?? "main";
  const headSha = await resolveRevision(dir, gitdir, "HEAD");
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

async function main() {
  try {
    const input = await readInput();
    const result = input.command === "compare"
      ? await compare(input)
      : input.command === "inspect"
        ? await inspectRepository(input)
        : (() => { throw new Error("Unsupported command"); })();
    process.stdout.write(JSON.stringify(result));
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(message.slice(0, MAX_STDERR));
    process.exitCode = 1;
  }
}

const isEntrypoint = process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href;
if (isEntrypoint) {
  await main();
}
