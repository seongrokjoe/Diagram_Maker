import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

// Synthetic source only; exercise the real parser, compiler and evidence UI.
export async function checkExecutionEvidence({ page, fixture, origin }) {
  const code = await readFile(new URL('../tests/fixtures/SendInitDataRequest.cpp', import.meta.url), 'utf8');
  const api = origin + '/api/v1';
  const created = await page.request.post(api + '/code-block-workspaces', { data: {
    title: '실행 경로 정확도 검증', blocks: [{ id: 'guards', language: 'cpp', title: '초기화 요청', code }],
    groups: [{ id: 'g', title: '초기화 요청', blockIds: ['guards'], views: [{ id: 's', diagramType: 'sequence', presetId: 'balanced' }] }],
  } });
  assert.equal(created.status(), 201);
  const workspace = await created.json();
  const started = await page.request.post(`${api}/code-block-workspaces/${workspace.id}/runs`, { data: { expectedRevision: workspace.revision } });
  assert.equal(started.status(), 202);
  let run = await started.json();
  for (let attempt = 0; attempt < 300 && !['Partial', 'Completed', 'Failed'].includes(run.state); attempt++) {
    await delay(100); run = await (await page.request.get(`${api}/code-block-runs/${run.id}`)).json();
  }
  assert.equal(run.state, 'Partial', 'disabled LLM must remain visibly incomplete');
  const view = run.results[0].views[0];
  const artifact = await (await page.request.get(`${api}/code-block-runs/${run.id}/groups/g/views/s/pages/${view.pages[0].id}`)).json();
  const calls = ['IsInitDataRequestStatus()', 'Sleep(10)', 'SendUnitIntervalTime()', 'SetUnitMode()',
    'RequestUpdateVersion()', 'Sleep(100)', 'RequestUpdateFirmwareVersion()'];
  assert.deepEqual(artifact.ir.edges.filter(e => e.type === 'message').map(e => e.originalExpression), calls);
  assert.deepEqual(artifact.ir.edges.filter(e => e.type === 'return').map(e => e.returnValue), ['false', 'false', 'false', 'false', 'false', 'true']);
  await page.goto(origin + '/?' + new URLSearchParams({ codeWorkspace: workspace.id, codeRun: run.id,
    codeScreen: 'results', codeGroup: 'g', codeView: 's', codePage: view.pages[0].id }));
  const screen = page.locator('.code-block-workspace');
  await screen.getByText('의미 생성 미완료 · 실패 사유와 정적 구조를 확인하세요', { exact: true }).waitFor();
  const editor = screen.locator('.structured-diagram-editor');
  await editor.locator('.diagram-canvas svg').first().waitFor();
  await editor.getByText('동작 설명과 원본 근거', { exact: true }).click();
  const facts = editor.locator('.execution-evidence');
  await facts.locator(':scope > summary').click();
  const rows = facts.locator('.node-evidence-row');
  assert.equal(await rows.filter({ hasText: /^호출 ·/ }).count(), 7);
  assert.equal(await rows.filter({ hasText: /^조건 ·|^종료 조건 ·/ }).count(), 5);
  assert.equal(await rows.filter({ hasText: /^반환 ·/ }).count(), 6);
  for (let index = 0; index < await rows.count(); index++) {
    const row = rows.nth(index);
    const expression = await row.locator('code').innerText();
    await row.locator('.evidence-browser > summary').click();
    await row.locator('.evidence-item').first().click();
    const evidence = screen.getByRole('region', { name: '코드 근거', exact: true });
    await evidence.waitFor();
    const excerpt = await evidence.locator('pre').innerText();
    assert.ok(code.includes(excerpt), 'evidence must come from the immutable pasted source');
    assert.ok(excerpt.includes(expression), `evidence must contain ${expression}`);
    await evidence.getByRole('button', { name: '근거 닫기', exact: true }).click();
    await row.locator('.evidence-browser > summary').click();
  }
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await editor.screenshot({ path: path.join(fixture, `execution-evidence-${width}.png`) });
    const overflow = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      elements: [...document.querySelectorAll('.code-block-workspace *')].filter(element => {
        const box = element.getBoundingClientRect();
        return box.width > 0 && box.right > innerWidth + 1 && getComputedStyle(element).position !== 'absolute';
      }).slice(0, 20).map(element => ({ tag: element.tagName, className: element.className,
        right: element.getBoundingClientRect().right, width: element.getBoundingClientRect().width })) }));
    assert.ok(overflow.scrollWidth <= width + 1, JSON.stringify(overflow));
  }
  const downloading = page.waitForEvent('download');
  await editor.getByRole('button', { name: 'SVG 다운로드', exact: true }).click();
  await (await downloading).saveAs(path.join(fixture, 'execution-sequence.svg'));
  await writeFile(path.join(fixture, 'execution-result.json'), JSON.stringify({ calls, conditions: 5,
    returns: ['false', 'false', 'false', 'false', 'false', 'true'], evidenceRows: await rows.count(), runId: run.id }, null, 2));
  await page.reload();
  await editor.locator('.diagram-canvas svg').first().waitFor();
  assert.equal(new URL(page.url()).searchParams.get('codeRun'), run.id);
  await page.setViewportSize({ width: 1440, height: 1000 });
}
