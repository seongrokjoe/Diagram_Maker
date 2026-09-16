import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import path from 'node:path';

// Real API and persisted artifacts; only the loopback model supplies synthetic review failures.
export async function checkPartialGitAi({ request, root, origin, fixture, analysis, requestCount }) {
  assert.ok(analysis.resultCounts.aiPartial > 0, JSON.stringify(analysis.resultCounts));
  const group = analysis.result.diagramGroups[0];
  const view = group.views.find(value => value.document?.pages.some(page => page.aiState === 'Partial'));
  assert.ok(view, 'a partly reviewed AI page remains available');
  const source = view.document.pages.find(page => page.aiState === 'Partial');
  const base = `/analyses/${analysis.id}/groups/${group.groupId}/views/${view.viewId}/pages/${source.id}`;
  const ai = await request(base + '?variant=ai');
  const code = await request(base + '?variant=code');
  assert.notEqual(ai.id, code.id);
  assert.equal(ai.explanation.status, 'Incomplete');
  assert.ok(ai.explanation.coverage.verifiedUnits > 0 && ai.explanation.coverage.failedUnits > 0);
  assert.ok(ai.explanation.failures.some(failure => failure.issueCodes.includes('incorrect_change') &&
    failure.factIds.length > 0 && failure.correctionInstructions.length > 0));
  assert.equal(code.explanation.status, 'Static');
  assert.equal((await request(base + '?variant=ai')).id, ai.id, 'partial AI survives a fresh API read');
  const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const before = requestCount(), errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
  try {
    await page.goto(origin);
    await page.getByRole('navigation').getByRole('button', { name: 'Git 변경 분석', exact: true }).click();
    const workspace = page.locator('.analysis-workspace');
    await workspace.locator('.revision-list button').filter({ hasText: analysis.targetSha.slice(0, 10) }).first().click();
    await workspace.getByRole('combobox', { name: '생성 이력', exact: true }).selectOption(analysis.id);
    await workspace.getByRole('button', { name: '3. 다이어그램 확인', exact: true }).click();
    const tree = workspace.getByRole('tree');
    const row = tree.locator(`[data-row-id="view:${group.groupId}/${view.viewId}:${source.id}:ai"]`);
    await row.click();
    assert.match(await row.innerText(), /부분 완료/);
    const result = workspace.locator('.analysis-result');
    await result.locator('.structured-diagram-editor .diagram-canvas svg').first().waitFor();
    await result.locator('.code-block-explanation > summary').click();
    await workspace.getByText('AI 의미 설명 부분 완료', { exact: true }).waitFor();
    await workspace.locator('.semantic-failure-details > summary').click();
    await workspace.getByText('검토 사유: incorrect_change', { exact: true }).first().waitFor();
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 1000 });
      await workspace.locator('.resizable-results').screenshot({ path: path.join(fixture, `git-partial-ai-${width}.png`) });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    }
    assert.equal(requestCount(), before, 'partial result inspection never regenerates annotations');
    assert.deepEqual(errors, []);
  } catch (error) {
    await page.screenshot({ path: path.join(fixture, 'git-partial-ai-failure.png') }).catch(() => {});
    throw error;
  } finally { await browser.close(); }
  return { name: 'git-partial-ai', partialPages: analysis.resultCounts.aiPartial,
    structuredFailures: true, independentCodeOriginal: true, apiPersistence: true, ui: true };
}
