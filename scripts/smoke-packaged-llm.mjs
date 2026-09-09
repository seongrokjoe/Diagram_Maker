// Exercise the packaged HTTP transport with synthetic loopback responses only.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdtemp, realpath, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
assert.ok(process.argv[2], 'Pass the unpacked package directory under artifacts/stage');
const packageRoot = await realpath(path.resolve(root, process.argv[2]));
const basicMode = process.argv.includes('--basic');
const relative = path.relative(await realpath(path.join(root, 'artifacts/stage')), packageRoot);
assert.ok(relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative));
const fixture = await mkdtemp(path.join(root, 'artifacts/packaged-llm-'));
const checks = [], requests = [];
let mode = 'valid', output = '', child, closed, origin, redirected = 0;
const diagram = {
  type: 'flowchart', title: 'Synthetic Diagram',
  nodes: ['SyntheticClient', 'SyntheticService', 'SyntheticStore'].map((label, index) => ({
    id: `n${index}`, label, kind: 'component', group: null, status: 'unchanged', confidence: 'Inferred', evidenceIds: [],
  })),
  edges: [0, 1].map(index => ({ id: `e${index}`, sourceId: `n${index}`, targetId: `n${index + 1}`,
    type: 'flow', label: 'request', status: 'unchanged', confidence: 'Inferred', evidenceIds: [], sequenceIndex: index })),
  notes: [], provenance: [],
};
const llm = createServer(async (request, response) => {
  try {
    if (request.url === '/redirect-target') { redirected++; response.writeHead(500).end(); return; }
    assert.equal(request.url, '/v1/chat/completions');
    assert.equal(request.method, 'POST');
    assert.equal(request.headers.authorization, undefined);
    let body = '';
    for await (const chunk of request) { body += chunk; assert.ok(body.length < 100000); }
    const payload = JSON.parse(body);
    assert.equal(payload.model, 'synthetic-package-test');
    const thinking = payload.chat_template_kwargs?.enable_thinking === true;
    const structured = !!payload.structured_outputs?.json;
    requests.push({ thinking, structured, mode });
    if (mode === 'redirect') {
      response.writeHead(307, { location: origin + '/redirect-target' }).end(); return;
    }
    const content = mode === 'malformed' ? 'not-json' : !structured ? 'OK'
      : JSON.stringify(thinking ? { result: 'ok' } : diagram);
    response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
      choices: [{ message: { role: 'assistant', content, reasoning_content: thinking ? 'synthetic-only' : null }, finish_reason: 'stop' }],
      usage: { prompt_tokens: 12, completion_tokens: 120, total_tokens: 132 },
    }));
  } catch (error) { response.writeHead(500).end(); output += `Fixture error: ${error.message}\n`; }
});
try {
  llm.listen(0, '127.0.0.1'); await once(llm, 'listening');
  origin = `http://127.0.0.1:${llm.address().port}`;
  const llmPolicy = path.join(fixture, 'synthetic-llm.json');
  const networkPolicy = path.join(fixture, 'test-network-policy.json');
  await writeFile(llmPolicy, JSON.stringify({ Llm: { Enabled: true, AllowDevelopmentStub: false,
    Endpoint: origin + '/v1/chat/completions', AllowedOrigin: origin, Model: 'synthetic-package-test', MaxTransientRetries: 0 } }));
  await writeFile(networkPolicy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [origin],
    LlmAddressRanges: ['127.0.0.1/32'], Databases: [] }));
  child = spawn(path.join(packageRoot, 'DiagramMaker.Api.exe'), ['--urls', 'http://127.0.0.1:0'], {
    cwd: packageRoot, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
      Storage__Provider: 'InMemory', Security__TrustReverseProxyHeaders: 'false', CodexTest__Enabled: 'false',
      DIAGRAMMAKER_LLM_POLICY_PATH: llmPolicy, DIAGRAMMAKER_NETWORK_POLICY_PATH: basicMode ? '' : networkPolicy,
      GitWorker__NodeExecutable: path.join(packageRoot, 'runtime/node/node.exe'),
      GitWorker__ScriptPath: path.join(packageRoot, 'tools/git-worker/index.mjs') },
  });
  let startupError;
  child.on('error', error => { startupError = error; });
  closed = new Promise(resolve => child.once('close', resolve));
  child.stdout.on('data', data => { output += data; }); child.stderr.on('data', data => { output += data; });
  let api;
  for (let i = 0; i < 150; i++) {
    if (startupError) throw startupError;
    api = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
    if (api) break;
    assert.equal(child.exitCode, null, output); await delay(100);
  }
  assert.ok(api, output);
  async function test(name, status = 200) {
    const response = await fetch(`${api}/api/v1/llm/tests/${name}`, { method: 'POST', signal: AbortSignal.timeout(30000) });
    const value = await response.json();
    assert.equal(response.status, status, JSON.stringify(value));
    if (status === 200) assert.equal(value.success, true);
    return value;
  }
  await test('connection'); checks.push('connection');
  const contract = await test('diagram-contract');
  assert.equal(contract.nodeCount, 3); assert.equal(contract.edgeCount, 2);
  checks.push('diagram-contract');
  const thinking = await test('thinking-contract'); assert.equal(thinking.thinkingEnabled, true);
  checks.push('thinking-contract');
  assert.deepEqual(requests.map(({ thinking, structured }) => ({ thinking, structured })), [
    { thinking: false, structured: false }, { thinking: false, structured: true }, { thinking: true, structured: true },
  ]);
  mode = 'malformed'; await test('diagram-contract', 503); checks.push('malformed response rejected');
  mode = 'redirect'; await test('connection', 503);
  assert.equal(redirected, 0); checks.push('HTTP redirect rejected');
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', checks, requests,
    networkMode: basicMode ? 'basic' : 'restricted', syntheticOnly: true, corporateLlmTested: false }, null, 2));
  console.log(`Packaged LLM transport (${basicMode ? 'basic' : 'restricted'}): ${checks.length} checks passed. ${path.relative(root, fixture)}`);
} catch (error) {
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'failed', checks, error: String(error) }, null, 2));
  throw error;
} finally {
  if (child?.exitCode === null) { child.kill(); await Promise.race([closed, delay(5000)]); }
  llm.closeAllConnections(); await new Promise(resolve => llm.close(resolve));
  await writeFile(path.join(fixture, 'server.log'), output);
}
