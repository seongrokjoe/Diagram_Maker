// Optional Windows/Edge UI regression check using only the local fake CLI.
// Run verify.ps1 first, then install Playwright under artifacts/ui-check (see README).
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { cp, mkdir, mkdtemp, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { checkAnalysisResultUi } from './analysis-result-ui-checks.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
await mkdir(path.join(root, 'artifacts'), { recursive: true });
const run = await mkdtemp(path.join(root, 'artifacts/ui-smoke-'));
const runtime = path.join(run, 'artifacts/codex-test');
execFileSync(process.execPath, [path.join(root, 'tools/codex-test/prepare-samples.mjs'), runtime], { windowsHide: true });
await cp(path.join(root, 'artifacts/verify-web-dist'), path.join(runtime, 'web'), { recursive: true });
const fake = path.join(root, 'tests/DiagramMaker.FakeVllm/bin/Release/net9.0/DiagramMaker.FakeVllm.exe');
let output = '';
const server = spawn('dotnet', [path.join(root, 'src/DiagramMaker.Api/bin/Release/net9.0/DiagramMaker.Api.dll'), '--urls', 'http://127.0.0.1:0'], {
  cwd: path.join(root, 'src/DiagramMaker.Api'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    CodexTest__Enabled: 'true', CodexTest__RuntimeRoot: runtime, CodexTest__AssetsRoot: path.join(root, 'tools/codex-test'),
    CodexTest__ExecutablePath: fake, CodexTest__Model: '', CodexTest__RequestTimeoutSeconds: '20',
    DIAGRAMMAKER_LLM_POLICY_PATH: path.join(run, 'must-not-be-read.json'),
    GitWorker__ScriptPath: path.join(root, 'tools/git-worker/index.mjs') },
});
server.stdout.on('data', data => { output += data; });
server.stderr.on('data', data => { output += data; });
let origin, browser, page;
const checkpoints = [];
async function checkpoint(name) {
  checkpoints.push(name);
  await writeFile(path.join(run, 'result.json'), JSON.stringify({ status: 'running', checkpoints }, null, 2));
}
try {
  await checkpoint('fixture startup');
  for (let i = 0; i < 100; i++) {
    origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
    if (origin) break;
    if (server.exitCode !== null) throw new Error(`UI fixture failed to start: ${output}`);
    await delay(100);
  }
  assert.ok(origin, `UI fixture must start: ${output}`);
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(origin);
  await page.getByRole('combobox', { name: /^테스트 샘플/ }).selectOption('natural-orders');
  const responseEvent = page.waitForResponse(response => response.url().endsWith('/natural-orders/generate'));
  await page.getByRole('button', { name: 'Codex로 다이어그램 그리기', exact: true }).click();
  const response = await responseEvent;
  assert.equal(response.status(), 200);
  const { record } = await response.json();
  assert.equal(record.views.length, 4);
  await checkpoint('natural: four views generated');
  const status = async () => (await (await page.request.get(`${origin}/api/v1/runtime-info`)).json()).codex.calls;
  const calls = await status();
  const names = { flowchart: '흐름 / 영향도', sequence: '호출 시퀀스', class: '클래스 관계', state: '상태 전이' };
  const editor = page.locator('.structured-diagram-editor');
  const svg = editor.locator('.diagram-canvas svg');
  const nodes = editor.locator('[data-ir-kind="node"]');
  for (const view of record.views) {
    const type = view.selection.diagramType;
    await checkpoint(`${type}: started`);
    await page.locator('.analysis-output .diagram-type-tabs').getByRole('button', { name: `${names[type]} · Completed`, exact: true }).click();
    await page.waitForFunction(dsl => document.querySelector('.structured-diagram-editor details pre')?.textContent === dsl,
      view.diagram.mermaidDsl);
    await svg.waitFor();
    const measurement = await page.waitForFunction(() =>
      document.querySelector('.structured-diagram-editor .diagram-canvas svg')?.getBoundingClientRect().width || false);
    const beforeWidth = await measurement.jsonValue();
    await editor.getByRole('button', { name: '확대', exact: true }).click();
    await editor.getByText('110%', { exact: true }).waitFor();
    await page.waitForFunction(width => {
      const rendered = document.querySelector('.structured-diagram-editor .diagram-canvas svg');
      return rendered && rendered.getBoundingClientRect().width > width * 1.09;
    }, beforeWidth);
    assert.ok(await svg.evaluate((element, width) => element.getBoundingClientRect().width > width * 1.09, beforeWidth),
      `${type}: rendered SVG must scale`);
    await editor.getByRole('button', { name: '100%로 초기화' }).click();
    await svg.hover();
    await page.mouse.wheel(0, -100);
    await editor.getByText('110%', { exact: true }).waitFor();
    await page.waitForFunction(width => {
      const rendered = document.querySelector('.structured-diagram-editor .diagram-canvas svg');
      return rendered && rendered.getBoundingClientRect().width > width * 1.09;
    }, beforeWidth);
    await editor.getByRole('button', { name: '100%로 초기화' }).click();
    await checkpoint(`${type}: button and wheel zoom`);
    await editor.getByRole('button', { name: '구조 편집', exact: true }).click();
    await nodes.first().waitFor();
    await writeFile(path.join(run, `${type}-original.svg`), await svg.evaluate(element => element.outerHTML));
    const ids = await nodes.evaluateAll(elements => [...new Set(elements.map(element => element.getAttribute('data-ir-id')))]);
    assert.equal(ids.length, view.diagram.ir.nodes.length, `${type}: every node must be selectable`);
    const edges = editor.locator('[data-ir-kind="edge"]');
    const edgeIds = await edges.evaluateAll(elements => [...new Set(elements.map(element => element.getAttribute('data-ir-id')))]);
    assert.deepEqual(edgeIds.sort(), view.diagram.ir.edges.map(edge => edge.id).sort(), `${type}: every relation must be selectable`);
    const edgeId = edgeIds[0];
    assert.ok(edgeId, `${type}: fixture must contain a relation`);
    const mappedEdge = editor.locator(`[data-ir-kind="edge"][data-ir-id="${edgeId}"]`);
    await editor.locator(`text[data-ir-kind="edge"][data-ir-id="${edgeId}"], g.edgeLabel[data-ir-kind="edge"][data-ir-id="${edgeId}"]`).first().click();
    await page.keyboard.press('Delete');
    await mappedEdge.first().waitFor({ state: 'detached' });
    assert.equal(await nodes.evaluateAll(elements => new Set(elements.map(element => element.getAttribute('data-ir-id'))).size),
      ids.length, `${type}: relation deletion must retain every node`);
    await editor.getByRole('button', { name: '실행 취소', exact: true }).click();
    await mappedEdge.first().waitFor({ state: 'attached' });
    await checkpoint(`${type}: relation deletion and undo`);
    // Click the participant's text, which is a sibling of its named rectangle in Mermaid.
    const chosen = type === 'sequence' ? editor.locator('text.actor[data-ir-kind="node"]').first() : nodes.first();
    const removedId = await chosen.getAttribute('data-ir-id');
    assert.ok(removedId);
    await chosen.click();
    await page.keyboard.press('Delete');
    const mapped = editor.locator(`[data-ir-kind="node"][data-ir-id="${removedId}"]`);
    await mapped.first().waitFor({ state: 'detached' });
    await editor.getByRole('button', { name: '실행 취소', exact: true }).click();
    await mapped.first().waitFor();
    await editor.getByRole('button', { name: '다시 실행', exact: true }).click();
    await mapped.first().waitFor({ state: 'detached' });
    const savedEvent = page.waitForResponse(value => value.url().includes('/edits') && value.request().method() === 'POST');
    await editor.getByRole('button', { name: '새 리비전 저장', exact: true }).click();
    const savedResponse = await savedEvent;
    assert.equal(savedResponse.status(), 201);
    const saved = await savedResponse.json();
    assert.equal(saved.diagram.ir.nodes.length, ids.length - 1);
    assert.ok(saved.diagram.ir.edges.every(edge => edge.sourceId !== removedId && edge.targetId !== removedId));
    await editor.getByText('표시 리비전: v2 · 구조 편집', { exact: true }).waitFor();
    await page.reload();
    await page.getByRole('combobox', { name: /^자연어 예제 이력/ }).selectOption(record.id);
    await page.locator('.analysis-output .diagram-type-tabs').getByRole('button', { name: `${names[type]} · Completed`, exact: true }).click();
    await editor.getByText('표시 리비전: v2 · 구조 편집', { exact: true }).waitFor();
    await svg.waitFor();
    await mapped.first().waitFor({ state: 'detached' });
    assert.equal(await mapped.count(), 0, `${type}: history must restore the edit`);
    for (const format of ['SVG', 'PNG']) {
      const downloaded = page.waitForEvent('download');
      await editor.getByRole('button', { name: `${format} 다운로드`, exact: true }).click();
      const file = path.join(run, `${type}.${format.toLowerCase()}`);
      await (await downloaded).saveAs(file);
      assert.ok((await stat(file)).size > 100, `${type}: ${format} must be nonempty`);
    }
    await editor.screenshot({ path: path.join(run, `${type}-editor.png`) });
    await checkpoint(`${type}: node deletion, revisions and downloads`);
    console.log(`${type}: button/wheel zoom, node/relation Delete, incident edges, undo/redo, save/reload and SVG/PNG passed.`);
  }
  assert.equal(await status(), calls, 'Local editing, downloads and history must not invoke the CLI');

  await checkpoint('git: generation started');
  await page.getByRole('combobox', { name: /^테스트 샘플/ }).selectOption('cpp-packets');
  await page.getByRole('button', { name: '1. 샘플 변경점 분석', exact: true }).click();
  await page.getByRole('button', { name: '2. Codex로 다이어그램 그리기', exact: true }).click({ timeout: 60000 });
  await editor.locator('svg').waitFor({ timeout: 60000 });
  const gitNames = { flowchart: names.flowchart, sequence: names.sequence, class: names.class, 'code-relation': '변경 구현 맵' };
  for (const [type, name] of Object.entries(gitNames)) {
    await page.locator('.diagram-result .diagram-type-tabs').getByRole('button', { name, exact: true }).click();
    const dslPrefix = type === 'sequence' ? 'sequenceDiagram' : type === 'class' ? 'classDiagram' : 'flowchart ';
    await page.waitForFunction(prefix => document.querySelector('.structured-diagram-editor details pre')?.textContent?.startsWith(prefix), dslPrefix);
    await editor.locator('svg').waitFor();
    const unreadable = await editor.locator('svg text, svg tspan').evaluateAll(elements => elements.filter(element => {
      const style = getComputedStyle(element);
      const hasGlyphs = [...element.childNodes].some(child => child.nodeType === Node.TEXT_NODE && child.textContent.trim());
      return hasGlyphs && style.stroke !== 'none' && Number.parseFloat(style.strokeWidth) > 0;
    }).map(element => element.textContent));
    assert.deepEqual(unreadable, [], `${type}: Git marker strokes must not bleed into label text`);
    const unmappedLabels = await editor.locator('svg g.edgeLabel').evaluateAll(elements => elements
      .filter(element => element.textContent.trim() && element.getAttribute('data-ir-kind') !== 'edge')
      .map(element => element.textContent));
    assert.deepEqual(unmappedLabels, [], `${type}: every relation label must select its relation`);
    await editor.screenshot({ path: path.join(run, `git-${type}.png`) });
    await checkpoint(`git-${type}: readable change labels`);
  }
  console.log('Git flowchart/sequence/class/implementation-map: change labels remain readable.');
  const callsBeforePages = await status();
  await page.locator('.diagram-result .diagram-type-tabs').getByRole('button', { name: names.sequence, exact: true }).click();
  await page.waitForFunction(() => document.querySelector('.structured-diagram-editor details pre')?.textContent?.startsWith('sequenceDiagram'));
  const pagePicker = page.getByRole('combobox', { name: '표시 페이지', exact: true });
  await pagePicker.waitFor();
  const pageIds = await pagePicker.locator('option').evaluateAll(options => options.map(option => option.value));
  assert.ok(pageIds.length >= 2, 'fixture must have distinct overview/detail pages');
  const options = page.locator('.result-options-editor');
  await options.locator(':scope > summary').click();
  const previews = options.locator('.preset-card .diagram-canvas.compact');
  assert.ok(await previews.count() > 0, 'local preset samples must be available');
  for (const width of [1024, 1250, 1366, 1920]) {
    await page.setViewportSize({ width, height: 1100 });
    for (let index = 0; index < await previews.count(); index++) {
      await previews.nth(index).locator('svg').waitFor();
      assert.ok(await previews.nth(index).isVisible(), `preset ${index} visible at ${width}px`);
      const box = await previews.nth(index).boundingBox();
      assert.ok(box.width > 40 && box.height >= 90, `preset ${index} has a readable canvas at ${width}px`);
    }
    await options.screenshot({ path: path.join(run, `presets-${width}.png`) });
    await checkpoint(`presets: ${width}px visible`);
  }
  await options.locator(':scope > summary').click();
  await page.setViewportSize({ width: 1366, height: 1100 });

  let mode = 'race';
  let startedSlow;
  const slowStarted = new Promise(resolve => { startedSlow = resolve; });
  const pageArtifacts = new Map();
  const pagePattern = '**/api/v1/analyses/*/groups/*/views/*/pages/*';
  await page.route(pagePattern, async route => {
    const response = await route.fetch();
    const artifact = await response.json();
    const id = route.request().url().split('/').at(-1);
    if (mode === 'race' && id === pageIds[1]) { startedSlow(); await delay(450); }
    if (mode === 'disabled') artifact.explanation = { summary: '정적 구조 표시', changes: [], factIds: [], evidenceIds: [],
      status: 'Disabled', warnings: ['합성 UI 검사: LLM 비활성화'], basis: 'GeneratedSource' };
    if (mode === 'legacy') delete artifact.explanation;
    pageArtifacts.set(id, artifact);
    await route.fulfill({ response, json: artifact }).catch(() => undefined); // the delayed request can be aborted
  });
  await pagePicker.selectOption(pageIds[1]);
  await slowStarted;
  await pagePicker.selectOption(pageIds[0]);
  await page.waitForResponse(response => response.url().endsWith(`/pages/${pageIds[0]}`));
  const latest = pageArtifacts.get(pageIds[0]);
  assert.ok(latest.explanation, 'new page must persist an explanation');
  await page.waitForFunction(expected => document.querySelector('.page-behavior')?.textContent === expected, latest.explanation.summary);
  await delay(600);
  assert.equal(await page.locator('.page-behavior').textContent(), latest.explanation.summary, 'late page response must not replace current explanation');
  assert.equal(await editor.locator('details pre').first().textContent(), latest.mermaidDsl, 'diagram and explanation must belong to the same page');
  await checkpoint('pages: out-of-order response keeps diagram and explanation together');
  mode = 'disabled';
  await pagePicker.selectOption(pageIds[1]);
  await page.getByText('합성 UI 검사: LLM 비활성화', { exact: true }).waitFor();
  assert.ok(await page.locator('.semantic-incomplete').isVisible());
  assert.ok(await page.getByRole('button', { name: '의미 설명 다시 생성', exact: true }).isVisible());
  mode = 'legacy';
  await pagePicker.selectOption(pageIds[0]);
  await page.getByText('이전 생성 이력에는 페이지별 설명이 없습니다. 다시 그리기를 실행하면 현재 코드 분석으로 생성합니다.', { exact: true }).waitFor();
  mode = 'original';
  await pagePicker.selectOption(pageIds[1]);
  await page.waitForFunction(() => Boolean(document.querySelector('.page-behavior')));
  await editor.getByRole('button', { name: '구조 편집', exact: true }).click();
  await page.locator('.explanation-edit-notice').waitFor();
  await editor.getByRole('button', { name: '취소', exact: true }).click();
  await page.locator('.explanation-edit-notice').waitFor({ state: 'detached' });
  await page.unroute(pagePattern);
  assert.equal(await status(), callsBeforePages, 'preset previews, page switches and manual editing must not invoke the CLI');
  await checkpoint('pages: disabled, legacy, manual edit notice and no extra LLM calls');
  await checkAnalysisResultUi({ page, origin, run, status });
  await checkpoint('results: 80 collapsed notices, distinct groups, left dropdown arrow, 603 searchable/paged references and lazy snippets');
  assert.deepEqual(errors, [], 'No uncaught browser errors');
  await writeFile(path.join(run, 'result.json'), JSON.stringify({ status: 'passed', checkpoints }, null, 2));
  console.log(`UI smoke passed with FAKE CLI; no real Codex calls. Screenshots: ${path.relative(root, run)}`);
} catch (error) {
  await page?.screenshot({ path: path.join(run, 'failure.png'), fullPage: true }).catch(() => undefined);
  if (page) await writeFile(path.join(run, 'failure.html'), await page.content()).catch(() => undefined);
  await writeFile(path.join(run, 'result.json'), JSON.stringify({ status: 'failed', checkpoints, error: String(error) }, null, 2));
  console.error(`UI smoke failed. Diagnostics: ${path.relative(root, run)}`);
  throw error;
} finally {
  if (browser) await browser.close();
  if (origin) await fetch(`${origin}/api/v1/sample-tests/shutdown`, { method: 'POST' }).catch(() => undefined);
  for (let i = 0; i < 50 && server.exitCode === null; i++) await delay(100);
  if (server.exitCode === null) server.kill();
  await writeFile(path.join(run, 'server.log'), output);
}
