import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { readLicenseBundle } from "./license-bundle.mjs";
import { sha256 } from "./license-policy.mjs";
const forbidden = /CodexCliCompletionTransport|CodexProcessRunner|SampleTestWorkspace|model_provider\s*=\s*[\\"]*openai/;
export function scanInternalOnly(root, { packaged = false } = {}) {
  const failures = [];
  function scan(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isSymbolicLink()) { failures.push(`Linked payload: ${file}`); continue; }
    if (packaged && /^(?:data|repositories|\.git|\.codex|\.env(?:\..*)?|auth\.json|codex-test|(?:llm|network)-policy\.json)$/i.test(entry.name)) failures.push(`Forbidden package payload: ${file}`);
    if (["node_modules", "licenses", "obj", "bin", ".git", "artifacts"].includes(entry.name)) continue;
    if (entry.isDirectory()) scan(file);
    else if (/\.(cs|tsx?|mjs|ps1|cmd|dll|js)$/.test(file) && !/check-internal-only(?:\.test)?\.mjs$/.test(file)) {
      const bytes = fs.readFileSync(file);
      if (forbidden.test(bytes.toString("utf8")) || forbidden.test(bytes.toString("utf16le"))) failures.push(file);
    }
  }
  }
  scan(root);
  return failures;
}
export function checkPackage(packageRoot) {
  const failures = scanInternalOnly(packageRoot, { packaged: true });
  for (const required of ["DiagramMaker.Api.dll", "tools/git-worker/local-security.mjs", "start.cmd", "start-with-network-policy.cmd", "config/network-policy.example.json", "licenses/sbom.cdx.json"])
    if (!fs.existsSync(path.join(packageRoot, required))) failures.push(`Missing ${required}`);
  try {
    const items = readLicenseBundle(path.join(packageRoot, "licenses"));
    const sbom = JSON.parse(fs.readFileSync(path.join(packageRoot, "licenses/sbom.cdx.json"), "utf8"));
    if (sbom.bomFormat !== "CycloneDX" || sbom.components?.length !== items.length + 1) failures.push("Package SBOM inventory mismatch");
    const font = items.find(item => item.ecosystem === "assets" && item.name === "pretendard-variable");
    const fontRoot = path.join(packageRoot, "wwwroot/assets/fonts");
    const fonts = fs.existsSync(fontRoot) ? fs.readdirSync(fontRoot).filter(file => /^PretendardVariable-.*\.woff2$/.test(file)) : [];
    if (!font || fonts.length !== 1 || sha256(path.join(fontRoot, fonts[0])) !== font.archiveSha256) failures.push("Packaged font differs from reviewed asset");
  } catch (error) { failures.push(`Invalid license bundle: ${error.message}`); }
  return failures;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const packageRoot = process.argv[2];
  const failures = packageRoot ? checkPackage(packageRoot) : ["src", "web/src", "scripts", "tools/git-worker"].flatMap(root => scanInternalOnly(root));
  if (!packageRoot) for (const file of ["scripts/verify.ps1", "scripts/verify.cmd", "scripts/build-offline-win-x64.ps1", "scripts/start-local.ps1"]) {
    const source = fs.readFileSync(file, "utf8");
    // The launcher health probe is a literal loopback request, not a download.
    const withoutHealthProbe = source.replace(/^.*Invoke-WebRequest -Uri "http:\/\/127\.0\.0\.1:\$Port".*$/gm, "");
    if (/Invoke-WebRequest|npm(?:\.cmd)?\s+audit|https:\/\/nodejs\.org/.test(withoutHealthProbe)) failures.push(`Public build source in ${file}`);
  }
  if (failures.length) throw new Error(failures.join("\n"));
  console.log("Internal-only source/package guard passed.");
}
