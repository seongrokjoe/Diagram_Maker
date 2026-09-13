import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { sha256 } from "./license-policy.mjs";

const fontRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../web/src/assets/fonts");

export function inspectFont(directory = fontRoot) {
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, "manifest.json"), "utf8"));
  if (manifest.name !== "Pretendard Variable" || manifest.version !== "1.3.9" || manifest.license !== "OFL-1.1"
    || manifest.file !== "PretendardVariable.woff2" || manifest.licenseFile !== "LICENSE.txt")
    throw new Error("Unreviewed font asset: expected Pretendard Variable 1.3.9 / OFL-1.1");
  for (const [file, expected] of [[manifest.file, manifest.sha256], [manifest.licenseFile, manifest.licenseSha256]]) {
    const full = path.join(directory, file);
    if (fs.lstatSync(full).isSymbolicLink() || !/^[0-9a-f]{64}$/.test(expected) || sha256(full) !== expected)
      throw new Error(`Font asset hash differs: ${file}`);
  }
  if (fs.readFileSync(path.join(directory, manifest.file)).subarray(0, 4).toString() !== "wOF2")
    throw new Error("Invalid WOFF2 font");
  return manifest;
}

export function collectFontLicenses(root) {
  const font = inspectFont();
  const folder = path.join(root, "assets", "pretendard-variable-1.3.9");
  fs.mkdirSync(folder, { recursive: true });
  for (const file of [font.licenseFile, "manifest.json"])
    fs.copyFileSync(path.join(fontRoot, file), path.join(folder, file));
  const inventory = [{ ecosystem: "assets", name: "pretendard-variable", version: font.version, declared: font.license,
    selected: font.license, scope: "application-font", installed: true, locked: true, archiveSha256: font.sha256,
    source: font.source, licenseSource: font.licenseSource,
    evidence: [font.licenseFile, "manifest.json"].map(file => ({ file, sha256: sha256(path.join(fontRoot, file)) })) }];
  fs.writeFileSync(path.join(root, "assets/_inventory.json"), JSON.stringify(inventory, null, 2));
  return inventory;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv[2]) collectFontLicenses(path.resolve(process.argv[2]));
  else inspectFont();
  console.log("Pinned font asset and upstream license verified.");
}
