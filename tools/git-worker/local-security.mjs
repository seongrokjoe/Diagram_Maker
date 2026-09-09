import fs from "node:fs";
import path from "node:path";

export function assertLocalPath(value) {
  if (!path.isAbsolute(value) || /^[/\\]{2}/.test(value)) throw new Error("An absolute local path is required.");
  const full = path.resolve(value);
  if (process.env.DIAGRAMMAKER_LOCAL_ROOTS) {
    const roots = JSON.parse(process.env.DIAGRAMMAKER_LOCAL_ROOTS);
    if (!roots.some(root => {
      const relative = path.relative(path.resolve(root), full);
      return relative === "" || (relative !== ".." && !relative.startsWith(".." + path.sep) && !path.isAbsolute(relative));
    })) throw new Error("Git metadata is outside the approved local roots.");
  }
  if (full.split(/[/\\]/).some(p => /^OneDrive(?:$| - )/i.test(p))) throw new Error("Cloud synchronized paths are prohibited.");
  for (const key of ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"]) {
    const cloud = process.env[key];
    if (cloud) {
      const relative = path.relative(path.resolve(cloud), full);
      if (relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative))) throw new Error("Cloud synchronized paths are prohibited.");
    }
  }
  for (let current = full; ; current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked local paths are prohibited.");
    if (current === path.dirname(current)) break;
  }
  return full;
}

export function gitEnvironment(source = process.env) {
  const result = {};
  for (const key of ["SystemRoot", "WINDIR", "COMSPEC", "PATH", "PATHEXT", "TEMP", "TMP", "LANG", "LC_ALL"])
    if (source[key] !== undefined) result[key] = source[key];
  return { ...result, GIT_CONFIG_NOSYSTEM: "1", GIT_CONFIG_GLOBAL: "/dev/null",
    GIT_ALLOW_PROTOCOL: "", GIT_NO_LAZY_FETCH: "1", GIT_OPTIONAL_LOCKS: "0", GIT_TERMINAL_PROMPT: "0",
    GIT_NO_REPLACE_OBJECTS: "1", GIT_PAGER: "", GIT_ATTR_NOSYSTEM: "1" };
}

export function validateGitStorage(repository) {
  assertLocalPath(repository);
  const marker = path.join(repository, ".git");
  let gitdir = fs.existsSync(marker) ? marker : repository;
  assertLocalPath(gitdir);
  if (fs.statSync(gitdir).isFile()) {
    const match = /^gitdir:\s*(.+)$/im.exec(fs.readFileSync(gitdir, "utf8"));
    if (!match) throw new Error("Invalid Git directory marker.");
    gitdir = assertLocalPath(path.resolve(repository, match[1].trim()));
  }
  const commonFile = path.join(gitdir, "commondir");
  let common = gitdir;
  if (fs.existsSync(commonFile)) {
    assertLocalPath(commonFile);
    common = assertLocalPath(path.resolve(gitdir, fs.readFileSync(commonFile, "utf8").trim()));
  }
  for (const root of new Set([gitdir, common])) {
    // Git itself may follow object/config links even when all transport protocols
    // are disabled. Reject these before invoking either backend.
    const pending = [root];
    while (pending.length) {
      const directory = pending.pop();
      for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const item = path.join(directory, entry.name);
        if (entry.isSymbolicLink()) throw new Error("Linked Git metadata is prohibited.");
        if (entry.isDirectory()) pending.push(item);
      }
    }
    for (const name of ["config", "config.worktree"]) {
      const config = path.join(root, name);
      if (fs.existsSync(config) && /^\s*\[\s*include(?:if)?(?:\s|\])/im.test(fs.readFileSync(config, "utf8")))
        throw new Error("Git config includes are prohibited in analysis repositories.");
    }
    for (const name of ["alternates", "http-alternates"])
      if (fs.existsSync(path.join(root, "objects", "info", name))) throw new Error("Git object alternates are prohibited.");
  }
}
