import assert from 'node:assert/strict';
import path from 'node:path';
import { writeFile } from 'node:fs/promises';

export async function captureWidths(page, fixture, name, widths = [1366, 1440, 1920, 800, 390]) {
  for (const width of widths) {
    await page.setViewportSize({ width, height: 1000 });
    await page.screenshot({ path: path.join(fixture, `${name}-${width}.png`), fullPage: true });
    const overflow = await page.evaluate(() => [...document.querySelectorAll('body *')].filter(e => {
      const b = e.getBoundingClientRect(); return b.width > 0 && b.right > innerWidth + 1 && getComputedStyle(e).position !== 'absolute';
    }).slice(0, 5).map(e => e.className));
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${name} ${width}px overflow: ${overflow}`);
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
}

export async function checkDesignUi({ page, origin, fixture }) {
  await page.goto(origin);
  await page.locator('.preset-list svg').last().waitFor();
  await page.evaluate(() => document.fonts.ready);
  assert.ok(await page.evaluate(() => [...document.fonts].some(font => font.family.includes('Pretendard') && font.status === 'loaded')), 'bundled font actually loaded');
  const fontRequests = await page.evaluate(() => performance.getEntriesByType('resource').filter(e => e.name.endsWith('.woff2')).map(e => e.name));
  assert.ok(fontRequests.length && fontRequests.every(url => new URL(url).origin === origin), 'font comes from the app');
  const presets = await (await page.request.get(origin + '/api/v1/diagram-presets')).json();
  for (const type of ['flowchart', 'sequence', 'class', 'state']) {
    await page.getByRole('combobox', { name: '다이어그램 종류', exact: true }).selectOption(type);
    const names = presets.filter(p => p.type === type).map(p => p.name);
    await page.waitForFunction(expected => [...document.querySelectorAll('.preset-description > strong')].map(e => e.textContent).join('|') === expected.join('|'), names);
    const cards = page.locator('.preset-list .preset-card');
    assert.equal(await cards.count(), names.length);
    const boxes = await cards.evaluateAll(elements => elements.map(e => {
      const card = e.getBoundingClientRect(), image = e.querySelector('.diagram-canvas').getBoundingClientRect(), text = e.querySelector('.preset-description').getBoundingClientRect();
      return { x: card.x, y: card.y, bottom: card.bottom, imageRight: image.right, textLeft: text.left };
    }));
    assert.ok(boxes.every((b, i) => b.textLeft > b.imageRight && (i === 0 || b.y >= boxes[i - 1].bottom)), 'samples are rows with image left of text');
    await cards.last().click();
    assert.equal(await cards.last().getAttribute('aria-checked'), 'true');
  }
  await page.getByRole('combobox', { name: '다이어그램 종류', exact: true }).selectOption('flowchart');
  await captureWidths(page, fixture, 'natural');
  await page.getByRole('button', { name: '형식 추가', exact: true }).click();
  await captureWidths(page, fixture, 'natural-multiple', [390]);

  const commits = Array.from({ length: 61 }, (_, i) => ({ sha: (i + 1).toString(16).padStart(40, 'a'), parentShas: ['b'.repeat(40)],
    authoredAt: '2026-09-12T03:00:00Z', authorName: '합성 작성자', authorEmail: 'synthetic@example.invalid',
    message: `${i + 1}. 저장소 변경 분석과 긴 커밋 메시지 표시를 확인하는 합성 변경 내역` }));
  const repositories = ['first', 'second', 'empty', 'error'].map(id => ({ id, name: `합성 저장소 ${id}`, defaultBranch: 'main', localPath: 'synthetic', allowedRoles: [], createdAt: commits[0].authoredAt, analysisRules: { revision: 0, indirectCalls: [] } }));
  const selectedPreset = presets.find(p => p.type === 'flowchart' && p.detailLevel === 'balanced');
  const selection = { id: 'view-1', diagramType: 'flowchart', presetId: selectedPreset.id };
  const candidates = Array.from({ length: 4 }, (_, i) => ({ id: `change-${i}`, qualifiedName: `Example.OrderProcessingService.ValidateAndSaveOrder${i}`, changeType: 'Modified', filePath: 'synthetic/OrderProcessingService.cs', startLine: i + 1, callerCount: 2, calleeCount: 3 }));
  const groups = ['주문 요청 검증 및 저장 처리 그룹', '보고서 생성 및 응답 구성 그룹'].map((title, i) => ({ id: `group-${i}`, title, changeIds: candidates.slice(i * 2, i * 2 + 2).map(c => c.id), diagramType: 'flowchart', presetId: selectedPreset.id, views: [{ ...selection, id: `view-${i}` }] }));
  const plan = { id: 'ui-plan', revision: 1, request: { repositoryId: 'first', targetRevision: commits[0].sha, useLlmGrouping: false, enableThinking: false }, state: 'Ready', progress: 100,
    stageMessage: '합성 UI 검증', candidates, selections: groups, suggestedGroups: [], warnings: [], createdAt: commits[0].authoredAt, updatedAt: commits[0].authoredAt };
  const diagram = { id: 'ui-artifact', type: 'flowchart', version: 1, mermaidDsl: 'flowchart LR\na["요청 검증"] --> b["주문 저장"]', createdAt: commits[0].authoredAt,
    ir: { type: 'flowchart', title: '합성 주문 처리', notes: [], nodes: [{ id: 'a', label: '요청 검증', kind: 'process', status: 'unchanged', confidence: 'high', evidenceIds: [] }, { id: 'b', label: '주문 저장', kind: 'process', status: 'unchanged', confidence: 'high', evidenceIds: [] }], edges: [{ id: 'edge', sourceId: 'a', targetId: 'b', type: 'calls', label: '', status: 'unchanged', confidence: 'high', evidenceIds: [] }] } };
  const narrative = { summary: '선택한 주문 처리 코드의 변경과 호출 관계를 확인합니다.', intent: '합성 화면 검증', warnings: [], risks: [] };
  const analysis = { id: 'ui-analysis', state: 'Completed', progress: 100, stageMessage: '완료', baseSha: 'b'.repeat(40), targetSha: commits[0].sha,
    result: { changedFiles: [], narrative, diagrams: [diagram], diagramGroups: groups.map(g => ({ groupId: g.id, title: g.title, changeIds: g.changeIds, narrative, warnings: [], views: [{ viewId: g.views[0].id, selection: g.views[0], diagram, warnings: [], state: 'Completed', reused: false }] })) } };
  let delayedResolve, delayedSearch, signalResolve, signalSearch;
  const resolveSeen = new Promise(resolve => { signalResolve = resolve; });
  const searchSeen = new Promise(resolve => { signalSearch = resolve; });
  const calls = [];
  const routePattern = '**/api/v1/**';
  const handler = async route => {
    const url = new URL(route.request().url());
    const p = url.pathname;
    if (p === '/api/v1/repositories') return route.fulfill({ json: repositories });
    if (/\/commits\/resolve$/.test(p)) {
      if (url.searchParams.get('revision') === 'deadbeef') { delayedResolve = route; signalResolve(); return; }
      return route.fulfill({ json: commits[59] });
    }
    if (/\/commits$/.test(p)) {
      calls.push(url.search);
      if (p.includes('/error/')) return route.fulfill({ status: 503, json: { error: '합성 목록 오류' } });
      if (p.includes('/empty/')) return route.fulfill({ json: [] });
      const query = url.searchParams.get('query');
      if (query === 'delayed') { delayedSearch = route; signalSearch(); return; }
      const items = query ? commits.filter(c => c.message.includes(query)) : p.includes('/second/') ? [commits[60]] : commits;
      const skip = Number(url.searchParams.get('skip'));
      return route.fulfill({ json: items.slice(skip, skip + Number(url.searchParams.get('limit'))) });
    }
    if (p === '/api/v1/analysis-plans') return route.fulfill({ json: route.request().method() === 'POST' ? plan : [] });
    if (p.endsWith('/ui-plan/selection')) { plan.selections = route.request().postDataJSON().groups; plan.revision++; return route.fulfill({ json: plan }); }
    if (p.endsWith('/ui-plan/generate')) return route.fulfill({ json: analysis });
    if (p.endsWith('/ui-plan/analyses')) return route.fulfill({ json: [{ id: analysis.id, createdAt: commits[0].authoredAt, state: 'Completed', hasResult: true, totalGroups: 2, successfulGroups: 2, totalViews: 2, successfulViews: 2 }] });
    if (p.includes('/analyses/ui-analysis')) return route.fulfill({ json: p.includes('/pages/') ? diagram : analysis });
    if (p.includes('/diagram-artifacts/ui-artifact/revisions')) return route.fulfill({ json: [] });
    return route.fallback();
  };
  await page.route(routePattern, handler);
  try {
    await page.reload();
    await page.getByRole('button', { name: 'Git 변경 분석', exact: true }).click();
    const repository = page.getByRole('combobox', { name: '저장소', exact: true });
    await repository.selectOption('first');
    const trigger = page.getByRole('button', { name: 'Target 커밋 선택', exact: true });
    await trigger.getByText(commits[0].message, { exact: true }).waitFor();
    assert.equal(await page.getByRole('listbox').count(), 0, 'initially collapsed');
    await trigger.click();
    const popup = page.getByRole('dialog', { name: 'Target 커밋 찾기' });
    const options = popup.getByRole('option');
    assert.equal(await options.count(), 50);
    assert.ok((await popup.locator('.commit-results').boundingBox()).height <= 301);
    await captureWidths(page, fixture, 'git-commits-open', [1440, 390]);
    await options.nth(2).click();
    assert.equal(await popup.count(), 0);
    await trigger.getByText(commits[2].message, { exact: true }).waitFor();
    await trigger.click();
    await popup.getByRole('textbox', { name: '메시지, SHA 또는 작성자 검색' }).press('ArrowDown');
    await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
    await trigger.getByText(commits[1].message, { exact: true }).waitFor();
    assert.equal(await popup.count(), 0);
    await trigger.click(); await page.keyboard.press('Escape');
    assert.equal(await trigger.evaluate(e => e === document.activeElement), true);
    await trigger.click(); await page.locator('h1').click();
    assert.equal(await popup.count(), 0);
    await trigger.click();
    await popup.getByRole('button', { name: '이전 커밋 50개 더 보기' }).click();
    await options.nth(60).waitFor();
    await popup.getByRole('textbox', { name: '메시지, SHA 또는 작성자 검색' }).fill('61.');
    await page.waitForFunction(() => document.querySelectorAll('.commit-results [role="option"]').length === 1);
    await options.first().click();
    await trigger.click();
    await popup.locator('summary').click();
    await popup.getByRole('textbox', { name: 'Target 커밋 SHA 직접 입력' }).fill('invalid');
    await popup.getByRole('button', { name: 'SHA 확인', exact: true }).click();
    await popup.getByRole('alert').getByText(/7~64자리/).waitFor();
    await popup.getByRole('textbox', { name: 'Target 커밋 SHA 직접 입력' }).fill('abcdef1');
    await popup.getByRole('button', { name: 'SHA 확인', exact: true }).click();
    await trigger.getByText(commits[59].message, { exact: true }).waitFor();
    assert.equal(await popup.count(), 0);
    await trigger.click(); await popup.locator('summary').click();
    await popup.getByRole('textbox', { name: 'Target 커밋 SHA 직접 입력' }).fill('deadbeef');
    await popup.getByRole('button', { name: 'SHA 확인', exact: true }).click();
    await resolveSeen;
    assert.ok(delayedResolve);
    await repository.selectOption('second');
    await trigger.getByText(commits[60].message, { exact: true }).waitFor();
    await delayedResolve.fulfill({ json: commits[3] });
    await trigger.click();
    await popup.getByRole('textbox', { name: '메시지, SHA 또는 작성자 검색' }).fill('delayed');
    await searchSeen;
    await repository.selectOption('first');
    await trigger.getByText(commits[0].message, { exact: true }).waitFor();
    await delayedSearch.fulfill({ json: [commits[4]] });
    await trigger.click();
    assert.equal(await options.count(), 50);
    await page.keyboard.press('Escape');
    await repository.selectOption('empty'); await trigger.click();
    await popup.getByText('표시할 커밋이 없습니다.').waitFor();
    await repository.selectOption('error'); await trigger.click();
    await popup.getByRole('alert').getByText('합성 목록 오류').waitFor();
    await repository.selectOption('first');
    await trigger.getByText(commits[0].message, { exact: true }).waitFor();
    await page.getByLabel('Base 커밋 직접 지정').check();
    await page.getByRole('button', { name: 'Base 커밋 선택' }).click();
    await page.getByRole('dialog', { name: 'Base 커밋 찾기' }).getByRole('option').nth(1).click();
    assert.equal(await page.getByRole('dialog').count(), 0);
    await captureWidths(page, fixture, 'git-selection');
    await page.getByRole('button', { name: '변경점 사전 분석', exact: true }).click();
    await page.locator('.plan-editor .candidate-row').last().waitFor();
    await captureWidths(page, fixture, 'git-grouping');
    await page.locator('.candidate-row').first().getByRole('checkbox').uncheck();
    await page.locator('.candidate-row').first().getByRole('checkbox').check();
    await page.getByRole('button', { name: '선택대로 다이어그램 생성', exact: true }).click();
    await page.locator('.analysis-result:visible svg:visible').first().waitFor();
    await captureWidths(page, fixture, 'git-results');
    await page.getByRole('navigation', { name: '생성 결과 그룹 선택' }).getByRole('button').last().click();
    assert.equal(await page.getByRole('navigation', { name: '생성 결과 그룹 선택' }).locator('[aria-pressed="true"]').count(), 1);
    assert.ok(calls.some(q => q.includes('skip=50')) && calls.some(q => q.includes('query=61.')));
  } catch (error) {
    await page.screenshot({ path: path.join(fixture, 'interaction-failure.png'), fullPage: true });
    await writeFile(path.join(fixture, 'interaction-failure.json'), JSON.stringify({ calls, text: await page.locator('body').innerText() }, null, 2));
    throw error;
  } finally {
    await delayedResolve?.abort().catch(() => {});
    await delayedSearch?.abort().catch(() => {});
    await page.unroute(routePattern, handler);
    await page.goto(origin);
  }
}
