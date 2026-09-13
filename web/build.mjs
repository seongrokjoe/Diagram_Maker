import { build } from "esbuild";
import { copyFileSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, rmdirSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, isAbsolute, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { assertApprovedBuildPath } from "./offline-policy.mjs";
import { inspectFont } from "../scripts/font-assets.mjs";

const projectDirectory = dirname(fileURLToPath(import.meta.url));
assertApprovedBuildPath(projectDirectory);
const font = inspectFont(resolve(projectDirectory, "src/assets/fonts"));
const repositoryDirectory = resolve(projectDirectory, "..");
const outputArgumentIndex = process.argv.indexOf("--outDir");
const requestedOutput = outputArgumentIndex >= 0 ? process.argv[outputArgumentIndex + 1] : "dist";
if (!requestedOutput) throw new Error("--outDir requires a directory.");
const outputDirectory = resolve(projectDirectory, requestedOutput);
const relativeOutput = relative(repositoryDirectory, outputDirectory);
if (relativeOutput.startsWith("..") || isAbsolute(relativeOutput))
  throw new Error("The build output must remain inside the repository.");
if (outputDirectory !== resolve(projectDirectory, "dist") && !relativeOutput.startsWith("artifacts" + (process.platform === "win32" ? "\\" : "/")))
  throw new Error("The build output must be web/dist or a child of artifacts.");
assertApprovedBuildPath(outputDirectory);
const assetsDirectory = resolve(outputDirectory, "assets");
const vendorDirectory = resolve(outputDirectory, "vendor");

function removeTree(directory) {
  if (!existsSync(directory)) return;
  for (const entry of readdirSync(directory)) {
    const entryPath = resolve(directory, entry);
    if (lstatSync(entryPath).isDirectory()) removeTree(entryPath);
    else unlinkSync(entryPath);
  }
  rmdirSync(directory);
}

removeTree(outputDirectory);
mkdirSync(assetsDirectory, { recursive: true });
mkdirSync(vendorDirectory, { recursive: true });
await build({
  entryPoints: [resolve(projectDirectory, "src/main.tsx")],
  bundle: true,
  format: "esm",
  jsx: "automatic",
  jsxImportSource: "react",
  minify: true,
  sourcemap: false,
  target: "es2020",
  define: { "process.env.NODE_ENV": '"production"' },
  outfile: resolve(assetsDirectory, "app.js"),
  loader: { ".woff2": "file" },
  assetNames: "fonts/[name]-[hash]",
  legalComments: "none",
  logLevel: "info",
});

copyFileSync(
  resolve(projectDirectory, "node_modules/mermaid/dist/mermaid.min.js"),
  resolve(vendorDirectory, "mermaid.min.js"),
);
copyFileSync(resolve(projectDirectory, "src/assets/fonts", font.licenseFile), resolve(assetsDirectory, "fonts/LICENSE.txt"));
copyFileSync(resolve(projectDirectory, "src/assets/fonts/manifest.json"), resolve(assetsDirectory, "fonts/manifest.json"));
const html = readFileSync(resolve(projectDirectory, "index.html"), "utf8")
  .replace("/src/main.tsx", "/assets/app.js")
  .replace("</head>", '    <link rel="stylesheet" href="/assets/app.css" />\n  </head>');
writeFileSync(resolve(outputDirectory, "index.html"), html, "utf8");
