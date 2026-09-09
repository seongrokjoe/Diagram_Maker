// Synthetic startup checks for basic mode and explicit allowlists.
// No corporate configuration or external endpoint is used.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { assertLocalPath } from "../tools/git-worker/local-security.mjs";
import { setTimeout as delay } from "node:timers/promises";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
assertLocalPath(root);
await mkdir(path.join(root, "artifacts"), { recursive: true });
const fixture = await mkdtemp(path.join(root, "artifacts/internal-policy-"));
const policy = path.join(fixture, "network.json");
const llm = path.join(fixture, "disabled-llm.json");
const malformed = path.join(fixture, "malformed.json");
const empty = path.join(fixture, "empty.json");
const localAppData = path.join(fixture, "local-app-data");
await mkdir(path.join(localAppData, "DiagramMaker"), { recursive: true });
await writeFile(path.join(localAppData, "DiagramMaker/network-policy.json"), "not-json");
await writeFile(malformed, "not-json");
await writeFile(empty, "{}");
await writeFile(policy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
await writeFile(llm, JSON.stringify({ Llm: { Enabled: false, AllowDevelopmentStub: false } }));
const cases = [
  { name: "basic startup ignores leftover default policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: "" }, starts: true },
  { name: "explicit approved startup", starts: true },
  { name: "missing policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: path.join(fixture, "missing.json") }, match: /FileNotFoundException/ },
  { name: "malformed policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: malformed }, match: /JsonException/ },
  { name: "empty policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: empty }, match: /outside the approved local roots/ },
  { name: "cloud policy", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: path.join(fixture, "OneDrive/policy.json") }, match: /Cloud synchronized/ },
  { name: "retired provider", env: { CodexTest__Enabled: "true" }, match: /External inference test mode has been removed/ },
  { name: "unapproved endpoint", env: { Llm__Enabled: "true", Llm__Endpoint: "https://unapproved.invalid/v1/chat/completions", Llm__AllowedOrigin: "https://unapproved.invalid" }, match: /not approved by the network policy/ },
  { name: "public local binding", args: ["--urls", "http://0.0.0.0:0"], match: /explicit loopback IP bindings/ },
  { name: "basic origin mismatch", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: "", Llm__Enabled: "true",
    Llm__Endpoint: "http://127.0.0.1:9999/v1/chat/completions", Llm__AllowedOrigin: "http://127.0.0.1:9998" }, match: /outside Llm:AllowedOrigin/ },
  { name: "basic cloud data", env: { DIAGRAMMAKER_NETWORK_POLICY_PATH: "", Storage__Provider: "LocalFile",
    Storage__LocalFilePath: path.join(fixture, "OneDrive/data.json") }, match: /Cloud synchronized/ },
];
for (const item of cases) {
  const child = spawn("dotnet", [path.join(root, "src/DiagramMaker.Api/bin/Release/net9.0/DiagramMaker.Api.dll"),
    ...(item.args ?? ["--urls", "http://127.0.0.1:0"])], {
    cwd: path.join(root, "src/DiagramMaker.Api"), windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", DOTNET_ENVIRONMENT: "Development",
      DIAGRAMMAKER_NETWORK_POLICY_PATH: policy, DIAGRAMMAKER_LLM_POLICY_PATH: llm, LOCALAPPDATA: localAppData,
      Storage__Provider: "InMemory", Llm__Enabled: "false", CodexTest__Enabled: "false", ...item.env },
  });
  let output = "";
  child.stdout.on("data", data => output += data);
  child.stderr.on("data", data => output += data);
  const closed = new Promise((resolve, reject) => { child.once("error", reject); child.once("close", resolve); });
  const timeout = setTimeout(() => child.kill(), 15000);
  try {
    if (item.starts) {
      let api;
      for (let attempt = 0; attempt < 120; attempt++) {
        api = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
        if (api) break;
        assert.equal(child.exitCode, null, output);
        await delay(100);
      }
      assert.ok(api, `${item.name}: ${output}`);
      const response = await fetch(`${api}/health`, { signal: AbortSignal.timeout(5000) });
      assert.equal(response.status, 200, item.name);
    } else {
      const code = await closed;
      assert.notEqual(code, 0, item.name);
      assert.doesNotMatch(output, /Now listening on:/, item.name);
      assert.match(output, item.match, `${item.name}: ${output}`);
    }
  } finally {
    clearTimeout(timeout);
    if (child.exitCode === null) { child.kill(); await closed; }
  }
}
await writeFile(path.join(fixture, "result.json"), JSON.stringify({ status: "passed", checks: cases.map(item => item.name) }, null, 2));
console.log(`Internal startup policy: ${cases.length} startup checks passed. Fixture: ${path.relative(root, fixture)}`);
