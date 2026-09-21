// Deterministic synthetic contract fixture; never evidence of real model quality.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';
import { writeFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import { checkZoomWheel } from './diagram-presentation-ui-checks.mjs';

export const naturalPrompt = '문이 열려 있으면 장비 운전을 차단한다.';
export function naturalDesignFixture(context, properties, mode) {
  if (properties.reviewedSourceRangeIds) return { accepted: true,
    reviewedSourceRangeIds: context.sourceRanges.map(range => range.id), issues: [] };
  if (properties.requirements) return { title: '장비 설계', entities: ['장비'], requirements: [
    { id: 'r1', text: naturalPrompt, kind: 'interlock', origin: 'explicit', sourceQuote: '', sourceRangeIds: context.sourceRanges.map(range => range.id) }],
    scenarios: [{ id: 'scenario-1', title: '장비 설계', requirementIds: ['r1'], sourceRangeIds: context.sourceRanges.map(range => range.id) }], questions: [] };
  if (properties.reviewedRequirementIds) return { accepted: mode !== 'natural-reject', reviewedRequirementIds: ['r1'],
    issues: mode === 'natural-reject' ? ['문 열림 차단 경로를 보완하세요'] : [], itemIssues: [] };
  if (!properties.nodes?.items?.properties?.requirementIds) return undefined;
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

async function setZoom(editor, value) {
  const select = editor.locator(`.diagram-actions select:has(option[value="${value}"])`);
  await select.selectOption(String(value));
  await editor.locator('.zoom-status').filter({ hasText: `${value * 100}%` }).waitFor();
}

async function centerCanvas(canvas) {
  return canvas.evaluate(element => {
    element.scrollLeft = Math.max(0, (element.scrollWidth - element.clientWidth) / 2);
    element.scrollTop = Math.max(0, (element.scrollHeight - element.clientHeight) / 2);
    return { left: element.scrollLeft, top: element.scrollTop };
  });
}

async function canvasPosition(canvas) {
  return canvas.evaluate(element => ({ left: element.scrollLeft, top: element.scrollTop }));
}

async function drag(page, target, deltaX, deltaY, holdSpace = false, fromPadding = false) {
  const box = await target.boundingBox();
  assert.ok(box, 'drag target must be visible');
  const x = fromPadding ? box.x + 8 : box.x + Math.min(Math.max(4, box.width / 2), Math.max(4, box.width - 4));
  const y = fromPadding ? box.y + 8 : box.y + Math.min(Math.max(4, box.height / 2), Math.max(4, box.height - 4));
  if (holdSpace) await page.keyboard.down('Space');
  try {
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.move(x + deltaX, y + deltaY, { steps: 6 });
    await page.mouse.up();
  } finally {
    if (holdSpace) await page.keyboard.up('Space');
  }
}

async function waitForInputValue(locator, expected) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (await locator.inputValue() === expected) return;
    await delay(50);
  }
  assert.equal(await locator.inputValue(), expected);
}

async function checkViewPan(page, editor) {
  const canvas = editor.locator('.diagram-canvas');
  await setZoom(editor, 4);
  await centerCanvas(canvas);
  const before = await canvasPosition(canvas);
  await drag(page, editor.locator('[data-ir-kind="node"]').first(), -80, -60);
  const after = await canvasPosition(canvas);
  assert.ok(after.left > before.left || after.top > before.top, 'view mode drag over a rendered element pans the canvas');
  await setZoom(editor, 1);
}

async function checkClassDirectEdit(page, editor) {
  await editor.getByRole('button', { name: '구조 편집', exact: true }).click();
  const members = editor.locator('[data-ir-kind="member"]');
  await members.first().waitFor();
  assert.equal(await members.count(), 3, 'class members and precondition text map back to their original IR indices');
  await members.first().dblclick();
  const inline = editor.locator('.diagram-inline-editor.member input');
  await inline.waitFor();
  await inline.fill('-doorClosed bool');
  await inline.press('Enter');
  await editor.locator('.structure-editor-section > summary').filter({ hasText: '클래스 멤버' }).click();
  const memberInput = editor.locator('.member-edit-row input').first();
  await waitForInputValue(memberInput, '-doorClosed bool');
  await editor.getByRole('button', { name: '실행 취소', exact: true }).click();
  await waitForInputValue(memberInput, '-bool doorOpen');
  await editor.getByRole('button', { name: '다시 실행', exact: true }).click();
  await waitForInputValue(memberInput, '-doorClosed bool');
  await editor.locator('[data-ir-kind="member"]').filter({ hasText: 'doorClosed' }).waitFor({ timeout: 10000 });
  await editor.locator('.structure-editor-panel button.primary').click();
  await editor.locator('.structure-editor-panel').waitFor({ state: 'detached' });
}

async function checkSequenceDirectEditAndPan(page, editor) {
  await editor.getByRole('button', { name: '구조 편집', exact: true }).click();
  const annotations = editor.locator('[data-ir-kind="annotation"]');
  await annotations.first().waitFor();
  await annotations.first().dblclick();
  const inline = editor.locator('.diagram-inline-editor.annotation input');
  await inline.waitFor();
  await inline.fill('문이 닫힌 경우');
  await inline.press('Enter');
  await editor.locator('.structure-editor-section > summary').filter({ hasText: '시퀀스 조건·메모' }).click();
  const annotationInput = editor.locator('.annotation-edit-row input').first();
  await waitForInputValue(annotationInput, '문이 닫힌 경우');
  await editor.locator('[data-ir-kind="annotation"]').filter({ hasText: '문이 닫힌 경우' }).waitFor({ timeout: 10000 });

  const canvas = editor.locator('.diagram-canvas');
  await setZoom(editor, 4);
  await centerCanvas(canvas);
  const node = editor.locator('[data-ir-kind="node"]').first();
  await node.scrollIntoViewIfNeeded();
  const beforeBlocked = await canvasPosition(canvas);
  await drag(page, node, -70, -50);
  assert.deepEqual(await canvasPosition(canvas), beforeBlocked, 'edit mode keeps rendered elements available for selection');
  await drag(page, node, -70, -50, true);
  const afterSpace = await canvasPosition(canvas);
  assert.ok(afterSpace.left > beforeBlocked.left || afterSpace.top > beforeBlocked.top, 'Space + drag pans over a rendered element in edit mode');

  await centerCanvas(canvas);
  const beforeBlank = await canvasPosition(canvas);
  const blank = await canvas.evaluate(element => {
    const box = element.getBoundingClientRect();
    for (let y = Math.max(8, box.top + 8); y < Math.min(innerHeight - 8, box.bottom - 8); y += 20) {
      for (let x = Math.max(8, box.left + 8); x < Math.min(innerWidth - 8, box.right - 8); x += 20) {
        const target = document.elementFromPoint(x, y);
        if (target && element.contains(target) && !target.closest('[data-ir-id]')) return { x, y };
      }
    }
    return null;
  });
  assert.ok(blank, 'a visible blank canvas point is available');
  await page.mouse.move(blank.x, blank.y);
  await page.mouse.down();
  await page.mouse.move(blank.x - 55, blank.y - 45, { steps: 6 });
  await page.mouse.up();
  const afterBlank = await canvasPosition(canvas);
  assert.ok(afterBlank.left > beforeBlank.left || afterBlank.top > beforeBlank.top, 'blank-space drag pans in edit mode');
  await setZoom(editor, 1);
  await editor.locator('.structure-editor-panel button.primary').click();
  await editor.locator('.structure-editor-panel').waitFor({ state: 'detached' });
}

export async function checkNaturalDesign(request, count, setMode, root, origin, fixture) {
  const views = ['class', 'flowchart', 'sequence', 'state'].map(diagramType => ({ id: diagramType, diagramType, presetId: 'balanced' }));
  const body = { prompt: naturalPrompt, views };
  const before = count();
  const record = await request('/natural-diagrams', 'POST', body, 201);
  assert.equal(count() - before, 11, 'shared extraction/review, four design/reviews, and cross-view review');
  assert.equal(record.requirements.requirements.length, 1);
  assert.ok(record.views.every(view => view.designQuality.status === 'Reviewed' && view.designQuality.reviewedRequirementIds.length === 1));
  assert.deepEqual((await request(`/natural-diagrams/${record.id}`)).requirements, record.requirements);
  assert.equal((await request('/natural-diagrams', 'POST', body, 201)).id, record.id);
  assert.equal(count() - before, 11, 'cached result causes no generation');
  setMode('natural-reject');
  const failed = await request(`/natural-diagrams/${record.id}/views/revise`, 'POST', { views: record.request.views, regenerateViewIds: ['state'] }, 201);
  assert.equal(count() - before, 17, 'selected view uses two repairs without repeating requirements');
  assert.equal(failed.views.find(view => view.viewId === 'state').state, 'Failed');
  assert.equal(failed.views.find(view => view.viewId === 'state').diagram.id, record.views.find(view => view.viewId === 'state').diagram.id);
  assert.ok(failed.views.filter(view => view.viewId !== 'state').every(view => view.reused));
  setMode('valid');
  const statePage = record.views.find(view => view.viewId === 'state').pages[0];
  const queuedRun = await request('/natural-diagram-runs', 'POST', { request: record.request,
    sourceDiagramId: record.id, regeneratePageIds: [statePage.id] }, 202);
  let run;
  for (let attempt = 0; attempt < 300; attempt++) {
    run = await request(`/natural-diagram-runs/${queuedRun.id}`);
    if (['Completed', 'Partial', 'Failed', 'Cancelled'].includes(run.state)) break;
    await delay(100);
  }
  assert.equal(run.state, 'Completed', run.errorMessage);
  assert.equal(run.progress, 100);
  assert.ok(run.resultDiagramId);
  const runRecord = await request(`/natural-diagrams/${run.resultDiagramId}`);
  const regenerated = runRecord.views.find(view => view.viewId === 'state').pages[0];
  assert.notEqual(regenerated.diagram.id, statePage.diagram.id, 'selected scenario page is regenerated');
  assert.ok(runRecord.views.filter(view => view.viewId !== 'state').every(view => view.reused));
  assert.equal(count() - before, 20, 'background page regeneration reuses requirements and reviews the selected design and overall result');
  const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  let page;
  try {
    page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    await page.goto(origin);
    await page.getByRole('navigation').getByRole('button', { name: '자연어 다이어그램', exact: true }).click();
    let releaseRevisions;
    const revisionGate = new Promise(resolve => { releaseRevisions = resolve; });
    const revisionRoute = '**/api/v1/diagram-artifacts/*/revisions';
    await page.route(revisionRoute, async route => { await revisionGate; await route.continue(); });
    await page.locator('.revision-list button').filter({ hasText: '장비 설계' }).first().click();
    const preview = page.locator('.preview');
    try {
      await preview.locator('.structured-diagram-editor[aria-busy="true"]').waitFor();
      assert.equal(await preview.getByRole('button', { name: '구조 편집', exact: true }).isDisabled(), true,
        'editing waits for stored revisions so a late response cannot replace the draft');
    } finally { releaseRevisions(); }
    await preview.locator('.structured-diagram-editor[aria-busy="false"]').waitFor();
    await page.unroute(revisionRoute);
    for (const [type, label] of [['class', '클래스'], ['flowchart', '플로우차트'], ['sequence', '시퀀스'], ['state', '상태']]) {
      const index = views.findIndex(view => view.id === type);
      await preview.locator('.diagram-type-tabs button').nth(index).click();
      await preview.locator('.diagram-canvas[aria-busy="false"] svg').waitFor();
      const editor = preview.locator('.structured-diagram-editor');
      await checkZoomWheel(page, editor);
      if (type === 'class') {
        await checkViewPan(page, editor);
        await checkClassDirectEdit(page, editor);
      }
      if (type === 'sequence') await checkSequenceDirectEditAndPan(page, editor);
      await preview.locator('.diagram-canvas').screenshot({ path: path.join(fixture, `natural-${type}.png`) });
      assert.ok((await preview.locator('.diagram-canvas').innerText()).length > 0, `${label} renders`);
    }
    await page.reload();
    await page.getByRole('navigation').getByRole('button', { name: '자연어 다이어그램', exact: true }).click();
    await page.locator('.revision-list button').filter({ hasText: '장비 설계' }).first().click();
    const restored = page.locator('.preview');
    await restored.locator('.diagram-canvas svg').waitFor();
    await restored.locator('[data-ir-kind="member"]').filter({ hasText: 'doorClosed' }).waitFor();
    await restored.locator('.diagram-type-tabs button').nth(2).click();
    await restored.locator('[data-ir-kind="annotation"]').filter({ hasText: '문이 닫힌 경우' }).waitFor();
    assert.deepEqual(errors, []);
    assert.equal(count() - before, 20, 'view navigation never invokes the LLM');
  } catch (error) {
    if (page) {
      await page.screenshot({ path: path.join(fixture, 'natural-ui-failure.png') }).catch(() => {});
      await writeFile(path.join(fixture, 'natural-ui-failure.html'), await page.content());
    }
    throw error;
  } finally { await browser.close(); }
  return { name: 'natural-design', formats: 4, requests: 20, sharedRequirements: 1,
    backgroundRun: true, selectedScenarioRegeneration: true, rejectedRepairPreservesSource: true,
    directSvgEditing: ['class-member', 'sequence-condition'], canvasPan: ['view', 'edit-blank', 'edit-space'], revisionRestore: true };
}
