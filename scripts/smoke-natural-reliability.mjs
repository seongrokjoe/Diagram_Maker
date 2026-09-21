// Synthetic loopback contract tests. No corporate LLM or database connection is used.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { cp, mkdir, mkdtemp, realpath, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';
import { naturalDesignFixture } from './natural-design-fixture.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
const fixture = await mkdtemp(path.join(root, 'artifacts/natural-reliability-'));
const packageRoot = process.argv[2] ? await realpath(path.resolve(root, process.argv[2])) : null;
if (packageRoot) {
  const relative = path.relative(await realpath(path.join(root, 'artifacts/stage')), packageRoot);
  assert.ok(relative && !relative.startsWith('..') && !path.isAbsolute(relative));
}
const runtime = packageRoot ?? path.join(fixture, 'api');
if (!packageRoot) {
  await cp(path.join(root, 'src/DiagramMaker.Api/bin/Release/net9.0'), runtime, { recursive: true });
  await cp(path.join(root, 'artifacts/verify-web-dist'), path.join(runtime, 'wwwroot'), { recursive: true });
}
let mode = 'valid', child, origin, output = '', browser, page;
const calls = [], checks = [];
const startedAt = Date.now();
const llm = createServer(async (request, response) => {
  try {
    assert.equal(request.url, '/v1/chat/completions');
    let body = ''; for await (const chunk of request) body += chunk;
    assert.ok(!body.includes('secret_for_mask_test'));
    const payload = JSON.parse(body);
    const context = JSON.parse(payload.messages[1].content);
    const properties = (payload.structured_outputs?.json ?? payload.response_format?.json_schema?.schema).properties;
    const kind = properties.requirements ? 'requirements' : properties.reviewedSourceRangeIds ? 'requirements-review' :
      properties.reviewedRequirementIds ? context.views ? 'final-review' : 'review' : 'design';
    calls.push({ kind, mode, at: Date.now(), requirementCount: context.requirements?.requirements?.length });
    let value;
    if (kind === 'requirements') {
      const ranges = context.sourceRanges;
      const items = ranges.map((range, index) => ({ id: `r${index + 1}`, text: `처리 ${index + 1}`, kind: 'behavior',
        origin: 'explicit', sourceQuote: '', sourceRangeIds: [range.id] }));
      value = { title: '합성 요청 설계', entities: ['장비'], requirements: items,
        scenarios: [{ id: 'scenario-1', title: '요청 처리', requirementIds: items.map(item => item.id), sourceRangeIds: ranges.map(range => range.id) }],
        questions: mode === 'question' && !JSON.stringify(ranges).includes('사용자 확인 답변') ?
          [{ id: 'q1', text: '승인을 어떻게 처리합니까?', reason: '승인 주체에 따라 호출 흐름이 달라집니다.', sourceRangeIds: [ranges[0].id], choices: ['자동 승인', '담당자 승인'] }] : [] };
      if (mode === 'unknown-id') value.requirements[0].sourceRangeIds = ['unknown'];
    } else if (kind === 'requirements-review') value = { accepted: true, reviewedSourceRangeIds: context.sourceRanges.map(r => r.id), issues: [] };
    else if (kind === 'review' || kind === 'final-review') {
      const rejected = mode === 'contradiction' || mode === 'cross-view' && kind === 'final-review';
      value = { accepted: !rejected, reviewedRequirementIds: context.requirements.requirements.map(r => r.id),
        issues: rejected ? ['조건 반전을 수정하세요.'] : [] };
    }
    else {
      value = naturalDesignFixture(context, properties, 'valid');
      const ids = context.requirements.requirements.map(r => r.id);
      for (const item of [...value.nodes, ...value.edges]) item.requirementIds = ids;
    }
    await delay(mode === 'question' ? 100 : 1);
    response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify({
      choices: [{ message: { content: mode === 'null-json' ? 'null' : JSON.stringify(value) },
        finish_reason: mode === 'split' && kind === 'design' && context.requirements.requirements.length > 1 ? 'length' : 'stop' }],
      usage: { prompt_tokens: 100, completion_tokens: 100, total_tokens: 200 },
    }));
  } catch (error) { output += `fixture: ${error.stack}\n`; response.writeHead(500).end(); }
});
const policy = path.join(fixture, 'llm.json'), network = path.join(fixture, 'network.json');
async function start() {
  let startup = '';
  child = spawn(packageRoot ? path.join(runtime, 'DiagramMaker.Api.exe') : 'dotnet',
    [...(packageRoot ? [] : [path.join(runtime, 'DiagramMaker.Api.dll')]), '--urls', 'http://127.0.0.1:0'], {
      cwd: runtime, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development', Storage__Provider: 'LocalFile',
        Storage__LocalFilePath: path.join(fixture, 'data/store.json'), Security__TrustReverseProxyHeaders: 'true',
        DIAGRAMMAKER_LLM_POLICY_PATH: policy, DIAGRAMMAKER_NETWORK_POLICY_PATH: network, CodexTest__Enabled: 'false' },
    });
  child.stdout.on('data', data => { startup += data; output += data; });
  child.stderr.on('data', data => { startup += data; output += data; });
  for (let i = 0; i < 150; i++) {
    origin = startup.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
    if (origin) return;
    assert.equal(child.exitCode, null, startup); await delay(100);
  }
  throw new Error(startup);
}
async function stop() {
  if (child?.exitCode === null) { const closed = once(child, 'close'); child.kill(); await closed; }
}
async function request(url, method = 'GET', body, expected = 200, user) {
  const response = await fetch(origin + '/api/v1' + url, { method,
    headers: { 'content-type': 'application/json', ...(user ? { 'X-Remote-User': user, 'X-Remote-Roles': 'Reviewer' } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
  assert.equal(response.status, expected, `${method} ${url}: ${await (response.status === expected ? Promise.resolve('') : response.text())}`);
  if (expected === 403) return;
  return response.headers.get('content-type')?.includes('json') ? response.json() : response.text();
}
async function poll(id) {
  for (let i = 0; i < 300; i++) {
    const run = await request(`/natural-diagram-runs/${id}`);
    if (!['Queued', 'Generating'].includes(run.state)) return run;
    await delay(100);
  }
  throw new Error('Run did not finish');
}
async function create(prompt = '장비에 요청한다. 결과를 확인한다.') {
  return request('/natural-diagram-runs', 'POST', { request: { prompt, diagramType: 'flowchart' } }, 202);
}
try {
  llm.listen(0, '127.0.0.1'); await once(llm, 'listening');
  const llmOrigin = `http://127.0.0.1:${llm.address().port}`;
  await writeFile(policy, JSON.stringify({ Llm: { Enabled: true, Endpoint: llmOrigin + '/v1/chat/completions', AllowedOrigin: llmOrigin,
    UseServerTokenization: false, MaxTransientRetries: 0, DiagramOutputTokens: 8000, ReviewOutputTokens: 2000 } }));
  await writeFile(network, JSON.stringify({ LocalRoots: [root], LlmOrigins: [llmOrigin], LlmAddressRanges: ['127.0.0.1/32'], Databases: [] }));
  await start();
  let run = await poll((await create('token=secret_for_mask_test 요청한다. 결과를 확인한다.')).id);
  assert.equal(run.state, 'Completed', run.errorMessage);
  assert.equal(run.checkpoints, null);
  assert.equal(run.requirements.sourceRanges.length, 2);
  const original = await request(`/natural-diagrams/${run.resultDiagramId}`);
  assert.equal(original.generatorVersion, 'natural-v7');
  await request(`/natural-diagram-runs/${run.id}`, 'GET', undefined, 403, 'other-owner');
  await request(`/natural-diagram-runs/${run.id}/diagnostics`, 'GET', undefined, 403, 'other-owner');
  await request(`/natural-diagram-runs/${run.id}/answers`, 'POST', {}, 403, 'other-owner');
  const diagnostic = await request(`/natural-diagram-runs/${run.id}/diagnostics`);
  assert.ok(!diagnostic.includes('secret_for_mask_test') && !diagnostic.includes(llmOrigin));
  checks.push({ name: 'generation-evidence-masking-acl-diagnostics', requests: calls.length });

  for (const failureMode of ['null-json', 'unknown-id']) {
    mode = failureMode; const before = calls.length;
    const failed = await poll((await create()).id);
    assert.equal(failed.state, 'Failed');
    assert.equal(calls.length - before, 3);
    await request(`/natural-diagram-runs/${failed.id}/resume`, 'POST', { expectedRevision: failed.revision }, 202);
    assert.equal((await poll(failed.id)).state, 'Failed');
    assert.equal(calls.length - before, 3, 'resume cannot reset exhausted repairs');
    checks.push({ name: failureMode, requests: calls.length - before });
  }
  mode = 'contradiction';
  const failed = await poll((await request('/natural-diagram-runs', 'POST', {
    request: original.request, sourceDiagramId: original.id, regenerateViewIds: [original.views[0].viewId],
  }, 202)).id);
  assert.equal(failed.state, 'Partial');
  assert.equal(failed.views[0].pages[0].diagram.id, original.views[0].pages[0].diagram.id);
  checks.push({ name: 'rejected-design-preserves-last-success' });

  mode = 'valid';
  const allFormats = await poll((await request('/natural-diagram-runs', 'POST', { request: {
    prompt: '장비에 요청한다. 결과를 확인한다.', diagramType: 'flowchart',
    views: ['flowchart', 'sequence', 'state', 'class'].map((diagramType, index) => ({ id: `format-${index}`, diagramType, presetId: 'balanced' })),
  } }, 202)).id);
  assert.equal(allFormats.state, 'Completed', allFormats.errorMessage);
  assert.equal(allFormats.views.length, 4);
  assert.ok(allFormats.views.every(view => view.pages.every(page => page.designQuality.reviewedRequirementIds.length === 2)));
  checks.push({ name: 'four-formats-common-requirement-coverage' });

  mode = 'cross-view';
  const crossView = await poll((await create()).id);
  assert.equal(crossView.state, 'Partial');
  assert.equal(crossView.views[0].errorCode, 'NATURAL_CROSS_VIEW_REVIEW');
  assert.ok(crossView.views[0].pages.every(page => page.diagram && page.state === 'Completed'));
  checks.push({ name: 'cross-view-rejection-retains-reviewed-pages' });

  mode = 'split';
  const split = await poll((await create()).id);
  assert.equal(split.state, 'Completed', split.errorMessage);
  const parts = split.views[0].pages;
  assert.equal(parts.length, 2);
  assert.equal(new Set(parts.flatMap(part => part.designQuality.reviewedRequirementIds)).size, 2);
  const splitStart = calls.length;
  const regenerated = await poll((await request('/natural-diagram-runs', 'POST', {
    request: split.request, sourceDiagramId: split.resultDiagramId,
    regenerateViewIds: [split.views[0].viewId], regeneratePageIds: [parts[0].id],
  }, 202)).id);
  assert.equal(regenerated.state, 'Completed', regenerated.errorMessage);
  assert.equal(regenerated.views[0].pages[1].diagram.id, parts[1].diagram.id);
  assert.notEqual(regenerated.views[0].pages[0].diagram.id, parts[0].diagram.id);
  assert.equal(calls.slice(splitStart).filter(call => call.kind === 'design').length, 1);
  checks.push({ name: 'truncated-design-split-and-selected-detail-regeneration', requests: calls.length - splitStart });

  mode = 'question';
  run = await poll((await create()).id);
  assert.equal(run.state, 'NeedsClarification');
  const beforeRestart = calls.length;
  await stop(); await start();
  run = await request(`/natural-diagram-runs/${run.id}`);
  assert.equal(run.state, 'NeedsClarification');
  assert.equal(run.questions.length, 1);
  assert.equal(calls.length, beforeRestart);
  const answers = [{ questionId: 'q1', text: '담당자 승인' }];
  await request(`/natural-diagram-runs/${run.id}/answers`, 'POST', { expectedRevision: run.revision - 1, questionVersion: run.questionVersion, answers }, 409);
  await request(`/natural-diagram-runs/${run.id}/answers`, 'POST', { expectedRevision: run.revision, questionVersion: 0, answers }, 409);
  await request(`/natural-diagram-runs/${run.id}/answers`, 'POST', { expectedRevision: run.revision, questionVersion: run.questionVersion, answers: [] }, 400);

  const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
  await page.goto(origin);
  await page.getByRole('region', { name: '자연어 요청 확인' }).waitFor();
  await page.locator('.semantic-progress-current').filter({ hasText: '답변 대기' }).waitFor();
  await page.screenshot({ path: path.join(fixture, 'questions-1440.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: path.join(fixture, 'questions-390.png'), fullPage: true });
  await page.getByRole('button', { name: '담당자 승인', exact: true }).click();
  let rejectedPoll = false;
  await page.route(`**/api/v1/natural-diagram-runs/${run.id}`, route => {
    if (!rejectedPoll) { rejectedPoll = true; return route.abort(); }
    return route.continue();
  });
  await page.getByRole('button', { name: '답변 저장 후 생성', exact: true }).click();
  await page.locator('.natural-run-status').filter({ hasText: 'Completed' }).waitFor({ timeout: 30000 });
  await page.locator('.preview .diagram-canvas svg').waitFor();
  assert.ok(rejectedPoll);
  assert.equal(await page.getByText('실행 상태를 불러오지 못했습니다. 자동으로 다시 확인합니다.', { exact: true }).count(), 0);
  assert.equal(await page.getByText('Failed to fetch', { exact: true }).count(), 0);
  assert.deepEqual(errors, []);
  await page.screenshot({ path: path.join(fixture, 'completed-390.png'), fullPage: true });
  run = await request(`/natural-diagram-runs/${run.id}`);
  assert.equal(run.answerVersion, 1);
  assert.equal(run.state, 'Completed');
  await request(`/natural-diagram-runs/${run.id}/answers`, 'POST', { expectedRevision: run.revision, questionVersion: run.questionVersion, answers }, 409);
  checks.push({ name: 'questions-restart-stale-answers-poll-recovery-mobile', requests: calls.length - beforeRestart });
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', checks, calls,
    elapsedMilliseconds: Date.now() - startedAt, realModel: false, postgres: false }, null, 2));
  console.log(`Natural reliability smoke passed: ${fixture}`);
} catch (error) {
  await page?.screenshot({ path: path.join(fixture, 'failure.png'), fullPage: true }).catch(() => {});
  await writeFile(path.join(fixture, 'failure.txt'), String(error.stack ?? error));
  throw error;
} finally {
  await browser?.close(); await stop(); llm.close();
  await writeFile(path.join(fixture, 'server.log'), output);
}
