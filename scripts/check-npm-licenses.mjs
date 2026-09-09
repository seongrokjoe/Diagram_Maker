import { inspectNpmRoots } from "./license-policy.mjs";
try {
  const roots = process.argv.slice(2);
  if (!roots.length) throw new Error("Usage: check-npm-licenses.mjs <node_modules> [...]");
  const packages = inspectNpmRoots(roots);
  for (const item of packages) console.log(item.name + "@" + item.version + "\t" + item.selected);
  console.log("Reviewed " + packages.length + " locked packages, including optional platform packages.");
} catch (error) { console.error(error.message); process.exitCode = 1; }
