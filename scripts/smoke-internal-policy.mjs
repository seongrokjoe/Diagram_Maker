// Synthetic startup rejection checks. Every candidate is rejected before a
// listener or outbound transport starts; no corporate configuration is loaded.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { assertLocalPath } from "../tools/git-worker/local-security.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
assertLocalPath(root);
await mkdir(path.join(root, "artifacts"), { recursive: true });
const fixture = await mkdtemp(path.join(root, "artifacts/internal-policy-"));
const policy = path.join(fixture, "network.json");
const llm = path.join(fixture, "disabled-llm.json");
await writeFile(policy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
await writeFile(llm, JSON.stringify({ Llm: { Enabled: false, AllowDevelopmentStub: false } }));
const cases = [
  { name: "missing policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: path.join(fixture, "missing.json") }, match: /FileNotFoundException/ },
  { name: "cloud policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: path.join(fixture, "OneDrive/policy.json") }, match: /Cloud synchronized/ },
  { name: "retired provider", env: { CodexTest__Enabled: "true" }, match: /External inference test mode has been removed/ },
  { name: "unapproved endpoint", env: { Llm__Enabled: "true", Llm__Endpoint: "https://unapproved.invalid/v1/chat/completions", Llm__AllowedOrigin: "https://unapproved.invalid" }, match: /not approved by the network policy/ },
  { name: "public local binding", args: ["--urls", "http://0.0.0.0:0"], match: /explicit loopback IP bindings/ },
];
for (const item of cases) {
  const child = spawn("dotnet", [path.join(root, "src/DiagramMaker.Api/bin/Release/net9.0/DiagramMaker.Api.dll"),
    ...(item.args ?? ["--urls", "http://127.0.0.1:0"])], {
    cwd: path.join(root, "src/DiagramMaker.Api"), windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", DOTNET_ENVIRONMENT: "Development",
      DIAGRAMMAKER_NETWORK_POLICY_PATH: policy, DIAGRAMMAKER_LLM_POLICY_PATH: llm,
      Storage__Provider: "InMemory", Llm__Enabled: "false", CodexTest__Enabled: "false", ...item.env },
  });
  let output = "";
  child.stdout.on("data", data => output += data);
  child.stderr.on("data", data => output += data);
  const timeout = setTimeout(() => child.kill(), 15000);
  try {
    const code = await new Promise((resolve, reject) => { child.once("error", reject); child.once("close", resolve); });
    assert.notEqual(code, 0, item.name);
    assert.doesNotMatch(output, /Now listening on:/, item.name);
    assert.match(output, item.match, item.name);
  } finally { clearTimeout(timeout); }
}
await writeFile(path.join(fixture, "result.json"), JSON.stringify({ status: "passed", checks: cases.map(item => item.name) }, null, 2));
console.log(`Internal startup policy: ${cases.length} rejection checks passed. Fixture: ${path.relative(root, fixture)}`);
