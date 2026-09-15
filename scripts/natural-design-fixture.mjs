// Deterministic synthetic contract fixture; never evidence of real model quality.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import { checkZoomWheel } from './diagram-presentation-ui-checks.mjs';

export const naturalPrompt = '문이 열려 있으면 장비 운전을 차단한다.';
export function naturalDesignFixture(context, properties, mode) {
  if (properties.requirements) return { title: '장비 설계', entities: ['장비'], requirements: [
    { id: 'r1', text: naturalPrompt, kind: 'interlock', origin: 'explicit', sourceQuote: naturalPrompt }] };
  if (properties.reviewedRequirementIds) return { accepted: mode !== 'natural-reject', reviewedRequirementIds: ['r1'],
    issues: mode === 'natural-reject' ? ['문 열림 차단 경로를 보완하세요'] : [] };
  if (!properties.nodes?.items?.properties?.members) return undefined;
  const node = (id, label, kind, members = []) => ({ id, label, kind, shape: '', members, details: [], requirementIds: ['r1'], assumption: false });
  const edge = (id, sourceId, targetId, label, extras = {}) => ({ id, sourceId, targetId, label,
    type: context.type === 'state' ? 'transition' : context.type === 'sequence' ? 'message' : 'flow',
    event: '', guard: '', action: '', controlPath: [], requirementIds: ['r1'], assumption: false, ...extras });
  const result = { title: '장비 설계', nodes: [], edges: [], notes: [] };
  if (context.type === 'class') result.nodes = [node('machine', '장비', 'class', [
    { name: 'doorOpen', kind: 'field', visibility: 'private', type: 'bool', parameters: [], preconditions: [] },
    { name: 'Run', kind: 'method', visibility: 'public', type: 'void', parameters: [], preconditions: ['문이 닫힘'] }])];
  else if (context.type === 'state') {
    result.nodes = [node('start', '시작', 'initial'), node('idle', '대기', 'state'), node('run', '운전', 'state'), node('end', '종료', 'final')];
    result.edges = [edge('e1', 'start', 'idle', ''), edge('e2', 'idle', 'run', '', { event: '운전 요청', guard: '문이 닫힘', action: '가동' }), edge('e3', 'run', 'end', '정지')];
  } else if (context.type === 'sequence') {
    result.nodes = [node('operator', '작업자', 'participant'), node('machine', '장비', 'participant')];
    result.edges = [edge('e1', 'operator', 'machine', '운전 요청', { controlPath: [{ id: 'door', kind: 'alt', label: '문 상태', branch: '닫힘' }] }),
      edge('e2', 'machine', 'operator', '운전 차단', { controlPath: [{ id: 'door', kind: 'alt', label: '문 상태', branch: '열림' }] })];
  } else {
    result.nodes = [node('check', '문이 닫혔는가', 'decision'), node('run', '운전', 'operation'), node('stop', '차단', 'terminal')];
    result.edges = [edge('e1', 'check', 'run', '예'), edge('e2', 'check', 'stop', '아니요')];
  }
  return result;
}

export async function checkNaturalDesign(request, count, setMode, root, origin, fixture) {
  const views = ['class', 'flowchart', 'sequence', 'state'].map(diagramType => ({ id: diagramType, diagramType, presetId: 'balanced' }));
  const body = { prompt: naturalPrompt, views };
  const before = count();
  const record = await request('/natural-diagrams', 'POST', body, 201);
  assert.equal(count() - before, 9, 'one shared extraction plus independent design/review for four views');
  assert.equal(record.requirements.requirements.length, 1);
  assert.ok(record.views.every(view => view.designQuality.status === 'Reviewed' && view.designQuality.reviewedRequirementIds.length === 1));
  assert.deepEqual((await request(`/natural-diagrams/${record.id}`)).requirements, record.requirements);
  assert.equal((await request('/natural-diagrams', 'POST', body, 201)).id, record.id);
  assert.equal(count() - before, 9, 'cached result causes no generation');
  setMode('natural-reject');
  const failed = await request(`/natural-diagrams/${record.id}/views/revise`, 'POST', { views: record.request.views, regenerateViewIds: ['state'] }, 201);
  assert.equal(count() - before, 13, 'selected view uses its one repair without repeating requirements');
  assert.equal(failed.views.find(view => view.viewId === 'state').state, 'Failed');
  assert.equal(failed.views.find(view => view.viewId === 'state').diagram.id, record.views.find(view => view.viewId === 'state').diagram.id);
  assert.ok(failed.views.filter(view => view.viewId !== 'state').every(view => view.reused));
  setMode('valid');
  const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    await page.goto(origin);
    await page.getByRole('navigation').getByRole('button', { name: '자연어 다이어그램', exact: true }).click();
    await page.locator('.revision-list button').filter({ hasText: '장비 설계' }).first().click();
    const preview = page.locator('.preview');
    for (const [type, label] of [['class', '클래스'], ['flowchart', '플로우차트'], ['sequence', '시퀀스'], ['state', '상태']]) {
      const index = views.findIndex(view => view.id === type);
      await preview.locator('.diagram-type-tabs button').nth(index).click();
      await preview.locator('.diagram-canvas svg').waitFor();
      await checkZoomWheel(page, preview.locator('.structured-diagram-editor'));
      await preview.locator('.diagram-canvas').screenshot({ path: path.join(fixture, `natural-${type}.png`) });
      assert.ok((await preview.locator('.diagram-canvas').innerText()).length > 0, `${label} renders`);
    }
    assert.deepEqual(errors, []);
    assert.equal(count() - before, 13, 'view navigation never invokes the LLM');
  } finally { await browser.close(); }
  return { name: 'natural-design', formats: 4, requests: 13, sharedRequirements: 1, rejectedRepairPreservesSource: true };
}
