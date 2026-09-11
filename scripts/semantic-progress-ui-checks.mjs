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
      recentFailures: [
        { id: 'generation-failure', stage: 'llm-SharedSemanticResponse', state: 'Failed', sent: true,
          errorCode: 'LLM_SCHEMA_INVALID', validationCode: 'SharedTextNotKorean', purpose: 'generation',
          inputCharacters: 34000, inputCharacterLimit: 56500,
          validationDetails: { expectedItems: 40, receivedItems: 40, missingItems: 0, duplicateItems: 0,
            unknownItems: 0, field: 'items.summary', itemIndex: 0, actualLength: 80, allowedLength: 80 } },
        { id: 'repair-limit', stage: 'llm-SharedSemanticResponse', state: 'Failed', sent: false,
          errorCode: 'LLM_INPUT_CHARACTERS', purpose: 'repair', inputCharacters: 60700, inputCharacterLimit: 56500 },
      ],
    } });
  const pattern = /\/api\/v1\/(code-block-runs|code-block-workspaces)\//;
  const routeHandler = async route => {
    const url = new URL(route.request().url());
    if (url.pathname === runPath) return route.fulfill({ json: snapshot() });
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
    assert.match(await progress.innerText(), /전체: 완료 12단위.*실제 전송 17회/);
    assert.match(await progress.innerText(), /이번 실행 3: 새 완료 4단위.*실제 전송 6회/);
    await progress.getByText('5분이 지났습니다.', { exact: false }).waitFor();
    const failures = progress.getByLabel('요청 오류 진단', { exact: true });
    assert.match(await failures.innerText(), /최초 기록.*의미 생성.*전송 후/);
    assert.match(await failures.innerText(), /후속 기록.*응답 수정.*전송 전/);
    assert.match(await failures.innerText(), /60,700 \/ 허용 56,500자/);
    assert.match(await failures.innerText(), /SharedTextNotKorean/);
    assert.equal(await workspace.locator('.structured-diagram-editor').count(), 0);
    assert.equal(pageRequests, 0, 'Running results must not fetch pages');
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: 1000 });
      await progress.screenshot({ path: path.join(fixture, `semantic-progress-${width}.png`) });
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1));
    }
    state = 'Partial';
    await workspace.getByRole('button', { name: '부분 결과 열기', exact: true }).waitFor();
    assert.equal(pageRequests, 0, 'Stopped results remain closed until requested');
    await workspace.getByRole('button', { name: '부분 결과 열기', exact: true }).click();
    await workspace.getByLabel('정적 구조 보기', { exact: true }).waitFor();
    await workspace.screenshot({ path: path.join(fixture, 'semantic-partial-open.png') });
  } finally {
    await page.unroute(pattern, routeHandler); page.off('request', onRequest);
    await page.setViewportSize({ width: 1440, height: 1000 });
  }
}
