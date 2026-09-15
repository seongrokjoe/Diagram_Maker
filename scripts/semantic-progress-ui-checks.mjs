import assert from 'node:assert/strict';
import path from 'node:path';

export async function checkSemanticProgress({ page, fixture, run }) {
  const runPath = `/api/v1/code-block-runs/${run.id}`;
  const historyPath = `/api/v1/code-block-workspaces/${run.workspaceId}/runs`;
  let state = 'Generating';
  const snapshot = () => ({ ...run, state, stopReason: state === 'Partial' ? 'budget' : null,
    stageMessage: '합성 진행 상태', canResume: state === 'Partial', execution: {
      stage: 'llm-SharedSemanticResponse', completedUnits: 12, reusedUnits: 8, requests: 15,
      elapsedSeconds: 326, budgetSeconds: 900, totalElapsedSeconds: 926, attemptNumber: 3,
      attemptCompletedUnits: 4, attemptRequests: 5, transportRequests: 17, attemptTransportRequests: 6,
      waitMilliseconds: 20000, attemptWaitMilliseconds: 5000,
      protocolUpgraded: true,
      lastProgressAt: '2026-09-14T00:00:00Z', coverage: { totalUnits: 24, verifiedUnits: 12, pendingUnits: 12, failedUnits: 1 },
      lastRequest: { id: 'review-invalid', stage: 'llm-SharedSemanticReview', sent: true, purpose: 'review',
        outputLimit: 2000, completionTokens: 412, finishReason: 'stop', outputMode: 'structured_outputs', schemaRelaxed: true },
      recentFailures: [
        { id: 'generation-failure', stage: 'llm-SharedSemanticResponse', state: 'Failed', sent: true,
          errorCode: 'LLM_SCHEMA_INVALID', validationCode: 'SharedUnknownIds', purpose: 'generation',
          recoveryGroupId: 'generation-batch', recoveryState: 'Recovered', attempt: 1,
          inputCharacters: 34000, inputCharacterLimit: 56500,
          validationDetails: { expectedItems: 40, receivedItems: 40, missingItems: 0, duplicateItems: 0,
            unknownItems: 1, nonTargetItems: 1, unknownAliases: 0 } },
        { id: 'repair-limit', stage: 'llm-SharedSemanticResponse', state: 'Failed', sent: false,
          errorCode: 'LLM_INPUT_CHARACTERS', purpose: 'repair', inputCharacters: 60700, inputCharacterLimit: 56500 },
        { id: 'review-length', stage: 'llm-SharedSemanticReview', sent: true, purpose: 'review',
          errorCode: 'LLM_RESPONSE_TRUNCATED', outputLimit: 2000, completionTokens: 2000, finishReason: 'length', outputMode: 'structured_outputs',
          recoveryGroupId: 'review-parent', parentGroupId: 'generation-batch', recoveryState: 'Recovered', attempt: 1 },
        { id: 'review-rejected', stage: 'llm-SharedSemanticReview', sent: true, purpose: 'review',
          errorCode: 'LLM_SEMANTIC_REVIEW', validationCode: 'SemanticReviewRejected', outputLimit: 2000, completionTokens: 120,
          recoveryGroupId: 'review-left', parentGroupId: 'review-parent', recoveryState: 'Exhausted', attempt: 1,
          validationDetails: { expectedItems: 4, receivedItems: 4, missingItems: 0, duplicateItems: 0, unknownItems: 0, issueCodes: ['reversed_condition'] } },
        { id: 'review-invalid', stage: 'llm-SharedSemanticReview', sent: true, purpose: 'review',
          errorCode: 'LLM_SCHEMA_INVALID', validationCode: 'InvalidReview', outputLimit: 2000, completionTokens: 412,
          recoveryGroupId: 'review-right', parentGroupId: 'review-parent', recoveryState: 'Retrying', attempt: 2 },
        { id: 'http-error', stage: 'llm-SharedSemanticReview', state: 'Failed', sent: true, purpose: 'review',
          errorCode: 'LLM_HTTP_400', httpStatus: 400, serverErrorCategory: 'schema-constraint', outputMode: 'structured_outputs',
          recoveryGroupId: 'review-server', parentGroupId: 'generation-batch', recoveryState: 'RequiresAction', attempt: 1 },
        ...Array.from({ length: 8 }, (_, i) => ({ id: 'older-error-' + i, stage: 'llm-SharedSemanticReview',
          sent: true, purpose: 'review', errorCode: 'LLM_RESPONSE_TRUNCATED', recoveryState: 'Recovered',
          startedAt: '2026-09-14T00:00:00Z', outputLimit: 2000 })),
      ],
    } });
  const pattern = /\/api\/v1\/(code-block-runs|code-block-workspaces)\//;
  const routeHandler = async route => {
    const url = new URL(route.request().url());
    if (url.pathname === runPath) return route.fulfill({ json: snapshot() });
    if (url.pathname === runPath + '/diagnostics') return route.fulfill({ json: { diagnostics: snapshot().execution.recentFailures } });
    if (url.pathname === historyPath && route.request().method() === 'GET') {
      const response = await route.fetch();
      const history = await response.json();
      return route.fulfill({ response, json: history.map(item => item.id === run.id ? snapshot() : item) });
    }
    return route.fallback();
  };
  let pageRequests = 0;
  const onRequest = request => { if (request.url().includes(runPath + '/groups/')) pageRequests++; };
  await page.route(pattern, routeHandler); page.on('request', onRequest);
  try {
    await page.reload();
    const workspace = page.locator('.code-block-workspace');
    await workspace.getByText('모든 상세 페이지를 완성한 뒤 결과를 표시합니다. 완료된 작업은 계속 저장됩니다.', { exact: true }).waitFor();
    const progress = workspace.getByLabel('전체 및 이번 실행 진척', { exact: true });
    assert.match(await progress.innerText(), /LLM 실제 전송 17회 · 이번 실행 6회/);
    await progress.getByText('5분이 지났습니다.', { exact: false }).waitFor();
    assert.match(await progress.innerText(), /의미 설명 검토 12 \/ 24개/);
    assert.equal(await progress.locator('time').getAttribute('datetime'), '2026-09-14T00:00:00Z');
    const announcement = await progress.getByRole('status').innerText();
    await page.waitForTimeout(1100);
    assert.equal(await progress.getByRole('status').innerText(), announcement, 'Clock ticks must not update the live announcement');
    const failures = progress.getByLabel('요청 오류 진단', { exact: true });
    await failures.locator('tbody tr').last().waitFor();
    assert.equal(await failures.locator('tbody tr').count(), 14, 'each failed request has its own persistent row');
    assert.ok(await failures.getByLabel('오류 목록 스크롤', { exact: true }).evaluate(e => e.scrollHeight > e.clientHeight && e.clientHeight <= 287), 'a bounded scroll area retains older errors');
    const detail = failures.getByLabel('선택 오류 상세', { exact: true });
    assert.ok(await detail.evaluate(e => e.clientHeight <= 287), 'details have the same bounded height');
    assert.match(await detail.innerText(), /복구 완료/);
    await detail.locator('summary').click();
    assert.match(await detail.innerText(), /SharedUnknownIds/);
    await failures.locator('tbody tr button').nth(1).click();
    await failures.locator(':scope > summary').click();
    assert.equal(await failures.getAttribute('open'), null);
    await failures.locator(':scope > summary').click();
    assert.equal(await failures.locator('tbody tr.selected').getAttribute('aria-selected'), 'true', 'selection survives collapse');
    assert.match(await detail.innerText(), /전송 전 검사/);
    assert.match(await detail.innerText(), /60,700자 \/ 56,500/);
    await failures.locator('tbody tr button').nth(2).click();
    assert.match(await detail.innerText(), /출력 한도 2,000토큰 · 사용 2,000토큰/);
    await failures.locator('tbody tr button').nth(3).click();
    assert.match(await detail.innerText(), /조건 반전/);
    assert.match(await detail.innerText(), /복구 실패/);
    await failures.locator('tbody tr button').nth(4).click();
    assert.match(await detail.innerText(), /승인 값과 문제 목록이 모순/);
    await failures.locator('tbody tr button').nth(5).click();
    assert.match(await detail.innerText(), /HTTP 400/);
    assert.match(await detail.innerText(), /설정 확인 필요/);
    assert.match(await detail.innerText(), /JSON 스키마 제약/);
    await progress.getByText('실행 기술 정보', { exact: true }).click();
    assert.match(await progress.innerText(), /전체 내부 처리 완료 12단위 · 이번 실행 새 완료 4단위 · 재사용 8단위/);
    assert.match(await progress.innerText(), /호환 스키마/);
    assert.match(await progress.innerText(), /정책이 갱신/);
    assert.equal(await workspace.locator('.structured-diagram-editor').count(), 0);
    assert.equal(pageRequests, 0, 'Running results must not fetch pages');
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 1000 });
      if (width === 390) assert.ok(await failures.getByLabel('오류 목록 스크롤', { exact: true }).evaluate(e =>
        e.scrollWidth > e.clientWidth && getComputedStyle(e.querySelector('tbody td:nth-child(2)')).whiteSpace === 'nowrap'),
      'small screens scroll the error table without breaking stage labels into single characters');
      await progress.screenshot({ path: path.join(fixture, `semantic-progress-${width}.png`) });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
    }
    state = 'Partial';
    await workspace.getByRole('button', { name: '부분 결과 열기', exact: true }).waitFor();
    assert.equal(pageRequests, 0, 'Stopped results remain closed until requested');
    await workspace.getByRole('button', { name: '부분 결과 열기', exact: true }).click();
    await workspace.getByLabel('Code 다이어그램 보기 (정적 구조)', { exact: true }).waitFor();
    await workspace.screenshot({ path: path.join(fixture, 'semantic-partial-open.png') });
  } finally {
    await page.unroute(pattern, routeHandler); page.off('request', onRequest);
    await page.setViewportSize({ width: 1440, height: 1000 });
  }
}
