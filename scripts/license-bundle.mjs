import fs from "node:fs";
import path from "node:path";
import { sha256 } from "./license-policy.mjs";

export function readLicenseBundle(root) {
  const items = ["npm", "nuget"].flatMap(kind => {
    const records = JSON.parse(fs.readFileSync(path.join(root, kind, "_inventory.json"), "utf8").replace(/^\uFEFF/, ""));
    if (!Array.isArray(records) || !records.length) throw new Error(`Empty ${kind} license inventory`);
    for (const item of records) {
      const folder = kind === "npm" ? (item.name + "@" + item.version).replaceAll(/[^A-Za-z0-9._-]/g, "_")
        : (item.name + "-" + item.version).toLowerCase();
      if (item.ecosystem !== kind || !item.selected || folder.includes("/") || folder.includes("\\") || folder.includes("..")) throw new Error("Invalid license record");
      if (item.installed !== false && !item.evidence?.length) throw new Error(`Missing license evidence: ${item.name}`);
      for (const evidence of item.evidence ?? []) {
        if (!evidence.file || path.basename(evidence.file) !== evidence.file) throw new Error("Invalid evidence path");
        const file = path.join(root, kind, folder, evidence.file);
        if (!fs.existsSync(file) || sha256(file) !== evidence.sha256) throw new Error(`License evidence differs: ${item.name}/${evidence.file}`);
      }
    }
    return records;
  });
  return items;
}
