// Synthetic contract/performance regression. Never measures corporate model quality or latency.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, realpath, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { once } from 'node:events';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath, gitEnvironment } from '../tools/git-worker/local-security.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
const fixture = await mkdtemp(path.join(root, 'artifacts/shared-semantics-'));
const packageArgument = process.argv.slice(2).find(value => !value.startsWith('--'));
const packageRoot = packageArgument ? await realpath(path.resolve(root, packageArgument)) : null;
const maxInputCharacters = process.argv.includes('--characters-60000') ? 60000 : 2000000;
if (packageRoot) {
  const relative = path.relative(await realpath(path.join(root, 'artifacts/stage')), packageRoot);
  assert.ok(relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative));
}
const source = increment => Array.from({ length: 4 }, (_, c) => `class Example${c} {\npublic:\n` +
  Array.from({ length: 10 }, (_, m) => `  int Task${m}(int input) {\n` +
    Array.from({ length: 27 }, (_, i) => `    input += ${i + increment};`).join('\n') + '\n    return input;\n  }').join('\n') + '\n};').join('\n');
assert.equal(source(1).split('\n').length, 1212);
const repositoryPath = path.join(fixture, 'repository');
await mkdir(repositoryPath);
const git = (...args) => execFileSync('git', args, { cwd: repositoryPath, encoding: 'utf8', windowsHide: true, env: gitEnvironment() }).trim();
git('init', '-b', 'main'); git('config', 'user.name', 'Synthetic Test'); git('config', 'user.email', 'synthetic@example.invalid');
await writeFile(path.join(repositoryPath, 'Example.cpp'), source(1)); git('add', 'Example.cpp'); git('commit', '-m', 'Synthetic baseline');
const baseSha = git('rev-parse', 'HEAD');
await writeFile(path.join(repositoryPath, 'Example.cpp'), source(2)); git('add', 'Example.cpp'); git('commit', '-m', 'Synthetic increment change');
const targetSha = git('rev-parse', 'HEAD');
let requestCount = 0, output = '', server, closed;
const checks = [];
const batches = [];
const llm = createServer(async (request, response) => {
  try {
    assert.equal(request.url, '/v1/chat/completions');
    let body = ''; for await (const chunk of request) body += chunk;
    const payload = JSON.parse(body);
    assert.equal(payload.chat_template_kwargs.enable_thinking, false);
    const properties = payload.structured_outputs.json.properties;
    let context = JSON.parse(payload.messages[1].content);
    context = context.context ?? context;
    const reviewing = !!properties.items?.items.properties.issues;
    if (properties.items) batches.push({ source: context.sourceKind, reviewing, outputLimit: payload.max_tokens, items: context.items.length,
      kinds: context.items.map(item => item.kind), characters: payload.messages[1].content.length,
      facts: context.sources.facts.length, sourceCharacters: context.sources.facts.reduce((n, f) => n + (f.content?.length ?? 0), 0) });
    const result = reviewing ? { items: context.items.map(item => ({ id: item.id, issues: [] })) } : {
      summary: '원본 코드의 입력값을 가공하고 결과를 반환합니다', recommendedType: context.available[0],
      items: context.items.map(item => ({ id: item.id, summary: '입력값을 누적하고 반환합니다', description: '원본 근거에 표시된 값을 누적하고 호출한 곳으로 반환합니다' })),
    };
    requestCount++;
    response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
      choices: [{ message: { content: JSON.stringify(result) }, finish_reason: 'stop' }],
      usage: { prompt_tokens: 1000, completion_tokens: 200, total_tokens: 1200 },
    }));
  } catch (error) { output += `Synthetic fixture error: ${error.message}\n`; response.writeHead(500).end(); }
});
try {
  llm.listen(0, '127.0.0.1'); await once(llm, 'listening');
  const llmOrigin = `http://127.0.0.1:${llm.address().port}`;
  const policy = path.join(fixture, 'synthetic-llm.json');
  const networkPolicy = path.join(fixture, 'network-policy.json');
  await writeFile(policy, JSON.stringify({ Llm: { Enabled: true, Endpoint: llmOrigin + '/v1/chat/completions',
    AllowedOrigin: llmOrigin, UseServerTokenization: false, MaxTransientRetries: 0, MaxInputCharacters: maxInputCharacters,
    // Match the shipped policy example instead of inheriting appsettings' larger review budget.
    DiagramOutputTokens: 8000, ReviewOutputTokens: 2000 } }));
  await writeFile(networkPolicy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [llmOrigin], LlmAddressRanges: ['127.0.0.1/32'], Databases: [] }));
  const runtime = packageRoot ?? path.join(fixture, 'api');
  if (!packageRoot) {
    await cp(path.join(root, 'src/DiagramMaker.Api/bin/Release/net9.0'), runtime, { recursive: true });
    await cp(path.join(root, 'artifacts/verify-web-dist'), path.join(runtime, 'wwwroot'), { recursive: true });
  }
  server = spawn(packageRoot ? path.join(runtime, 'DiagramMaker.Api.exe') : 'dotnet',
    [...(packageRoot ? [] : [path.join(runtime, 'DiagramMaker.Api.dll')]), '--urls', 'http://127.0.0.1:0'], {
      cwd: runtime, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development', Storage__Provider: 'LocalFile',
        Storage__LocalFilePath: path.join(fixture, 'data/store.json'), Security__TrustReverseProxyHeaders: 'false', CodexTest__Enabled: 'false',
        DIAGRAMMAKER_LLM_POLICY_PATH: policy, DIAGRAMMAKER_NETWORK_POLICY_PATH: networkPolicy,
        GitWorker__ScriptPath: path.join(packageRoot ?? root, 'tools/git-worker/index.mjs'),
        ...(packageRoot ? { GitWorker__NodeExecutable: path.join(packageRoot, 'runtime/node/node.exe') } : {}) },
    });
  closed = new Promise(resolve => server.once('close', resolve));
  server.stdout.on('data', data => { output += data; }); server.stderr.on('data', data => { output += data; });
  let origin;
  for (let i = 0; i < 150; i++) {
    origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1]; if (origin) break;
    assert.equal(server.exitCode, null, output); await delay(100);
  }
  assert.ok(origin, output);
  async function request(url, method = 'GET', body, status = 200) {
    const response = await fetch(origin + '/api/v1' + url, { method, headers: { 'content-type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
    const result = await response.json(); assert.equal(response.status, status, JSON.stringify(result)); return result;
  }
  async function poll(url) {
    for (let i = 0; i < 600; i++) {
      const result = await request(url);
      if (['Completed', 'Partial', 'Failed', 'Ready'].includes(result.state)) return result;
      await delay(100);
    }
    throw new Error('Synthetic pipeline did not finish');
  }
  const views = ['flowchart', 'class', 'code-relation'].map(diagramType => ({ id: diagramType, diagramType, presetId: 'balanced' }));
  for (const selected of [views.slice(0, 1), views]) {
    const workspace = await request('/code-block-workspaces', 'POST', { title: '고정 합성 C++ 성능',
      blocks: [{ id: 'code', language: 'cpp', title: 'C++', code: source(1) }], groups: [{ id: 'g', title: '전체', blockIds: ['code'], views: selected }] }, 201);
    const before = requestCount;
    const beforeBatch = batches.length;
    const queued = await request(`/code-block-workspaces/${workspace.id}/runs`, 'POST', { expectedRevision: workspace.revision }, 202);
    const run = await poll(`/code-block-runs/${queued.id}`);
    assert.equal(run.state, 'Completed', run.errorMessage ?? JSON.stringify(run.results.map(g => g.views.map(v => v.warnings))));
    const requests = requestCount - before;
    const generationRequests = batches.slice(beforeBatch).filter(b => !b.reviewing).length;
    assert.equal(generationRequests, selected.length === 1 ? 2 : 3, 'Generation count must retain the perf.2 baseline');
    assert.ok(requests > 0 && requests <= 30, `C++ ${selected.length} formats: ${requests} requests`);
    assert.ok(batches.filter(b => b.reviewing).every(b => b.items <= 16 && b.outputLimit === 2000), 'Reviews must fit the default output budget');
    const diagnostic = await request(`/code-block-runs/${run.id}/diagnostics`);
    assert.equal(diagnostic.version, 2); assert.equal(diagnostic.execution.transportRequests, requests);
    let pages = 0;
    for (const view of run.results[0].views) for (const page of view.pages) {
      const artifact = await request(`/code-block-runs/${run.id}/groups/g/views/${view.viewId}/pages/${page.id}`);
      assert.equal(artifact.explanation.status, 'Semantic'); assert.ok(artifact.mermaidDsl); pages++;
    }
    assert.equal(pages, selected.length === 1 ? 41 : 43, 'All baseline pages must remain');
    checks.push({ name: 'code-block', lines: 1212, functions: 40, formats: selected.length, requests, generationRequests, reviewRequests: requests - generationRequests, pages });
    const next = await request(`/code-block-workspaces/${workspace.id}/runs`, 'POST', { expectedRevision: workspace.revision }, 202);
    assert.equal((await poll(`/code-block-runs/${next.id}`)).state, 'Completed');
    assert.equal(requestCount - before, requests, 'Unchanged completed results should not call the LLM again');
  }
  const repository = await request('/repositories', 'POST', { name: 'Synthetic C++', localPath: repositoryPath, defaultBranch: 'main', allowedRoles: ['Reviewer'] }, 201);
  const queuedPlan = await request('/analysis-plans', 'POST', { repositoryId: repository.id, baseRevision: baseSha, targetRevision: targetSha, useLlmGrouping: false }, 202);
  let plan = await poll(`/analysis-plans/${queuedPlan.id}`); assert.equal(plan.state, 'Ready');
  assert.ok(plan.candidates.length >= 40);
  const group = { id: 'g', title: '증가량 변경', changeIds: plan.candidates.map(c => c.id), diagramType: 'flowchart', presetId: 'balanced', views };
  plan = await request(`/analysis-plans/${plan.id}/selection`, 'PUT', { expectedRevision: plan.revision, groups: [group] });
  const before = requestCount;
  const queued = await request(`/analysis-plans/${plan.id}/generate`, 'POST', { expectedRevision: plan.revision }, 202);
  const analysis = await poll(`/analyses/${queued.id}?includeGraph=false&summary=true`);
  assert.ok(['Completed', 'Partial'].includes(analysis.state), analysis.errorMessage);
  let pages = 0;
  for (const view of analysis.result.diagramGroups[0].views) {
    assert.equal(view.generationMetadata.llmStatus, 'Semantic', JSON.stringify(view.warnings));
    for (const page of view.document.pages) {
      const artifact = await request(`/analyses/${analysis.id}/groups/g/views/${view.viewId}/pages/${page.id}`);
      assert.equal(artifact.explanation.status, 'Semantic'); pages++;
    }
  }
  const requests = requestCount - before;
  assert.ok(requests <= 64, `Git request count: ${requests}`);
  assert.ok(batches.filter(b => b.source === 'git' && !b.reviewing).length <= 10, 'Git generation must remain shared across pages');
  assert.equal(pages, 51, 'All Git baseline pages must remain');
  const generationRequests = batches.filter(b => b.source === 'git' && !b.reviewing).length;
  checks.push({ name: 'git', lines: 1212, functions: 40, formats: 3, requests, generationRequests, reviewRequests: requests - generationRequests, pages });
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', syntheticOnly: true, packageRoot, checks }, null, 2));
  console.log(JSON.stringify({ fixture, checks }));
} catch (error) {
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'failed', syntheticOnly: true, checks, batches, error: String(error) }, null, 2));
  throw error;
} finally {
  if (server?.exitCode === null) { server.kill(); await Promise.race([closed, delay(5000)]); }
  await writeFile(path.join(fixture, 'server.log'), output);
  llm.closeAllConnections(); await new Promise(resolve => llm.close(resolve));
}
