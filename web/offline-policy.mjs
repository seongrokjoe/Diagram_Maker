import fs from "node:fs";
import path from "node:path";

function within(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === "" || (relative !== ".." && !relative.startsWith(".." + path.sep) && !path.isAbsolute(relative));
}

export function assertLocalBuildPath(candidate) {
  if (!path.isAbsolute(candidate) || /^[/\\]{2}/.test(candidate)) throw new Error("Absolute non-network build path required.");
  const full = path.resolve(candidate);
  if (full.split(/[/\\]/).some(part => /^OneDrive(?:$| - )/i.test(part))) throw new Error("Cloud synchronized build paths are prohibited.");
  for (const key of ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"]) {
    if (process.env[key] && within(path.resolve(process.env[key]), full)) throw new Error("Cloud synchronized build paths are prohibited.");
  }
  for (let current = full; ; current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error("Linked build paths are prohibited.");
    if (current === path.dirname(current)) break;
  }
}

export function assertApprovedBuildPath(candidate) {
  assertLocalBuildPath(candidate);
  const policyFile = process.env.DIAGRAMMAKER_NETWORK_POLICY_PATH
    ?? path.join(process.env.LOCALAPPDATA ?? "", "DiagramMaker/network-policy.json");
  assertLocalBuildPath(policyFile);
  const policy = JSON.parse(fs.readFileSync(policyFile, "utf8").replace(/^\uFEFF/, ""));
  if (!Array.isArray(policy.LocalRoots) || !policy.LocalRoots.some(root => {
    assertLocalBuildPath(root);
    return within(path.resolve(root), path.resolve(candidate));
  })) throw new Error("Build path is outside the approved local roots.");
}
