import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

const allowed = new Set(["MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "0BSD", "ISC", "Zlib", "PostgreSQL", "Unlicense"]);

// Preserve SPDX AND/OR precedence and record the permitted alternative.
export function selectLicense(expression, packageId = "") {
  if (typeof expression !== "string" || !expression.trim()) throw new Error(`${packageId}: missing license`);
  const tokens = expression.match(/\(|\)|[A-Za-z0-9.+-]+/g) ?? [];
  if (tokens.join("") !== expression.replace(/\s/g, "")) throw new Error(`${packageId}: invalid SPDX expression`);
  let index = 0;
  function atom() {
    const token = tokens[index++];
    if (token === "(") { const value = or(); if (tokens[index++] !== ")") throw new Error("Unbalanced SPDX expression"); return value; }
    if (!token || [")", "AND", "OR", "WITH"].includes(token)) throw new Error("Invalid SPDX operand");
    return allowed.has(token) || (token === "CC-BY-4.0" && packageId === "caniuse-lite@1.0.30001810") ? token : null;
  }
  function and() { let value = atom(); while (tokens[index] === "AND") { index++; const right = atom(); value = value && right ? `(${value} AND ${right})` : null; } return value; }
  function or() { let value = and(); while (tokens[index] === "OR") { index++; const right = and(); value = value || right; } return value; }
  const selected = or();
  if (index !== tokens.length) throw new Error(`${packageId}: unsupported or malformed SPDX expression`);
  if (!selected) throw new Error(`${packageId}: disallowed license ${expression}`);
  return selected;
}

export function sha256(file) { return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex"); }

export function inspectNpmRoots(roots) {
  const packages = new Map();
  for (const root of roots) {
    if (!fs.existsSync(root) || !fs.statSync(root).isDirectory()) throw new Error(`Missing dependency directory: ${root}`);
    const lockPath = path.join(path.dirname(root), "package-lock.json");
    if (!fs.existsSync(lockPath)) throw new Error(`Missing lock file: ${lockPath}`);
    const lock = JSON.parse(fs.readFileSync(lockPath, "utf8"));
    if (!lock.packages || ![2, 3].includes(lock.lockfileVersion)) throw new Error(`Unsupported lock file: ${lockPath}`);
    let installed = 0;
    for (const [relative, item] of Object.entries(lock.packages ?? {})) {
      if (!relative) continue;
      const directory = path.resolve(path.dirname(root), relative);
      if (!directory.startsWith(path.resolve(root) + path.sep)) throw new Error("Dependency path escapes node_modules");
      const manifestFile = path.join(directory, "package.json");
      if (fs.existsSync(directory) && fs.lstatSync(directory).isSymbolicLink()) throw new Error("Linked dependencies are not accepted");
      if (fs.existsSync(manifestFile) && fs.lstatSync(manifestFile).isSymbolicLink()) throw new Error("Linked dependency manifests are not accepted");
      const manifest = fs.existsSync(manifestFile) ? JSON.parse(fs.readFileSync(manifestFile, "utf8")) : null;
      const name = manifest?.name ?? item.name ?? relative.split("node_modules/").at(-1);
      const id = `${name}@${item.version}`;
      if (manifest && manifest.version !== item.version) throw new Error(`${id}: installed version differs from lock`);
      if (manifest) installed++;
      if (!manifest && !item.optional) throw new Error(`${id}: required dependency not installed`);
      const texts = manifest ? fs.readdirSync(directory).filter(file => /^(licen[cs]e|copying|notice|third.?party)([._-].*)?$/i.test(file) && fs.lstatSync(path.join(directory, file)).isFile()).map(file => path.join(directory, file)) : [];
      // Some packages ship their complete terms in a README or a source header.
      if (manifest && texts.length === 0) for (const file of fs.readdirSync(directory).filter(file => /^(readme.*|.*\.(?:js|cjs|mjs))$/i.test(file))) {
        const full = path.join(directory, file);
        if (fs.lstatSync(full).isFile() && /copyright|permission is hereby granted|licensed under/i.test(fs.readFileSync(full, "utf8"))) texts.push(full);
      }
      let declared = typeof manifest?.license === "string" ? manifest.license : manifest?.license?.type ?? item.license;
      if (id === "khroma@2.1.0" && !declared) {
        const evidence = path.join(repositoryRoot, "packaging/licenses/khroma-2.1.0-MIT.txt");
        const actual = texts.find(file => path.basename(file).toLowerCase() === "license");
        if (!actual || !fs.existsSync(evidence) || sha256(actual) !== sha256(evidence)) throw new Error(`${id}: reviewed MIT evidence differs`);
        declared = "MIT";
      }
      const selected = selectLicense(declared, id);
      if (item.license) selectLicense(item.license, id);
      const existing = packages.get(id);
      if (existing && (existing.selected !== selected || existing.integrity !== item.integrity)) throw new Error(`${id}: inconsistent duplicate dependency`);
      if (existing?.installed && !manifest) continue;
      const supplemental = manifest && texts.length === 0 ? path.join(repositoryRoot, `packaging/licenses/standard/${selected}.txt`) : null;
      if (supplemental && !fs.existsSync(supplemental)) throw new Error(`${id}: no shipped terms or local standard text`);
      packages.set(id, { ecosystem: "npm", name, version: item.version, declared, selected, dev: existing ? existing.dev && !!item.dev : !!item.dev,
        installed: !!manifest, integrity: item.integrity, source: item.resolved, directory, texts, supplemental,
        licenseSource: supplemental ? "manifest declaration with supplemental standard terms" : "shipped package evidence",
        evidence: [...texts, ...(manifest ? [manifestFile] : []), ...(supplemental ? [supplemental] : [])].map(file => ({ file: file === supplemental ? "LICENSE-EXPRESSION.txt" : path.basename(file), sha256: sha256(file) })) });
    }
    if (!installed) throw new Error(`No installed packages in ${root}`);
    function walk(directory) {
      for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        if (entry.name === ".bin" || entry.name.startsWith(".")) continue;
        if (entry.isSymbolicLink()) throw new Error("Linked dependencies are not accepted");
        if (!entry.isDirectory()) continue;
        const full = path.join(directory, entry.name);
        if (entry.name.startsWith("@")) { walk(full); continue; }
        if (fs.existsSync(path.join(full, "package.json"))) {
          const relative = path.relative(path.dirname(root), full).split(path.sep).join("/");
          if (!lock.packages[relative]) throw new Error(`Unlocked installed package: ${relative}`);
        }
        if (fs.existsSync(path.join(full, "node_modules"))) walk(path.join(full, "node_modules"));
      }
    }
    walk(root);
  }
  return [...packages.values()].sort((a, b) => `${a.name}@${a.version}`.localeCompare(`${b.name}@${b.version}`));
}
