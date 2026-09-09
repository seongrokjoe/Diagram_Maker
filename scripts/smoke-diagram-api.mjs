// Isolated, synthetic, loopback-only API regression check. Leaves its fixture in
// artifacts for inspection; never modifies registered repositories or local data.
import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import { mkdir, mkdtemp, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { setTimeout as delay } from "node:timers/promises";
import { smokeCodeBlocks } from "./code-block-smoke.mjs";
import { assertLocalPath, gitEnvironment } from "../tools/git-worker/local-security.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
assertLocalPath(root);
await mkdir(path.join(root, "artifacts"), { recursive: true });
const fixture = await mkdtemp(path.join(root, "artifacts", "api-smoke-"));
const git = (...args) => execFileSync("git", args, { cwd: fixture, encoding: "utf8", windowsHide: true, env: gitEnvironment() }).trim();
const llmPolicy = path.join(fixture, "disabled-llm.json");
const networkPolicy = path.join(fixture, "test-network-policy.json");
await writeFile(llmPolicy, JSON.stringify({ Llm: { Enabled: false, AllowDevelopmentStub: false } }));
await writeFile(networkPolicy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
git("init", "-b", "main");
git("config", "user.name", "Synthetic Test");
git("config", "user.email", "synthetic@example.invalid");
const code = body => `namespace Smoke;
class Store { public int Count; public void Save(int value) { Count = value; } }
class Service {
  private Store store = new Store();
  public void Run(int n) {
    ${body}
  }
}
`;
await writeFile(path.join(fixture, "Service.cs"), code("if (n > 0) store.Save(n);"));
git("add", "Service.cs"); git("commit", "-m", "Synthetic baseline");
const baseSha = git("rev-parse", "HEAD");
await writeFile(path.join(fixture, "Service.cs"), code("switch(n) { case 1: store.Save(1); break; default: store.Save(2); break; }"));
git("add", "Service.cs"); git("commit", "-m", "Synthetic switch change");
const targetSha = git("rev-parse", "HEAD");
let output = "";
// The API smoke does not serve or test a UI. Supply the build's empty static
// directory on a clean checkout where web/dist has not been generated.
await mkdir(path.join(root, "src/DiagramMaker.Api/bin/Release/net9.0/wwwroot"), { recursive: true });
const server = spawn("dotnet", [path.join(root, "src/DiagramMaker.Api/bin/Release/net9.0/DiagramMaker.Api.dll"), "--urls", "http://127.0.0.1:0"], {
  cwd: path.join(root, "src/DiagramMaker.Api"), windowsHide: true,
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development", DOTNET_ENVIRONMENT: "Development",
    Storage__Provider: "InMemory", Llm__Enabled: "false", Security__TrustReverseProxyHeaders: "false",
    DIAGRAMMAKER_LLM_POLICY_PATH: llmPolicy, DIAGRAMMAKER_NETWORK_POLICY_PATH: process.argv.includes('--basic') ? '' : networkPolicy,
    GitWorker__ScriptPath: path.join(root, "tools/git-worker/index.mjs") },
  stdio: ["ignore", "pipe", "pipe"],
});
server.stdout.on("data", data => { output += data; });
server.stderr.on("data", data => { output += data; });
let origin;
try {
  for (let i = 0; i < 100; i++) {
    origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
    if (origin) break;
    if (server.exitCode !== null) throw new Error(`API stopped before startup: ${output}`);
    await delay(100);
  }
  assert.ok(origin, "API must start on loopback");
  async function request(url, method = "GET", body, status = 200) {
    const response = await fetch(`${origin}/api/v1${url}`, { method, headers: { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30_000) });
    const value = await response.json();
    assert.equal(response.status, status, `${method} ${url}: ${JSON.stringify(value)}`);
    return value;
  }
  async function poll(url, terminal) {
    for (let i = 0; i < 120; i++) {
      const value = await request(url);
      if (terminal.includes(value.state)) return value;
      assert.notEqual(value.state, "Failed", JSON.stringify(value));
      await delay(250);
    }
    throw new Error(`Timed out: ${url}`);
  }
  const repository = await request("/repositories", "POST", { name: "API smoke", localPath: fixture, defaultBranch: "main", allowedRoles: ["Reviewer"] }, 201);
  await smokeCodeBlocks(request, poll);
  const queued = await request("/analysis-plans", "POST", { repositoryId: repository.id, baseRevision: baseSha, targetRevision: targetSha, useLlmGrouping: false }, 202);
  let plan = await poll(`/analysis-plans/${queued.id}`, ["Ready"]);
  const change = plan.candidates.find(item => item.qualifiedName === "Smoke.Service.Run");
  assert.ok(change, "changed method must be selectable");
  const views = [
    { id: "flow", diagramType: "flowchart", presetId: "flow-vertical-overview" },
    { id: "sequence", diagramType: "sequence", presetId: "sequence-caller-context" },
    { id: "class", diagramType: "class", presetId: "class-related" },
    { id: "map", diagramType: "code-relation", presetId: "code-class-grouped" },
  ];
  const group = { id: "group", title: "Synthetic change", changeIds: [change.id], diagramType: views[0].diagramType, presetId: views[0].presetId, views };
  plan = await request(`/analysis-plans/${plan.id}/selection`, "PUT", { expectedRevision: plan.revision, groups: [group] });
  const analysis = await request(`/analysis-plans/${plan.id}/generate`, "POST", { expectedRevision: plan.revision }, 202);
  const result = await poll(`/analyses/${analysis.id}?includeGraph=false&summary=true`, ["Completed", "Partial"]);
  const resultViews = result.result.diagramGroups[0].views;
  assert.equal(resultViews.length, 4);
  for (const view of resultViews) {
    assert.ok(view.diagram, `${view.viewId} must have an artifact: ${JSON.stringify(view.warnings)}`);
    assert.equal(view.diagram.ir.nodes.length, 0, "summary responses must omit node payloads");
    assert.ok(!view.diagram.explanation, "summary responses must omit explanation payloads");
    const page = view.document.pages.find(page => page.id !== "overview") ?? view.document.pages[0];
    const basePath = `/analyses/${analysis.id}/groups/group/views/${view.viewId}`;
    const artifact = await request(`${basePath}/pages/${page.id}`);
    assert.ok(artifact.ir.nodes.length > 0);
    assert.equal(artifact.explanation.status, "Disabled");
    assert.ok(artifact.explanation.warnings.some(warning => warning.includes("LLM")));
    assert.equal(artifact.explanation.changes.length, 0, "disabled generation must not invent change meaning");
    const removed = artifact.ir.nodes[0].id;
    const remaining = artifact.ir.nodes.filter(node => node.id !== removed);
    if (remaining.length === 0) remaining.push({ id: "manual", label: "Manual remaining node" });
    const document = { title: artifact.ir.title, direction: artifact.ir.direction, nodes: remaining,
      edges: artifact.ir.edges.filter(edge => edge.sourceId !== removed && edge.targetId !== removed) };
    const input = { rootArtifactId: artifact.id, expectedVersion: artifact.version, document };
    const preview = await request(`${basePath}/edit-preview?pageId=${page.id}`, "POST", input);
    assert.ok(preview.ir.nodes.every(node => node.id !== removed));
    assert.ok(preview.ir.edges.every(edge => edge.sourceId !== removed && edge.targetId !== removed));
    const revision = await request(`${basePath}/edits?pageId=${page.id}`, "POST", input, 201);
    assert.equal(revision.version, artifact.version + 1);
    assert.equal(revision.diagram.explanation.basis, "GeneratedSourceBeforeManualEdit");
    assert.equal(revision.diagram.explanation.summary, artifact.explanation.summary);
    await request(`${basePath}/edits?pageId=${page.id}`, "POST", input, 409);
    const evidenceId = artifact.ir.nodes.flatMap(node => node.evidenceIds)[0];
    if (evidenceId) {
      const snippet = await request(`/analyses/${analysis.id}/evidence/${evidenceId}/snippet`);
      assert.ok([baseSha, targetSha].includes(snippet.revisionSha));
      assert.ok(snippet.content.length > 0);
    }
  }
  const unauthorized = await fetch(`${origin}/api/v1/analyses/${analysis.id}`, { headers: { "X-Remote-User": "spoof", "X-Remote-Roles": "Admin" } });
  assert.equal(unauthorized.status, 401, "untrusted identity headers must remain blocked");
  const summaries = await request("/analysis-plans?summary=true");
  assert.equal(summaries[0].candidates.length, 0);
  console.log(`API smoke passed: 4 diagram types, lazy pages, node/incident-edge deletion, revisions/conflicts, evidence blobs, identity guard. Fixture: ${path.relative(root, fixture)}`);
} finally {
  server.kill();
}
