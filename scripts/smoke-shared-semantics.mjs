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
import { executionMeaningFixture } from './execution-meaning-fixture.mjs';
import { reliabilitySource as source } from './reliability-fixture.mjs';
import { checkVariants, checkVariantUi, checkGitVariantUi } from './ai-code-variant-checks.mjs';
import { naturalDesignFixture, checkNaturalDesign } from './natural-design-fixture.mjs';
import { checkPartialGitAi } from './partial-ai-ui-checks.mjs';

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
assert.equal(source(1).split('\n').length, 1212);
const repositoryPath = path.join(fixture, 'repository');
await mkdir(repositoryPath);
const git = (...args) => execFileSync('git', args, { cwd: repositoryPath, encoding: 'utf8', windowsHide: true, env: gitEnvironment() }).trim();
git('init', '-b', 'main'); git('config', 'user.name', 'Synthetic Test'); git('config', 'user.email', 'synthetic@example.invalid');
await writeFile(path.join(repositoryPath, 'Example.cpp'), source(1)); git('add', 'Example.cpp'); git('commit', '-m', 'Synthetic baseline');
const baseSha = git('rev-parse', 'HEAD');
await writeFile(path.join(repositoryPath, 'Example.cpp'), source(2)); git('add', 'Example.cpp'); git('commit', '-m', 'Synthetic increment change');
const targetSha = git('rev-parse', 'HEAD');
let requestCount = 0, output = '', server, closed, testMode = 'valid';
const checks = [];
const batches = [];
const llm = createServer(async (request, response) => {
  try {
    assert.equal(request.url, '/v1/chat/completions');
    let body = ''; for await (const chunk of request) body += chunk;
    const payload = JSON.parse(body);
    assert.equal(payload.chat_template_kwargs.enable_thinking, false);
    const schema = payload.structured_outputs?.json ?? payload.response_format?.json_schema?.schema;
    if (testMode === 'http-error' || testMode === 'schema-compatibility' && JSON.stringify(schema).includes('maxLength')) {
      response.writeHead(400, { 'content-type': 'application/json' }).end(JSON.stringify({ error: { message: testMode === 'http-error'
        ? 'PRIVATE source at https://secret.example.invalid credential=PRIVATE'
        : 'The provided JSON schema contains features not supported by xgrammar.' } }));
      return;
    }
    const properties = schema.properties;
    let context = JSON.parse(payload.messages[1].content);
    if (context.originalRequest) context = JSON.parse(context.originalRequest);
    if (!properties.concepts && !properties.reviewedRequirementIds) context = context.context ?? context;
    const reviewing = !!properties.items?.items.properties.findings;
    if (properties.items) batches.push({ source: context.sourceKind, reviewing, outputLimit: payload.max_tokens, items: context.items.length,
      kinds: context.items.map(item => item.kind), characters: payload.messages[1].content.length,
      facts: context.sources.facts.length, sourceCharacters: context.sources.facts.reduce((n, f) => n + (f.content?.length ?? 0), 0) });
    const result = naturalDesignFixture(context, properties, testMode) ?? (properties.steps ? executionMeaningFixture(context) : properties.accepted ? { accepted: true, issues: [] } :
      reviewing ? { items: context.items.map(item => ({ id: item.id,
        findings: testMode === 'partial-git' && item.kind === 'change' ? [{ field: 'description', code: 'incorrect_change',
          instruction: '변경 전후 근거를 구분하세요', evidenceIds: [item.id] }] : [] })) } : {
      summary: '원본 코드의 입력값을 가공하고 결과를 반환합니다', recommendedType: context.available[0],
      items: context.items.map(item => ({ id: item.id, summary: '입력값을 누적하고 반환합니다', description: '원본 근거에 표시된 값을 누적하고 호출한 곳으로 반환합니다' })),
    });
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
    // Match the shipped policy example and application defaults.
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
    const result = await response.json(); assert.equal(response.status, status, `${method} ${url}: ${result.error ?? result.errorCode ?? response.status}`); return result;
  }
  async function poll(url) {
    for (let i = 0; i < 9000; i++) {
      const result = await request(url);
      if (['Completed', 'Partial', 'Failed', 'Ready'].includes(result.state)) return result;
      await delay(100);
    }
    throw new Error('Synthetic pipeline did not finish');
  }
  const views = ['flowchart', 'class', 'code-relation'].map(diagramType => ({ id: diagramType, diagramType, presetId: 'balanced' }));
  const repository = await request('/repositories', 'POST', { name: 'Synthetic C++', localPath: repositoryPath, defaultBranch: 'main', allowedRoles: ['Reviewer'] }, 201);
  if (!process.argv.includes('--execution-only')) {
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
    assert.ok(generationRequests <= 10, 'Adaptive batches share annotations across all selected views');
    assert.ok(batches.slice(beforeBatch).filter(b => !b.reviewing).every(b => b.items <= 26));
    assert.ok(requests > 0 && requests <= 20, `C++ ${selected.length} formats: ${requests} requests (shared annotation generation and review)`);
    assert.ok(batches.filter(b => b.reviewing).every(b => b.items <= 26 && b.outputLimit === 2000), 'Reviews must fit the default output budget');
    const diagnostic = await request(`/code-block-runs/${run.id}/diagnostics`);
    assert.equal(diagnostic.version, 2); assert.equal(diagnostic.execution.transportRequests, requests);
    let pages = 0;
    for (const view of run.results[0].views) for (const page of view.pages) {
      const artifact = await request(`/code-block-runs/${run.id}/groups/g/views/${view.viewId}/pages/${page.id}`);
      assert.equal(artifact.explanation.status, 'Semantic'); assert.ok(artifact.mermaidDsl); pages++;
    }
    assert.equal(pages, selected.length === 1 ? 41 : 43, 'All baseline pages must remain');
    if (selected.length === 1) {
      const base = `/code-block-runs/${run.id}/groups/g/views/${run.results[0].views[0].viewId}/pages/${run.results[0].views[0].pages[0].id}`;
      await checkVariants({ request, base, editPath: base + '/edits', summary: run.resultCounts });
      const ui = await checkVariantUi({ root, origin, fixture, run, requestCount: () => requestCount });
      checks.push({ name: 'code-ai-code-originals-edits', ui });
    }
    checks.push({ name: 'code-block', lines: 1212, functions: 40, formats: selected.length, requests, generationRequests,
      functionRequests: 0, reviewRequests: requests - generationRequests, pages });
    const next = await request(`/code-block-workspaces/${workspace.id}/runs`, 'POST', { expectedRevision: workspace.revision }, 202);
    assert.equal((await poll(`/code-block-runs/${next.id}`)).state, 'Completed');
    assert.equal(requestCount - before, requests, 'Unchanged completed results should not call the LLM again');
  }
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
      assert.equal(page.resultKind, 'semantic', 'compact responses retain page origin without explanation bodies');
      assert.ok(!page.diagram.explanation);
      const artifact = await request(`/analyses/${analysis.id}/groups/g/views/${view.viewId}/pages/${page.id}`);
      assert.equal(artifact.explanation.status, 'Semantic'); pages++;
    }
  }
  const requests = requestCount - before;
  assert.ok(requests <= 32, `Shared Git annotations exceeded the 32-request target: ${requests}`);
  assert.equal(pages, 51, 'All Git baseline pages must remain');
  const gitView = analysis.result.diagramGroups[0].views[0];
  await checkVariants({ request, base: `/analyses/${analysis.id}/groups/g/views/${gitView.viewId}/pages/${gitView.document.pages[0].id}`,
    editPath: `/analyses/${analysis.id}/groups/g/views/${gitView.viewId}/edits?pageId=${gitView.document.pages[0].id}`, summary: analysis.resultCounts });
  const gitUi = await checkGitVariantUi({ root, origin, fixture, analysis, requestCount: () => requestCount });
  checks.push({ name: 'git-ai-code-originals-edits', ui: gitUi });
  const generationRequests = batches.filter(b => b.source === 'git' && !b.reviewing).length;
  checks.push({ name: 'git', lines: 1212, functions: 40, formats: 3, requests, generationRequests, functionRequests: 0,
    reviewRequests: requests - generationRequests, pages });
  }
  // Compare one guarded state transition through all five public Git views.
  const machine = guard => `class Machine { int state; bool ready; public: bool Check(){return true;} void Save(){} bool Run(){if(${guard})state=1;if(Check()!=true)return false;Save();return true;} };`;
  await writeFile(path.join(repositoryPath, 'Machine.cpp'), machine('state==0'));
  git('add', 'Machine.cpp'); git('commit', '-m', 'Synthetic state baseline');
  const stateBase = git('rev-parse', 'HEAD');
  await writeFile(path.join(repositoryPath, 'Machine.cpp'), machine('state==0 && ready'));
  git('add', 'Machine.cpp'); git('commit', '-m', 'Synthetic state guard change');
  const stateTarget = git('rev-parse', 'HEAD');
  const stateQueued = await request('/analysis-plans', 'POST', { repositoryId: repository.id, baseRevision: stateBase, targetRevision: stateTarget, useLlmGrouping: false }, 202);
  let statePlan = await poll(`/analysis-plans/${stateQueued.id}`);
  assert.equal(statePlan.state, 'Ready');
  const fiveViews = ['flowchart', 'sequence', 'class', 'state', 'code-relation'].map(diagramType => ({ id: diagramType, diagramType, presetId: 'balanced' }));
  statePlan = await request(`/analysis-plans/${statePlan.id}/selection`, 'PUT', { expectedRevision: statePlan.revision,
    groups: [{ id: 'state-g', title: '상태 조건 변경', changeIds: statePlan.candidates.map(c => c.id), diagramType: 'state', presetId: 'balanced', views: fiveViews }] });
  const stateStarted = await request(`/analysis-plans/${statePlan.id}/generate`, 'POST', { expectedRevision: statePlan.revision }, 202);
  const stateAnalysis = await poll(`/analyses/${stateStarted.id}?includeGraph=false&summary=true`);
  assert.ok(stateAnalysis.result?.diagramGroups?.length, JSON.stringify(stateAnalysis));
  const generatedViews = stateAnalysis.result.diagramGroups[0].views;
  assert.equal(generatedViews.length, 5);
  for (const view of generatedViews) {
    assert.equal(view.generationMetadata.llmStatus, 'Semantic', JSON.stringify(view.warnings));
    const page = await request(`/analyses/${stateAnalysis.id}/groups/state-g/views/${view.viewId}/pages/${view.document.pages[0].id}`);
    assert.equal(page.ir.type, view.selection.diagramType);
    if (page.ir.type === 'state') {
      assert.equal(page.ir.edges.length, 1);
      assert.equal(page.ir.edges[0].status, 'modified');
      assert.deepEqual(page.ir.nodes.map(n => n.label.match(/\(([^)]+)\)$/)?.[1]), ['0', '1']);
      assert.ok(page.ir.edges[0].evidenceIds.length >= 4, 'both guards and writes retain evidence');
    }
    if (page.ir.type === 'sequence') {
      assert.deepEqual(page.ir.edges.filter(e => e.type === 'message').map(e => e.originalExpression), ['Check()', 'Save()']);
      assert.deepEqual(page.ir.edges.filter(e => e.type === 'return').map(e => e.returnValue), ['false', 'true']);
    }
  }
  checks.push({ name: 'git-five-formats', formats: 5, modifiedStateTransitions: 1, calls: 2, returns: ['false', 'true'] });
  testMode = 'partial-git';
  try {
    statePlan = await request(`/analysis-plans/${statePlan.id}`);
    statePlan = await request(`/analysis-plans/${statePlan.id}/selection`, 'PUT', { expectedRevision: statePlan.revision,
      groups: [{ id: 'partial-g', title: '부분 AI 보존 검증', changeIds: statePlan.candidates.map(c => c.id),
        diagramType: 'code-relation', presetId: 'balanced', views: [{ id: 'partial', diagramType: 'code-relation',
          presetId: 'balanced', refinementInstruction: '각 코드 역할을 설명하고 변경 전후 근거를 검토하세요.' }] }] });
    const partialStarted = await request(`/analysis-plans/${statePlan.id}/generate`, 'POST', { expectedRevision: statePlan.revision }, 202);
    const partial = await poll(`/analyses/${partialStarted.id}?includeGraph=false&summary=true`);
    checks.push(await checkPartialGitAi({ request, root, origin, fixture, analysis: partial, requestCount: () => requestCount }));
  } finally { testMode = 'valid'; }
  checks.push(await checkNaturalDesign(request, () => requestCount, value => { testMode = value; }, root, origin, fixture));
  const settings = await request('/llm/tests/code-diagram-settings');
  assert.equal(settings.reviewOutputTokens, 2000);
  assert.ok(!JSON.stringify(settings).includes(llmOrigin) && !('model' in settings));
  for (testMode of ['valid', 'schema-compatibility', 'http-error']) {
    const test = await request('/llm/tests/code-diagram-contract', 'POST');
    assert.equal(test.syntheticOnly, true); assert.equal(test.thinkingEnabled, false);
    assert.equal(test.success, testMode !== 'http-error', JSON.stringify(test.diagnostics));
    if (testMode === 'http-error') {
      assert.equal(test.execution.transportRequests, 1);
      assert.ok(test.diagnostics.some(d => d.httpStatus === 400 && d.serverErrorCategory === 'unknown'));
      assert.ok(!test.report.includes('PRIVATE') && !JSON.stringify(test).includes('secret.example'));
    }
    if (testMode === 'schema-compatibility') assert.ok(test.diagnostics.some(d => d.schemaRelaxed));
    await writeFile(path.join(fixture, `self-test-${testMode}.txt`), test.report);
    checks.push({ name: `self-test-${testMode}`, success: test.success, requests: test.execution.transportRequests });
  }
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
