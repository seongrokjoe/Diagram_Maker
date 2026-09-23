// Synthetic loopback contract tests. No corporate LLM or database connection is used.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { cp, mkdir, mkdtemp, readFile, realpath, writeFile } from 'node:fs/promises';
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
    let context = JSON.parse(payload.messages[1].content);
    if (context.originalRequest) context = JSON.parse(context.originalRequest);
    const properties = (payload.structured_outputs?.json ?? payload.response_format?.json_schema?.schema).properties;
    const kind = properties.requirements ? 'requirements' : properties.reviewedSourceRangeIds ? 'requirements-review' :
      properties.reviewedRequirementIds ? context.views ? 'final-review' : 'review' : properties.nodes ? 'design' : 'scenario-mapping';
    calls.push({ kind, mode, at: Date.now(), target: context.target?.id, requirementCount: context.requirements?.requirements?.length });
    const attempt = calls.filter(call => call.mode === mode && call.kind === kind).length;
    if (mode === 'grammar-once' && calls.filter(call => call.mode === mode).length === 1) {
      response.writeHead(400, { 'content-type': 'application/json' }).end(JSON.stringify({ error: { message: 'The provided JSON schema contains features not supported by xgrammar.' } })); return;
    }
    if (mode === 'review-unavailable' && kind === 'requirements-review' && attempt === 1) {
      response.writeHead(503, { 'content-type': 'application/json' }).end(JSON.stringify({ error: { message: 'synthetic unavailable' } })); return;
    }
    let value;
    if (kind === 'requirements') {
      const ranges = context.sourceRanges;
      const items = ranges.map((range, index) => ({ id: `r${index + 1}`, text: `처리 ${index + 1}`, kind: 'behavior',
        sourceRangeIds: [range.id] }));
      value = { title: '합성 요청 설계', entities: ['장비'], requirements: items,
        scenarios: [{ id: 'scenario-1', title: '요청 처리', requirementIds: items.map(item => item.id) }],
        questions: mode === 'question' && !JSON.stringify(ranges).includes('사용자 확인 답변') ?
          [{ id: 'q1', text: '승인을 어떻게 처리합니까?', reason: '승인 주체에 따라 호출 흐름이 달라집니다.', sourceRangeIds: [ranges[0].id], choices: ['자동 승인', '담당자 승인'] }] : [] };
      if (mode === 'unknown-id') value.requirements[0].sourceRangeIds = ['unknown'];
      if (mode === 'scenario-invalid') value.scenarios[0].requirementIds = ['unknown'];
      if (mode === 'all-assumptions-once' && attempt === 1)
        for (const item of value.requirements) { item.origin = 'assumption'; item.sourceRangeIds = []; }
      if (mode === 'review-contract-then-semantic') value.requirements[0].text = attempt === 1 ? '잘못된 조건' : '수정된 조건';
    } else if (kind === 'requirements-review') value = { reviewedSourceRangeIds: context.sourceRanges.map(r => r.id), issues: [] };
    else if (kind === 'review' || kind === 'final-review') {
      const rejected = mode === 'contradiction' || mode === 'cross-view' && kind === 'final-review';
      value = { reviewedRequirementIds: context.requirements.requirements.map(r => r.id),
        issues: rejected ? [{ itemId: context.views?.[0]?.pages?.[0]?.id ?? context.requirements.requirements[0].id,
          field: 'guard', code: 'NaturalConditionChanged', instruction: '조건 반전을 수정하세요.', evidenceIds: [context.requirements.requirements[0].id],
          sourceQuote: context.requirements.sourceRanges[0].text, relatedElementIds: [] }] : [] };
    }
    else if (kind === 'scenario-mapping') value = { scenarios: [{ id: 'scenario-1', title: '요청 처리',
      requirementIds: context.requirements.requirements.map(r => r.id) }], questions: [] };
    else {
      value = naturalDesignFixture(context, properties, 'valid');
    }
    if (kind === 'requirements-review' && mode === 'review-missing-field' && attempt === 1) delete value.reviewedSourceRangeIds;
    if (kind === 'requirements-review' && mode === 'review-all-missing') value.reviewedSourceRangeIds = [];
    if (kind === 'review' && mode === 'design-review-once' && attempt === 1) delete value.reviewedRequirementIds;
    if (kind === 'requirements-review' && (mode === 'semantic-no-progress' || mode === 'review-contract-then-semantic' && attempt === 3))
      value.issues = [{ code: 'NaturalConditionChanged', field: 'text', requirementIds: ['r1'],
        sourceRangeIds: [context.sourceRanges[0].id], instruction: '원문의 조건을 보존하세요.' }];
    if (kind === 'requirements-review' && mode === 'review-contract-then-semantic' && attempt === 1) delete value.reviewedSourceRangeIds;
    if (kind === 'requirements-review' && mode === 'review-contract-then-semantic' && attempt === 2) value.reviewedSourceRangeIds = null;
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
  assert.equal(original.generatorVersion, 'natural-v12');
  await request(`/natural-diagram-runs/${run.id}`, 'GET', undefined, 403, 'other-owner');
  await request(`/natural-diagram-runs/${run.id}/diagnostics`, 'GET', undefined, 403, 'other-owner');
  await request(`/natural-diagram-runs/${run.id}/answers`, 'POST', {}, 403, 'other-owner');
  const diagnostic = await request(`/natural-diagram-runs/${run.id}/diagnostics`);
  assert.ok(!diagnostic.includes('secret_for_mask_test') && !diagnostic.includes(llmOrigin));
  checks.push({ name: 'generation-evidence-masking-acl-diagnostics', requests: calls.length });

  for (const [testMode, stage, expected] of [['review-missing-field', 'requirements-review', 'Completed'],
    ['review-all-missing', 'requirements-review', 'Failed'], ['scenario-invalid', 'requirements', 'Completed'],
    ['design-review-once', 'review', 'Completed'], ['grammar-once', 'requirements', 'Completed'],
    ['all-assumptions-once', 'requirements', 'Completed'], ['review-contract-then-semantic', 'requirements-review', 'Completed'],
    ['semantic-no-progress', 'requirements-review', 'Failed']]) {
    mode = testMode; const before = calls.length;
    const checked = await poll((await create()).id);
    assert.equal(checked.state, expected, checked.errorMessage);
    const rows = (await request(`/natural-diagram-runs/${checked.id}/diagnostics?format=json`)).diagnostics;
    assert.ok(rows.some(row => row.errorCode || row.schemaRelaxed), `${testMode}: diagnostic evidence`);
    assert.ok(!rows.some(row => row.recoveryState === 'Retrying'));
    const sent = calls.slice(before);
    if (testMode === 'review-missing-field') {
      assert.equal(sent.filter(call => call.kind === 'requirements').length, 1);
      assert.equal(sent.filter(call => call.kind === stage).length, 2);
      assert.ok(rows.some(row => row.validationCode === 'NaturalFieldMissing' && row.recoveryState === 'Recovered'));
    }
    if (testMode === 'review-all-missing') {
      assert.equal(checked.errorCode, 'NATURAL_REQUIREMENTS_REVIEW_INVALID');
      assert.equal(sent.filter(call => call.kind === 'requirements').length, 1);
      assert.equal(sent.filter(call => call.kind === stage).length, 2);
    }
    if (testMode === 'scenario-invalid') {
      assert.equal(sent.filter(call => call.kind === 'requirements').length, 1);
      assert.equal(sent.filter(call => call.kind === 'scenario-mapping').length, 1);
    }
    if (testMode === 'design-review-once') {
      const generations = sent.filter(call => call.kind === 'design');
      assert.equal(generations.length, 1, 'review format repair does not regenerate the scenario');
      assert.equal(generations[0].requirementCount, 2);
    }
    if (testMode === 'grammar-once') assert.ok(rows.some(row => row.schemaRelaxed));
    if (testMode === 'all-assumptions-once') {
      assert.equal(sent.filter(call => call.kind === 'requirements').length, 2);
      assert.ok(rows.some(row => row.validationCode === 'NaturalFieldUnexpected' && row.recoveryState === 'Recovered'));
    }
    if (testMode === 'review-contract-then-semantic') {
      assert.equal(sent.filter(call => call.kind === 'requirements').length, 2);
      assert.equal(sent.filter(call => call.kind === 'requirements-review').length, 4);
      assert.ok(rows.some(row => row.validationCode === 'NaturalConditionChanged' && row.recoveryState === 'Recovered'));
      assert.ok(rows.some(row => row.extraction?.replaced === 1));
    }
    if (testMode === 'semantic-no-progress') {
      assert.equal(sent.filter(call => call.kind === 'requirements').length, 2);
      assert.equal(sent.filter(call => call.kind === 'requirements-review').length, 1);
      assert.ok(rows.some(row => row.validationCode === 'NaturalRepairNoProgress' && row.kind === 'Terminal'));
      assert.equal(checked.resumeAllowed, false);
    }
    const text = await request(`/natural-diagram-runs/${checked.id}/diagnostics`);
    assert.ok(!/category: unknown|action: unknown|recovery: unknown/.test(text));
    checks.push({ name: testMode, requests: sent.length });
  }
  mode = 'review-unavailable';
  const interrupted = await poll((await create()).id);
  assert.equal(interrupted.state, 'Failed'); assert.equal(interrupted.resumeAllowed, true);
  await request(`/natural-diagram-runs/${interrupted.id}/resume`, 'POST', { expectedRevision: interrupted.revision }, 202);
  assert.equal((await poll(interrupted.id)).state, 'Completed');
  assert.equal(calls.filter(call => call.mode === mode && call.kind === 'requirements').length, 1);
  checks.push({ name: 'review-http-interruption-resumes-without-reextraction' });

  mode = 'valid';
  await request('/llm/tests/natural-diagram-contract', 'POST', undefined, 403, 'other-owner');
  const selfTest = await request('/llm/tests/natural-diagram-contract', 'POST');
  assert.equal(selfTest.success, true, selfTest.report);
  assert.equal(selfTest.cases.length, 3);
  assert.ok(selfTest.cases.every(item => item.reviewedPages > 0));
  assert.ok(!selfTest.report.includes(llmOrigin));
  assert.equal(selfTest.summary.split('\n').length, 4);
  assert.match(selfTest.summary, /natural-v12; protocol: natural-design-v8/);
  assert.ok(!selfTest.summary.includes(llmOrigin));
  assert.ok(selfTest.cases.every(item => item.extraction.extractionAttempts >= 1));
  if (packageRoot) {
    const manifest = JSON.parse((await readFile(path.join(packageRoot, 'manifest.json'), 'utf8')).replace(/^\uFEFF/, ''));
    assert.ok(selfTest.report.includes(`Build: ${manifest.version}`), 'The report identifies the shipped package version');
  }
  await writeFile(path.join(fixture, 'natural-self-test.txt'), selfTest.report);
  checks.push({ name: 'natural-self-test-real-pipeline-five-views-admin-acl' });
  await request('/llm/tests/natural-diagram-contract?caseId=short-approval&diagramType=class', 'POST', undefined, 400);
  const streamedResponse = await fetch(origin + '/api/v1/llm/tests/natural-diagram-contract?format=ndjson&caseId=table-interlock&diagramType=state',
    { method: 'POST', signal: AbortSignal.timeout(30000) });
  assert.equal(streamedResponse.status, 200);
  assert.match(streamedResponse.headers.get('content-type') ?? '', /application\/x-ndjson/);
  const streamedEvents = (await streamedResponse.text()).trim().split('\n').map(line => JSON.parse(line));
  assert.ok(streamedEvents.some(event => event.type === 'progress' && event.caseId === 'table-interlock'));
  assert.ok(streamedEvents.some(event => event.type === 'case-completed' && event.case.state === 'Completed'));
  const streamedResult = streamedEvents.at(-1);
  assert.equal(streamedResult.type, 'result');
  assert.equal(streamedResult.result.success, true);
  assert.deepEqual(streamedResult.result.cases.map(item => [item.id, item.types]), [['table-interlock', ['state']]]);
  assert.ok(!JSON.stringify(streamedEvents).includes(llmOrigin));
  await writeFile(path.join(fixture, 'natural-self-test-stream.json'), JSON.stringify(streamedEvents, null, 2));
  checks.push({ name: 'natural-self-test-selected-ndjson-progress-result-and-invalid-selection' });
  mode = 'semantic-no-progress';
  const failedSelfTest = await request('/llm/tests/natural-diagram-contract', 'POST');
  assert.equal(failedSelfTest.success, false);
  assert.ok(failedSelfTest.cases.every(item => item.lastFailureCode === 'NaturalRepairNoProgress'));
  assert.ok(failedSelfTest.cases.every(item => item.extraction.extractionAttempts === 2 && item.extraction.reviewAttempts === 1));
  assert.ok(failedSelfTest.cases.every(item => item.issueCounts.NaturalConditionChanged > 0));
  assert.ok(!failedSelfTest.summary.includes('원문의 조건을 보존하세요'));
  await writeFile(path.join(fixture, 'natural-self-test-failed-summary.txt'), failedSelfTest.summary);
  checks.push({ name: 'self-test-failure-summary-isolates-cases-and-keeps-specific-reasons' });

  for (const failureMode of ['null-json', 'unknown-id']) {
    mode = failureMode; const before = calls.length;
    const failed = await poll((await create()).id);
    assert.equal(failed.state, 'Failed');
    assert.equal(calls.length - before, 2);
    assert.equal(failed.resumeAllowed, false);
    await request(`/natural-diagram-runs/${failed.id}/resume`, 'POST', { expectedRevision: failed.revision }, 409);
    assert.equal((await poll(failed.id)).state, 'Failed');
    assert.equal(calls.length - before, 2, 'resume cannot reset exhausted repairs');
    checks.push({ name: failureMode, requests: calls.length - before });
  }
  mode = 'contradiction';
  const failed = await poll((await request('/natural-diagram-runs', 'POST', {
    request: original.request, sourceDiagramId: original.id, regenerateViewIds: [original.views[0].viewId],
  }, 202)).id);
  assert.equal(failed.state, 'Partial');
  assert.equal(failed.views[0].pages[0].diagram.id, original.views[0].pages[0].diagram.id);
  const rejectionDiagnostics = (await request(`/natural-diagram-runs/${failed.id}/diagnostics?format=json`)).diagnostics;
  const condition = rejectionDiagnostics.find(item => item.validationCode === 'NaturalConditionChanged');
  assert.ok(condition, 'condition rejection has diagnostic metadata');
  const comparison = await request(`/natural-diagram-runs/${failed.id}/diagnostics/${condition.id}/comparison`);
  assert.equal(comparison.code, 'NaturalConditionChanged');
  assert.ok(comparison.sourceExcerpts.length > 0);
  assert.ok(comparison.observed.length > 0);
  await request(`/natural-diagram-runs/${failed.id}/diagnostics/${condition.id}/comparison`, 'GET', undefined, 403, 'other-owner');
  const metadataOnly = await request(`/natural-diagram-runs/${failed.id}/diagnostics`);
  assert.ok(!metadataOnly.includes(comparison.sourceExcerpts[0].text));  checks.push({ name: 'rejected-design-preserves-last-success' });

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
  const crossView = await poll((await request('/natural-diagram-runs', 'POST', { request: allFormats.request }, 202)).id);
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
  mode = 'unknown-id';
  const exhausted = await poll((await create()).id);
  assert.equal(exhausted.resumeAllowed, false);
  await page.reload();
  await page.locator('.natural-run-history summary').click();
  await page.locator('.natural-run-history button').first().click();
  await page.locator('.natural-run-status').filter({ hasText: 'Failed' }).waitFor();
  await page.getByText('자동 보정을 완료하지 못했습니다. 아래 진단에서', { exact: false }).waitFor();
  const diagnosticsPanel = page.getByLabel('요청 오류 진단', { exact: true });
  await diagnosticsPanel.locator('tbody tr').first().waitFor();
  const exported = await request(`/natural-diagram-runs/${exhausted.id}/diagnostics?format=json`);
  assert.equal(await diagnosticsPanel.locator('tbody tr').count(), exported.diagnostics.filter(d => d.errorCode).length);
  assert.equal(await page.getByRole('button', { name: '저장 지점에서 이어하기', exact: true }).count(), 0);
  await page.screenshot({ path: path.join(fixture, 'exhausted-390.png'), fullPage: true });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: path.join(fixture, 'exhausted-1440.png'), fullPage: true });
  mode = 'valid';
  await page.getByRole('button', { name: 'LLM 점검', exact: true }).click();
  const testPanel = page.locator('.natural-diagram-test');
  await testPanel.getByLabel('검사 문장').selectOption('table-interlock');
  await testPanel.getByLabel('검사 형식').selectOption('state');
  await page.getByRole('button', { name: '자연어 생성 검사', exact: true }).click();
  await testPanel.getByText('검사 통과', { exact: true }).waitFor({ timeout: 30000 });
  assert.match(await testPanel.getByLabel('전달용 검사 요약').inputValue(), /table-interlock: Completed/);
  await testPanel.getByText('표·공통 조건 (상태도)', { exact: false }).waitFor();
  await testPanel.getByRole('button', { name: '검사 요약 복사', exact: true }).click();
  await testPanel.getByText(/검사 요약을 복사했습니다|요약 내용을 선택해 복사/).waitFor();
  const download = page.waitForEvent('download');
  await testPanel.getByRole('button', { name: '자연어 검사 보고서 다운로드', exact: true }).click();
  await (await download).saveAs(path.join(fixture, 'natural-self-test-ui.txt'));
  await page.screenshot({ path: path.join(fixture, 'natural-self-test-1440.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  const summaryBounds = await testPanel.getByLabel('전달용 검사 요약').boundingBox();
  assert.ok(summaryBounds.width > 240 && summaryBounds.x + summaryBounds.width <= 390);
  await page.screenshot({ path: path.join(fixture, 'natural-self-test-390.png'), fullPage: true });
  assert.deepEqual(errors, []);
  checks.push({ name: 'exhausted-full-diagnostics-and-synthetic-test-ui-download' });
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
