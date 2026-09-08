// Offline end-to-end check. The child CLI is the local FakeVllm test executable,
// NOT Codex; no credentials or external model calls are involved.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
await mkdir(path.join(project, 'artifacts'), { recursive: true });
const run = await mkdtemp(path.join(project, 'artifacts/codex-smoke-'));
const runtime = path.join(run, 'artifacts/codex-test');
execFileSync(process.execPath, [path.join(project, 'tools/codex-test/prepare-samples.mjs'), runtime], { encoding: 'utf8', windowsHide: true });
const fake = path.join(project, `tests/DiagramMaker.FakeVllm/bin/Release/net9.0/DiagramMaker.FakeVllm${process.platform === 'win32' ? '.exe' : ''}`);
const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
  CodexTest__Enabled: 'true', CodexTest__RuntimeRoot: runtime, CodexTest__AssetsRoot: path.join(project, 'tools/codex-test'),
  CodexTest__ExecutablePath: fake, CodexTest__RequestTimeoutSeconds: '20',
  DIAGRAMMAKER_LLM_POLICY_PATH: path.join(run, 'must-not-be-read.json'),
  GitWorker__ScriptPath: path.join(project, 'tools/git-worker/index.mjs') };
let output = '';
const child = spawn('dotnet', [path.join(project, 'src/DiagramMaker.Api/bin/Release/net9.0/DiagramMaker.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
  cwd: path.join(project, 'src/DiagramMaker.Api'), env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
});
child.stdout.on('data', data => { output += data; });
child.stderr.on('data', data => { output += data; });
let origin;
try {
  for (let i = 0; i < 100; i++) {
    origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
    if (origin) break;
    if (child.exitCode !== null) throw new Error(`Sample API failed: ${output}`);
    await delay(100);
  }
  assert.ok(origin, `Test API must start: ${output}`);
  async function request(url, method = 'GET', body, status = 200, headers = {}) {
    const response = await fetch(`${origin}/api/v1${url}`, { method, headers: { 'Content-Type': 'application/json', ...headers },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(60_000) });
    const value = await response.json();
    assert.equal(response.status, status, `${method} ${url}: ${JSON.stringify(value)}`);
    return value;
  }
  async function poll(url, states) {
    for (let i = 0; i < 160; i++) {
      const value = await request(url);
      if (states.includes(value.state)) return value;
      assert.notEqual(value.state, 'Failed', JSON.stringify(value));
      await delay(200);
    }
    throw new Error(`Timed out: ${url}`);
  }
  const mode = await request('/runtime-info');
  assert.equal(mode.mode, 'codex-sample');
  assert.equal(mode.capabilities.thinkingControl, false);
  const catalog = await request('/sample-tests/scenarios');
  assert.equal(catalog.scenarios.length, 3);
  assert.equal((await request('/repositories')).length, 2);
  for (const url of ['/repositories', '/repositories/inspect', '/analysis-plans', '/analyses', '/natural-diagrams', '/natural-diagrams/123/regenerate', '/llm/tests/thinking-contract'])
    await request(url, 'POST', { prompt: 'UNAPPROVED_INPUT_MUST_NOT_LEAVE', localPath: project }, 403);
  await request('/sample-tests/cpp-packets/plan', 'POST', undefined, 403, { Origin: 'https://untrusted.invalid' });
  await request('/sample-tests/natural-orders/generate', 'POST', { prompt: 'UNAPPROVED_INPUT_MUST_NOT_LEAVE' }, 400);
  await request('/sample-tests/natural-orders/generate', 'POST', { refinementId: 'arbitrary company text' }, 400);
  assert.equal((await request('/runtime-info')).codex.calls, 0);

  for (const scenarioId of ['cpp-packets', 'csharp-orders']) {
    const queued = await request(`/sample-tests/${scenarioId}/plan`, 'POST', undefined, 202);
    const plan = await poll(`/analysis-plans/${queued.id}`, ['Ready']);
    assert.ok(plan.candidates.length > 0, `${scenarioId} must have actual changes`);
    const created = await request(`/sample-tests/${scenarioId}/generate`, 'POST', { planId: plan.id, refinementId: 'summarize' }, 202);
    const analysis = await poll(`/analyses/${created.id}?includeGraph=false&summary=true`, ['Completed', 'Partial']);
    assert.equal(analysis.testMetadata.scenarioId, scenarioId);
    assert.equal(analysis.testMetadata.refinementId, 'summarize');
    const views = analysis.result.diagramGroups[0].views;
    assert.equal(views.length, 4);
    for (const view of views) {
      assert.ok(view.diagram, `${scenarioId}/${view.viewId}: ${JSON.stringify(view)}`);
      const artifact = await request(`/analyses/${analysis.id}/groups/sample-changes/views/${view.viewId}/pages/overview`);
      assert.ok(artifact.ir.nodes.length > 0);
      const removed = artifact.ir.nodes[0].id;
      const nodes = artifact.ir.nodes.filter(node => node.id !== removed);
      if (nodes.length === 0) nodes.push({ id: 'manual', label: 'LOCAL_MANUAL_TEXT_NEVER_TO_MODEL' });
      const document = { title: 'LOCAL_MANUAL_TEXT_NEVER_TO_MODEL', direction: artifact.ir.direction, nodes,
        edges: artifact.ir.edges.filter(edge => edge.sourceId !== removed && edge.targetId !== removed) };
      const edited = await request(`/analyses/${analysis.id}/groups/sample-changes/views/${view.viewId}/edits?pageId=overview`, 'POST',
        { rootArtifactId: artifact.id, expectedVersion: artifact.version, document }, 201);
      assert.ok(edited.version > artifact.version);
    }
    const calls = (await request('/runtime-info')).codex.calls;
    await request(`/analyses/${analysis.id}?summary=true&includeGraph=false`);
    await request('/analysis-plans?summary=true');
    assert.equal((await request('/runtime-info')).codex.calls, calls, 'Loading history must not invoke CLI');
    const redrawn = await request(`/sample-tests/${scenarioId}/generate`, 'POST', {
      planId: plan.id, refinementId: 'exceptions', diagramTypes: ['flowchart'], direction: 'TB', detailLevel: 'detailed',
    }, 202);
    const result = await poll(`/analyses/${redrawn.id}?summary=true&includeGraph=false`, ['Completed', 'Partial']);
    assert.equal(result.testMetadata.refinementId, 'exceptions');
    assert.equal(result.result.diagramGroups[0].views[0].selection.overrides.direction, 'TB');
    assert.ok((await request('/runtime-info')).codex.calls > calls);
  }
  const natural = await request('/sample-tests/natural-orders/generate', 'POST', { refinementId: 'connected' });
  assert.equal(natural.kind, 'natural');
  assert.equal(natural.record.views.length, 4);
  assert.ok(natural.record.views.every(view => view.diagram), JSON.stringify(natural.record.views));
  assert.equal(natural.record.request.testMetadata.provider, 'codex-cli');
  const manifest = JSON.parse(await readFile(path.join(runtime, 'samples-manifest.json'), 'utf8'));
  const modified = manifest.repositories[0];
  const scenario = catalog.scenarios.find(item => item.id === modified.scenarioId);
  await writeFile(path.join(modified.localPath, scenario.fileName), 'UNAPPROVED_INPUT_MUST_NOT_LEAVE');
  await request(`/sample-tests/${modified.scenarioId}/plan`, 'POST', undefined, 400);
  await writeFile(path.join(modified.localPath, scenario.fileName), scenario.after);
  assert.ok(!output.includes('UNAPPROVED_INPUT_MUST_NOT_LEAVE'));
  console.log('Codex sample smoke passed with FAKE CLI: two Git fixtures, four views, natural four views, redraw, local edits, history, origin/input/source guards. No real Codex calls.');
} finally {
  if (origin) await fetch(`${origin}/api/v1/sample-tests/shutdown`, { method: 'POST' }).catch(() => undefined);
  for (let i = 0; i < 50 && child.exitCode === null; i++) await delay(100);
  if (child.exitCode === null) child.kill();
  await writeFile(path.join(run, 'server.log'), output);
  console.log('Sample test server log: ' + path.relative(project, path.join(run, 'server.log')));
}
