import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const roots = process.argv.slice(2);
if (roots.length === 0) {
  process.stderr.write("Usage: node scripts/check-npm-licenses.mjs <node_modules> [...]\n");
  process.exit(2);
}

const allowedTokens = new Set([
  "MIT",
  "Apache-2.0",
  "BSD-2-Clause",
  "BSD-3-Clause",
  "0BSD",
  "ISC",
  "Zlib",
  "Unlicense",
  "CC-BY-4.0",
]);
const reviewedOverrides = new Map([
  ["khroma@2.1.0", "MIT"],
]);
const failures = [];
const packages = new Map();

function scanNodeModules(nodeModulesPath) {
  if (!fs.existsSync(nodeModulesPath)) return;
  for (const entry of fs.readdirSync(nodeModulesPath, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === ".bin") continue;
    const fullPath = path.join(nodeModulesPath, entry.name);
    if (entry.name.startsWith("@")) {
      for (const scoped of fs.readdirSync(fullPath, { withFileTypes: true })) {
        if (scoped.isDirectory()) inspectPackage(path.join(fullPath, scoped.name));
      }
    } else {
      inspectPackage(fullPath);
    }
  }
}

function inspectPackage(packagePath) {
  const manifestPath = path.join(packagePath, "package.json");
  if (!fs.existsSync(manifestPath)) return;
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  const declaredLicense = typeof manifest.license === "string" ? manifest.license : manifest.license?.type;
  const key = `${manifest.name}@${manifest.version}`;
  const license = declaredLicense ?? reviewedOverrides.get(key);
  packages.set(key, license ?? "UNKNOWN");
  if (!license || !isAllowedExpression(license)) failures.push(`${key}: ${license ?? "UNKNOWN"}`);
  scanNodeModules(path.join(packagePath, "node_modules"));
}

function isAllowedExpression(expression) {
  const normalized = expression.replaceAll("(", "").replaceAll(")", "").trim();
  return normalized.split(/\s+OR\s+|\s*\/\s*/i).some((alternative) =>
    alternative.split(/\s+AND\s+/i).map((token) => token.trim()).filter(Boolean)
      .every((token) => allowedTokens.has(token)));
}

for (const root of roots) scanNodeModules(path.resolve(root));
for (const [name, license] of [...packages].sort(([left], [right]) => left.localeCompare(right))) {
  process.stdout.write(`${name}\t${license}\n`);
}
if (failures.length > 0) {
  process.stderr.write(`\nDisallowed or unknown licenses:\n${failures.join("\n")}\n`);
  process.exit(1);
}
