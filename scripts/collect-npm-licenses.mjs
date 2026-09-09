import fs from "node:fs";
import path from "node:path";
import { inspectNpmRoots } from "./license-policy.mjs";
try {
  const [output, ...roots] = process.argv.slice(2);
  if (!output || !roots.length) throw new Error("Usage: collect-npm-licenses.mjs <output> <node_modules> [...]");
  const packages = inspectNpmRoots(roots);
  fs.mkdirSync(output, { recursive: true });
  for (const item of packages) {
    const destination = path.join(output, (item.name + "@" + item.version).replaceAll(/[^A-Za-z0-9._-]/g, "_"));
    fs.mkdirSync(destination, { recursive: true });
    for (const file of item.texts) fs.copyFileSync(file, path.join(destination, path.basename(file)));
    if (item.installed) fs.copyFileSync(path.join(item.directory, "package.json"), path.join(destination, "package.json"));
    if (item.supplemental) fs.copyFileSync(item.supplemental, path.join(destination, "LICENSE-EXPRESSION.txt"));
  }
  fs.writeFileSync(path.join(output, "_inventory.json"), JSON.stringify(packages.map(({ directory, texts, supplemental, ...item }) => item), null, 2));
  fs.writeFileSync(path.join(output, "_inventory.tsv"), packages.map(p => [p.name + "@" + p.version, p.declared, p.selected, p.installed ? "installed" : "optional-not-installed"].join("\t")).join("\n") + "\n");
  console.log("Collected " + packages.length + " reviewed npm license records.");
} catch (error) { console.error(error.message); process.exitCode = 1; }
