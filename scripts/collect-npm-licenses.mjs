import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const [outputArgument, ...rootArguments] = process.argv.slice(2);
if (!outputArgument || rootArguments.length === 0) {
  process.stderr.write("Usage: node scripts/collect-npm-licenses.mjs <output> <node_modules> [...]\n");
  process.exit(2);
}

const outputRoot = path.resolve(outputArgument);
fs.mkdirSync(outputRoot, { recursive: true });
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
  const key = `${manifest.name}@${manifest.version}`;
  if (!packages.has(key)) {
    const destination = path.join(outputRoot, key.replaceAll(/[^A-Za-z0-9._-]/g, "_"));
    fs.mkdirSync(destination, { recursive: true });
    for (const name of fs.readdirSync(packagePath)) {
      if (/^(licen[cs]e|copying|notice)(\..*)?$/i.test(name)) {
        const source = path.join(packagePath, name);
        if (fs.statSync(source).isFile()) fs.copyFileSync(source, path.join(destination, name));
      }
    }
    packages.set(key, typeof manifest.license === "string" ? manifest.license : manifest.license?.type ?? "UNKNOWN");
  }
  scanNodeModules(path.join(packagePath, "node_modules"));
}

for (const root of rootArguments) scanNodeModules(path.resolve(root));
const inventory = [...packages]
  .sort(([left], [right]) => left.localeCompare(right))
  .map(([name, license]) => `${name}\t${license}`)
  .join("\n");
fs.writeFileSync(path.join(outputRoot, "_inventory.tsv"), `${inventory}\n`, "utf8");
process.stdout.write(`Collected license metadata for ${packages.size} npm packages.\n`);
