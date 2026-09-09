import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { readLicenseBundle } from "./license-bundle.mjs";
const [root, output] = process.argv.slice(2);
if (!root || !output) throw new Error("Usage: create-sbom.mjs <licenses-root> <output.json>");
const items = readLicenseBundle(root);
const components = items.map(item => {
  const name = item.ecosystem === "nuget" ? item.name.toLowerCase() : item.name;
  const purl = `pkg:${item.ecosystem}/${name.startsWith("@") ? "%40" + name.slice(1) : name}@${item.version}`;
  const properties = [{ name: "diagram-maker:declared-license", value: item.declared },
    { name: "diagram-maker:scope", value: item.scope ?? (item.dev ? "build" : "application") }];
  if (item.integrity) properties.push({ name: "diagram-maker:archive-integrity", value: item.integrity });
  if (item.installed === false) properties.push({ name: "diagram-maker:installed", value: "false" });
  if (item.evidence) properties.push({ name: "diagram-maker:license-evidence", value: JSON.stringify(item.evidence) });
  if (item.licenseSource) properties.push({ name: "diagram-maker:license-source", value: item.licenseSource });
  if (item.locked !== undefined) properties.push({ name: "diagram-maker:locked", value: String(item.locked) });
  return { type: "library", "bom-ref": purl, name: item.name, version: item.version, purl,
    licenses: [{ expression: item.selected }], properties,
    ...(item.archiveSha256 ? { hashes: [{ alg: "SHA-256", content: item.archiveSha256 }] } : {}) };
});
const nodeInfo = path.join(root, "node-runtime.json");
if (fs.existsSync(nodeInfo)) {
  const runtime = JSON.parse(fs.readFileSync(nodeInfo, "utf8").replace(/^\uFEFF/, ""));
  components.push({ type: "application", "bom-ref": "node-runtime", name: "Node.js", version: runtime.version,
    licenses: [{ license: { name: "MIT and bundled third-party terms; see NODE_LICENSE.txt" } }],
    hashes: [{ alg: "SHA-256", content: runtime.sha256 }], properties: [{ name: "diagram-maker:embedded-versions", value: JSON.stringify(runtime.versions) }] });
}
const bom = { bomFormat: "CycloneDX", specVersion: "1.5", serialNumber: `urn:uuid:${crypto.randomUUID()}`, version: 1,
  metadata: { component: { type: "application", "bom-ref": "diagram-maker", name: "Diagram Maker" },
    properties: [{ name: "diagram-maker:inventory-scope", value: "Application, build/test when requested, and runtime packs; container OS requires its separate image SBOM." }] }, components };
fs.writeFileSync(output, JSON.stringify(bom, null, 2));
console.log(`Created CycloneDX inventory with ${components.length} components.`);
