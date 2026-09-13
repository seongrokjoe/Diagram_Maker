// Exercise the actual UI-test CMD entry points with private data and synthetic code.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { copyFile, mkdir, mkdtemp, readFile, readdir, rm, stat, utimes, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';
import { copySourceInputs, readSourceInputs } from './ui-test-build.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
assert.equal(process.platform, 'win32', 'The UI-test launcher requires Windows.');
const fixture = await mkdtemp(path.join(root, 'artifacts/ui-test-launchers-'));
// Spaces and Korean characters also exercise CMD/PowerShell path quoting.
const source = path.join(fixture, 'UI 테스트 source');
const local = path.join(fixture, 'local');
await copySourceInputs(root, source, await readSourceInputs(root));
await mkdir(path.join(local, 'DiagramMaker'), { recursive: true });
for (const file of ['start-ui-test.cmd', 'stop-ui-test.cmd']) {
  await copyFile(path.join(root, file), path.join(source, file));
}
const key = createHash('sha256').update(source.toLowerCase()).digest('hex').slice(0, 12);
const runtime = path.join(local, 'DiagramMaker/UiTest', key);
const statePath = path.join(runtime, 'process.json');
const policyPath = path.join(fixture, 'explicit-policy.json');
await writeFile(policyPath, JSON.stringify({ LocalRoots: [fixture], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
const sentinel = 'Unrelated user configuration must remain unchanged.';
const sentinels = ['llm-policy.json', 'network-policy.json'];
for (const file of sentinels) await writeFile(path.join(local, 'DiagramMaker', file), sentinel);
const environment = { ...process.env, LOCALAPPDATA: local, DIAGRAMMAKER_NETWORK_POLICY_PATH: '',
  npm_config_cache: process.env.npm_config_cache ?? path.join(process.env.LOCALAPPDATA, 'npm-cache'),
  NUGET_PACKAGES: process.env.NUGET_PACKAGES ?? path.join(process.env.USERPROFILE, '.nuget/packages'),
  DIAGRAMMAKER_LLM_POLICY_PATH: path.join(fixture, 'missing-llm-policy.json'),
  Llm__Enabled: 'true', Llm__AllowDevelopmentStub: 'true', Storage__Provider: 'Postgres',
  Security__TrustReverseProxyHeaders: 'true', CodexTest__Enabled: 'true',
  Kestrel__Endpoints__Injected__Url: 'http://0.0.0.0:1025', ASPNETCORE_URLS: 'http://0.0.0.0:1026' };
const checks = [];
let commandIndex = 0;
let origin;
let lastState;
const occupied = createServer(socket => socket.end());
occupied.listen(0, '127.0.0.1');
await once(occupied, 'listening');
const port = occupied.address().port;
assert.ok(port <= 65515, 'The fixture needs twenty subsequent candidate ports.');

async function command(name, args = [], overrides = {}, expected = 0, match) {
  // Only fixed script names and validated numeric ports are passed through CMD.
  const child = spawn(process.env.ComSpec ?? 'cmd.exe', ['/d', '/c', name, ...args], {
    cwd: source, env: { ...environment, ...overrides }, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
  });
  let output = '';
  child.stdout.on('data', data => output += data);
  child.stderr.on('data', data => output += data);
  child.stdin.end('\r\n'); // Error CMDs pause; EOF permits unattended failure checks.
  const timer = setTimeout(() => child.kill(), 300_000);
  let exitCode;
  // Start-Process descendants can retain inherited pipe handles after CMD exits.
  // Observe the command's own exit and close our readers explicitly.
  try { [exitCode] = await once(child, 'exit'); }
  finally {
    clearTimeout(timer);
    child.stdout.destroy();
    child.stderr.destroy();
    await writeFile(path.join(fixture, `${String(commandIndex++).padStart(2, '0')}-${name}.log`), output);
  }
  if (expected === 0) assert.equal(exitCode, 0, output);
  else assert.ok(Number.isInteger(exitCode) && exitCode !== 0, output);
  if (match) assert.match(output, match);
  return output;
}
const start = (overrides, expected, match) => command('start-ui-test.cmd', ['-NoBrowser', '-Port', String(port)], overrides, expected, match);
const stop = () => command('stop-ui-test.cmd');
const readJson = async file => JSON.parse((await readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
async function readState() {
  lastState = await readJson(statePath);
  origin = `http://127.0.0.1:${lastState.port}`;
  return lastState;
}
async function request(route, method = 'GET', body, expected = 200) {
  const response = await fetch(`${origin}/api/v1${route}`, { method,
    headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(15_000) });
  const value = await response.json();
  assert.equal(response.status, expected, JSON.stringify(value));
  return value;
}
async function assertStopped() {
  await assert.rejects(() => fetch(`${origin}/health`, { signal: AbortSignal.timeout(2000) }));
  await assert.rejects(() => stat(statePath), { code: 'ENOENT' });
  assert.equal(occupied.listening, true, 'The occupied unrelated port must remain listening.');
}

try {
  await start(undefined, 0, /UI test ready:/);
  const first = await readState();
  assert.ok(first.port > port && first.port <= port + 20);
  assert.match(first.sourceHash, /^[a-f0-9]{64}$/);
  assert.match(await (await fetch(origin)).text(), new RegExp('ui-build=' + first.sourceHash));
  checks.push('CMD builds current project without a ZIP and avoids occupied port');
  const input = { title: '재시작 보존', blocks: [{ id: 'cpp', language: 'cpp', title: '합성 C++',
    code: 'int Next(int x) { if (x > 0) return x + 1; return 0; }' }],
  groups: [{ id: 'main', title: '테스트', blockIds: ['cpp'], views: [{ id: 'flow', diagramType: 'flowchart', presetId: 'flow-vertical-overview' }] }] };
  const workspace = await request('/code-block-workspaces', 'POST', input, 201);
  const queued = await request(`/code-block-workspaces/${workspace.id}/runs`, 'POST', { expectedRevision: 1 }, 202);
  let run;
  for (let attempt = 0; attempt < 120; attempt++) {
    run = await request(`/code-block-runs/${queued.id}`);
    if (['Completed', 'Partial', 'Failed'].includes(run.state)) break;
    await delay(250);
  }
  assert.equal(run.state, 'Partial', JSON.stringify(run));
  assert.equal(run.results[0].views[0].llmStatus, 'Incomplete');
  assert.ok(run.results[0].views[0].pages.length, 'Current worker must parse the C++ source.');
  checks.push('current API and C++ worker operate with isolated data and LLM disabled despite inherited settings');
  await start(undefined, 0, /Already running with the current project:/);
  assert.deepEqual(await readState(), first);
  checks.push('second start reuses exact existing process');
  await stop();
  await assertStopped();
  checks.push('CMD stop releases only the UI-test server');
  await start({ DIAGRAMMAKER_NETWORK_POLICY_PATH: policyPath });
  const restarted = await readState();
  assert.equal(restarted.appFolder, first.appFolder, 'Untouched source build must be reused.');
  assert.equal((await request(`/code-block-workspaces/${workspace.id}`)).input.title, input.title);
  assert.equal((await request(`/code-block-runs/${queued.id}`)).state, 'Partial');
  checks.push('explicit approved policy, cache reuse and saved workspace/run survive restart');

  // A stale PID record pointing to an unrelated executable must not stop it.
  await writeFile(statePath, JSON.stringify({ ...restarted, apiPid: process.pid }));
  await stop();
  assert.equal((await fetch(`${origin}/health`)).status, 200);
  await writeFile(statePath, JSON.stringify(restarted));
  for (const change of [{ apiStartedAt: '2000-01-01T00:00:00.000Z' }, { appFolder: `${first.sourceHash.slice(0, 12)}-00000000` }]) {
    await writeFile(statePath, JSON.stringify({ ...restarted, ...change }));
    await stop();
    assert.equal((await fetch(`${origin}/health`)).status, 200);
    await writeFile(statePath, JSON.stringify(restarted));
  }
  checks.push('stop rejects unrelated PID, mismatched start time and mismatched executable path');
  await stop();
  await assertStopped();
  await stop();
  checks.push('repeated stop succeeds without deleting data');

  // Rebuild both layers and preserve data without relying on modification times.
  const cssPath = path.join(source, 'web/src/styles.css');
  const originalCss = await readFile(cssPath, 'utf8');
  const cssTime = await stat(cssPath);
  await writeFile(cssPath, originalCss + '\n.ui-current-source-proof { color: #123456; }\n');
  await utimes(cssPath, cssTime.atime, cssTime.mtime);
  const programPath = path.join(source, 'src/DiagramMaker.Api/Program.cs');
  const originalProgram = await readFile(programPath, 'utf8');
  assert.ok(originalProgram.includes('app.Run();'));
  await writeFile(programPath, originalProgram.replace('app.Run();',
    'app.MapGet("/ui-source-proof", () => "current-api-proof");\napp.Run();'));
  const addedSource = path.join(source, 'web/src/ui-source-proof.ts');
  await writeFile(addedSource, 'export const proof: string = "added-file";\n');
  await start();
  const changed = await readState();
  assert.notEqual(changed.sourceHash, first.sourceHash);
  assert.equal(await (await fetch(`${origin}/ui-source-proof`)).text(), 'current-api-proof');
  assert.match(await (await fetch(`${origin}/assets/app.css`)).text(), /ui-current-source-proof/);
  assert.equal((await request(`/code-block-workspaces/${workspace.id}`)).input.title, input.title);
  checks.push('content change with unchanged timestamp and added source rebuild current UI and API');

  // A compile failure must not stop or silently replace the healthy old server.
  await writeFile(addedSource, 'export const proof: string = 123;\n');
  await start(undefined, 1, /Current-project UI build failed/);
  assert.deepEqual(await readState(), changed);
  assert.equal(await (await fetch(`${origin}/ui-source-proof`)).text(), 'current-api-proof');
  checks.push('failed current build reports failure and preserves the existing server and saved data');
  await rm(addedSource);
  await writeFile(cssPath, originalCss);
  await writeFile(programPath, originalProgram);
  await start(undefined, 0, /Restarting this UI test server/);
  const refreshed = await readState();
  assert.notEqual(refreshed.apiPid, changed.apiPid);
  assert.equal(refreshed.sourceHash, first.sourceHash);
  assert.doesNotMatch(await (await fetch(`${origin}/assets/app.css`)).text(), /ui-current-source-proof/);
  assert.notEqual(await (await fetch(`${origin}/ui-source-proof`)).text(), 'current-api-proof');
  assert.equal((await request(`/code-block-runs/${queued.id}`)).state, 'Partial');
  checks.push('deleted source and reverted UI/API replace the running build without stale outputs or data loss');
  await stop();
  await assertStopped();

  const cachedApp = path.join(runtime, 'apps', refreshed.appFolder);
  await writeFile(path.join(cachedApp, 'wwwroot/index.html'), 'Synthetic damaged cache');
  await start();
  const repaired = await readState();
  assert.notEqual(repaired.appFolder, refreshed.appFolder);
  assert.equal((await request(`/code-block-workspaces/${workspace.id}`)).input.title, input.title);
  await stop();
  await assertStopped();
  checks.push('damaged source build is rebuilt while saved workspaces are retained');

  await start({ DIAGRAMMAKER_NETWORK_POLICY_PATH: path.join(fixture, 'missing-policy.json') }, 1, /network-policy.json is required/);
  await writeFile(policyPath, JSON.stringify({ LocalRoots: [path.join(fixture, 'denied')] }));
  await start({ DIAGRAMMAKER_NETWORK_POLICY_PATH: policyPath }, 1, /outside the approved local roots/);
  await writeFile(policyPath, 'not-json');
  await start({ DIAGRAMMAKER_NETWORK_POLICY_PATH: policyPath }, 1);
  checks.push('missing, denied and malformed explicit policies fail closed');
  const sdkPath = path.join(source, 'global.json');
  const originalSdk = await readFile(sdkPath, 'utf8');
  await writeFile(sdkPath, JSON.stringify({ sdk: { version: '99.0.100', rollForward: 'disable' } }));
  await start(undefined, 1, /Current-project UI build failed/);
  await writeFile(sdkPath, originalSdk);
  await assertStopped();
  checks.push('unavailable project SDK fails without downloading or serving a stale build');
  for (const file of sentinels) assert.equal(await readFile(path.join(local, 'DiagramMaker', file), 'utf8'), sentinel);
  assert.ok((await readdir(path.join(runtime, 'logs'))).length > 0);
  checks.push('existing configuration, test data and logs are preserved');
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', checks, syntheticOnly: true, sourceHash: first.sourceHash }, null, 2));
  console.log(`UI test launchers: ${checks.length} checks passed. ${path.relative(root, fixture)}`);
} catch (error) {
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'failed', checks, error: String(error) }, null, 2));
  throw error;
} finally {
  // Restore our last known identity so failed negative cases can still clean up our API.
  if (lastState) await writeFile(statePath, JSON.stringify(lastState));
  await stop();
  await new Promise(resolve => occupied.close(resolve));
}
