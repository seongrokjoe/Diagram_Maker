import assert from 'node:assert/strict';
import path from 'node:path';

export async function checkAnalysisResultUi({ page, origin, run, status }) {
  const callsBefore = await status();
  const history = page.getByRole('combobox', { name: '생성 이력', exact: true });
  const analysisId = await history.inputValue();
  const analysisUrl = `${origin}/api/v1/analyses/${analysisId}?includeGraph=false&summary=true`;
  const original = await (await page.request.get(analysisUrl)).json();
  const fixture = structuredClone(original);
  const originalGroup = fixture.result.diagramGroups[0];
  fixture.result.narrative.warnings = Array.from({ length: 80 }, (_, index) => `[File${index + 1}.cs] 합성 분석 안내 ${index + 1}`);
  fixture.result.diagramGroups = [
    { ...structuredClone(originalGroup), title: '알람 내보내기 그룹' },
    { ...structuredClone(originalGroup), groupId: 'ui-second-group', title: 'CSV 검증 그룹' },
    { ...structuredClone(originalGroup), groupId: 'ui-failed-group', title: '실패한 그룹', views: originalGroup.views.map(view => ({
      ...view, diagram: null, document: null, state: 'Failed', errorMessage: '합성 검사: 생성 실패', warnings: ['합성 검사: 생성 실패'],
    })) },
  ];
  const evidenceIds = Array.from({ length: 603 }, (_, index) => `ui-evidence-${index + 1}`);
  const pagePattern = '**/api/v1/analyses/*/groups/*/views/*/pages/*';
  const evidencePattern = '**/api/v1/analyses/*/evidence/ui-evidence-*/snippet';
  const evidenceRequests = [];
  await page.route(analysisUrl, route => route.fulfill({ json: fixture }));
  await page.route(pagePattern, async route => {
    const requestUrl = new URL(route.request().url());
    const segments = requestUrl.pathname.split('/');
    const groupId = segments[6];
    const pageId = segments.at(-1);
    requestUrl.pathname = requestUrl.pathname.replace('/groups/ui-second-group/', `/groups/${originalGroup.groupId}/`);
    const response = await route.fetch({ url: requestUrl.toString() });
    const artifact = await response.json();
    artifact.explanation = { summary: `${groupId} · ${pageId}의 동작 설명`, changes: [], factIds: [], evidenceIds: [...evidenceIds, evidenceIds[0]],
      status: 'Disabled', warnings: ['합성 검사: 의미 설명 미완료', ...fixture.result.narrative.warnings], basis: 'GeneratedSource' };
    artifact.ir.nodes[0].evidenceIds = [...artifact.ir.nodes[0].evidenceIds, evidenceIds[0]];
    await route.fulfill({ response, json: artifact });
  });
  await page.route(evidencePattern, route => {
    evidenceRequests.push(route.request().url());
    return route.fulfill({ json: { revisionSha: 'a'.repeat(40), blobOid: 'b'.repeat(40), filePath: 'synthetic/Example.cs',
      startLine: 603, endLine: 603, content: 'var sample = 603;' } });
  });
  try {
    await history.selectOption(analysisId);
    const groups = page.getByRole('tree', { name: '다이어그램 결과 트리', exact: true });
    const selectGroup = groupId => groups.locator(`[aria-level="4"][data-row-id^="view:${groupId}/"]`).first().click();
    await groups.getByText('CSV 검증 그룹', { exact: true }).first().waitFor();
    assert.equal(await groups.locator('[aria-selected="true"]').count(), 1);
    await groups.screenshot({ path: path.join(run, 'result-tree-selector.png') });

    const warnings = page.locator('.analysis-result .result-content > .result-notices');
    assert.equal(await warnings.getAttribute('open'), null);
    assert.equal(await warnings.locator('li').count(), 0);
    await warnings.locator('summary').focus();
    await page.keyboard.press('Enter');
    await warnings.locator('li').last().waitFor();
    assert.equal(await warnings.locator('li').count(), 80);
    await warnings.locator('summary').click();
    assert.equal(await warnings.getAttribute('open'), null);

    await selectGroup('ui-second-group');
    await page.waitForFunction(() => document.querySelector('.page-behavior')?.textContent?.startsWith('ui-second-group ·'));
    assert.equal(await page.locator('.diagram-result .structured-diagram-editor').count(), 1);
    await page.locator('.code-block-explanation > summary').click();
    assert.ok((await groups.locator('[aria-selected="true"]').getAttribute('data-row-id')).startsWith('view:ui-second-group/'));
    const options = page.locator('.result-options-editor');
    assert.equal(await options.getAttribute('open'), null);
    await options.locator(':scope > summary').click();
    await options.getByRole('button', { name: '이 다이어그램 다시 그리기', exact: true }).waitFor();
    await page.keyboard.press('Escape');
    assert.equal(await options.getAttribute('open'), null);
    assert.equal(await options.locator(':scope > summary').evaluate(e => e === document.activeElement), true);

    const evidence = page.locator('.page-evidence > .evidence-browser');
    await evidence.waitFor();
    assert.equal(await evidence.getAttribute('open'), null);
    assert.equal(await evidence.locator('.evidence-item').count(), 0);
    assert.match(await evidence.locator('summary').textContent(), /603건/);
    assert.ok(await page.locator('.semantic-incomplete').isVisible());
    assert.equal(await page.locator('.semantic-incomplete > .result-notices').getAttribute('open'), null);
    await page.locator('.diagram-explanation').screenshot({ path: path.join(run, 'result-explanation-collapsed.png') });
    await evidence.locator('summary').click();
    await evidence.locator('.evidence-item').first().waitFor();
    assert.equal(await evidence.locator('.evidence-item').count(), 20);
    await evidence.getByRole('button', { name: '다음 근거', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.page-evidence .evidence-item strong')?.textContent === '근거 21');
    await evidence.getByRole('searchbox', { name: '근거 검색', exact: true }).fill('근거 603');
    await page.waitForFunction(() => document.querySelector('.page-evidence .evidence-item strong')?.textContent === '근거 603');
    assert.equal(await evidence.locator('.evidence-item').count(), 1);
    assert.equal(evidenceRequests.length, 0, 'opening, paging and filtering must not fetch snippets');
    await evidence.locator('.evidence-item').click();
    await page.getByText('var sample = 603;', { exact: true }).waitFor();
    assert.equal(evidenceRequests.length, 1);
    assert.ok(evidenceRequests[0].endsWith('/ui-evidence-603/snippet'));
    await evidence.screenshot({ path: path.join(run, 'result-evidence-search.png') });
    await evidence.getByRole('searchbox').fill('no-matching-evidence');
    await evidence.getByText('검색 결과가 없습니다.', { exact: true }).waitFor();
    assert.ok(await evidence.getByRole('button', { name: '다음 근거', exact: true }).isDisabled());

    await selectGroup(originalGroup.groupId);
    await page.waitForFunction(prefix => document.querySelector('.page-behavior')?.textContent?.startsWith(prefix), `${originalGroup.groupId} ·`);
    assert.equal(await page.locator('.diagram-result .structured-diagram-editor').count(), 1);
    assert.equal(await evidence.getAttribute('open'), null);
    assert.equal(await page.getByText('var sample = 603;', { exact: true }).count(), 0);
    await page.locator('.code-block-explanation > summary').click();
    await evidence.locator('summary').click();
    assert.equal(await evidence.getByRole('searchbox').inputValue(), '');
    await groups.locator('[data-row-id^="view:ui-failed-group/"][data-row-id$=":ai-status"]').first().click();
    await page.getByText('합성 검사: 생성 실패', { exact: true }).first().waitFor();
    assert.equal(await page.locator('.diagram-result .structured-diagram-editor').count(), 0);
    assert.ok((await groups.locator('[aria-selected="true"]').getAttribute('data-row-id')).startsWith('view:ui-failed-group/'));
    assert.equal(await status(), callsBefore, 'result navigation must not invoke the CLI');
  } finally {
    await page.unroute(analysisUrl);
    await page.unroute(pagePattern);
    await page.unroute(evidencePattern);
  }
}
