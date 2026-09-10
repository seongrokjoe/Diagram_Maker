// Optional local Windows/Edge regression test. All code is synthetic and LLM is disabled.
// Pass an unpacked directory under artifacts/stage to test the packaged EXE, Node and UI.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { cp, mkdir, mkdtemp, readFile, realpath, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';
import { checkSemanticProgress } from './semantic-progress-ui-checks.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
await mkdir(path.join(root, 'artifacts'), { recursive: true });
const fixture = await mkdtemp(path.join(root, 'artifacts/code-block-ui-'));
const policy = path.join(fixture, 'disabled-llm.json');
await writeFile(policy, JSON.stringify({ Llm: { Enabled: false, AllowDevelopmentStub: false } }));
const networkPolicy = path.join(fixture, 'test-network-policy.json');
await writeFile(networkPolicy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
const packageRoot = process.argv[2] ? await realpath(path.resolve(root, process.argv[2])) : null;
if (packageRoot) {
  const relativePackage = path.relative(await realpath(path.join(root, 'artifacts/stage')), packageRoot);
  assert.ok(relativePackage && relativePackage !== '..' && !relativePackage.startsWith('..' + path.sep) && !path.isAbsolute(relativePackage),
    'The resolved package directory must stay under artifacts/stage');
}
const outputPath = process.env.CODE_BLOCK_WEB_DIST ?? path.join(root, 'artifacts/verify-web-dist');
const runtime = packageRoot ?? path.join(fixture, 'api');
if (!packageRoot) {
  await cp(process.env.CODE_BLOCK_API_DIST ?? path.join(root, 'src/DiagramMaker.Api/bin/Release/net9.0'), runtime, { recursive: true });
  await cp(outputPath, path.join(runtime, 'wwwroot'), { recursive: true });
}
const server = spawn(packageRoot ? path.join(runtime, 'DiagramMaker.Api.exe') : 'dotnet',
  [...(packageRoot ? [] : [path.join(runtime, 'DiagramMaker.Api.dll')]), '--urls', 'http://127.0.0.1:0'], {
  cwd: runtime, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development', Storage__Provider: 'LocalFile',
    Storage__LocalFilePath: path.join(fixture, 'store.json'), Llm__Enabled: 'false', CodexTest__Enabled: 'false',
    DIAGRAMMAKER_LLM_POLICY_PATH: policy, DIAGRAMMAKER_NETWORK_POLICY_PATH: networkPolicy,
    GitWorker__ScriptPath: path.join(packageRoot ?? root, 'tools/git-worker/index.mjs'),
    ...(packageRoot ? { GitWorker__NodeExecutable: path.join(packageRoot, 'runtime/node/node.exe') } : {}) },
});
let output = '', browser, page;
server.stdout.on('data', data => { output += data; }); server.stderr.on('data', data => { output += data; });
try {
  let origin;
  for (let i = 0; i < 100; i++) { origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1]; if (origin) break; await delay(100); }
  assert.ok(origin, output);
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  let generationRequests = 0;
  page.on('request', request => {
    if (request.method() === 'POST' && /code-block-workspaces\/[^/]+\/runs$/.test(request.url())) generationRequests++;
  });
  await page.goto(origin);
  await page.getByRole('button', { name: '코드 블럭 다이어그램', exact: true }).click();
  const workspace = page.locator('.code-block-workspace');
  const blockEditor = workspace.locator('.code-block-card-body:visible');
  const groupList = workspace.locator('[aria-label="그룹 목록"]');
  assert.deepEqual(await page.getByRole('navigation', { name: '주요 기능' }).getByRole('button').allTextContents(),
    ['자연어 다이어그램', '코드 블럭 다이어그램', 'Git 변경 분석', '저장소 관리', 'LLM 점검']);
  assert.equal(await groupList.getByRole('button').count(), 1);
  assert.equal(await workspace.locator('.code-block-card').count(), 1);
  const titleInput = workspace.getByLabel('작업 제목', { exact: true });
  assert.equal(await titleInput.inputValue(), '');
  assert.equal(await titleInput.getAttribute('placeholder'), '코드 블럭 다이어그램');
  await workspace.getByRole('button', { name: '초안 저장', exact: true }).click();
  await workspace.getByRole('alert').getByText('작업 제목을 입력하세요.', { exact: true }).waitFor();
  await titleInput.evaluate(e => { if (document.activeElement !== e) throw new Error('required title receives focus'); });
  await titleInput.fill('한'); assert.equal(await titleInput.inputValue(), '한');
  const composeTab = workspace.getByRole('tab', { name: '코드 구성', exact: true });
  await composeTab.focus(); await page.keyboard.press('ArrowRight');
  assert.equal(await workspace.getByRole('tab', { name: '다이어그램 결과', exact: true }).getAttribute('aria-selected'), 'true');
  await page.keyboard.press('Home');
  assert.equal(await composeTab.getAttribute('aria-selected'), 'true');
  assert.equal(await composeTab.evaluate(e => getComputedStyle(e).borderBottomWidth), '3px');
  assert.equal(generationRequests, 0);
  const code = 'int save(int n){return n;}\nint run(){if(ready) return save(1); return 0;}';
  await workspace.getByLabel('작업 제목', { exact: true }).fill('합성 UI 검증');
  await workspace.getByLabel('코드', { exact: true }).fill(code);
  await page.getByRole('button', { name: '자연어 다이어그램', exact: true }).click();
  await page.getByRole('button', { name: '코드 블럭 다이어그램', exact: true }).click();
  assert.equal(await workspace.getByLabel('코드', { exact: true }).inputValue(), code, 'tab switch retains draft');
  await workspace.getByRole('button', { name: '+ 블럭 추가', exact: true }).click();
  assert.equal(await blockEditor.count(), 1, 'only the selected block editor is expanded');
  assert.equal(await groupList.getByRole('button').count(), 1, 'a block is added to the selected group');
  await blockEditor.getByLabel('코드', { exact: true }).fill('int unrelated(){return 2;}');
  await workspace.getByLabel('Thinking', { exact: true }).check();
  await workspace.getByLabel('사용자 관계 추가', { exact: true }).check();
  await workspace.getByRole('button', { name: '관계 추가', exact: true }).click();
  await workspace.getByLabel('관계 설명', { exact: true }).fill('그룹 옵션 검증');
  await workspace.getByLabel('사용자 관계 추가', { exact: true }).uncheck();
  await workspace.getByRole('button', { name: '+ 그룹 추가', exact: true }).click();
  assert.equal(await workspace.getByLabel('Thinking', { exact: true }).isChecked(), false);
  assert.equal(await workspace.getByLabel('사용자 관계 추가', { exact: true }).isChecked(), false);
  assert.equal(await blockEditor.count(), 0);
  const emptySaved = page.waitForResponse(r => r.url().endsWith('/code-block-workspaces') && r.request().method() === 'POST');
  await workspace.getByRole('button', { name: '초안 저장', exact: true }).click();
  const emptyRecord = await (await emptySaved).json();
  assert.equal(emptyRecord.input.groups.length, 2);
  assert.equal(emptyRecord.input.groups[1].blockIds.length, 0);
  assert.equal(emptyRecord.input.groups[0].enableThinking, true);
  assert.equal(emptyRecord.input.groups[0].enableUserRelations, false);
  assert.equal(emptyRecord.input.relations[0].description, '그룹 옵션 검증');
  await groupList.getByRole('button').first().click();
  assert.equal(await blockEditor.getByLabel('코드', { exact: true }).inputValue(), 'int unrelated(){return 2;}', 'group returns to its expanded card');
  await blockEditor.getByLabel('블럭 그룹 이동').selectOption({ label: '그룹 2' });
  await groupList.getByRole('button').first().click();
  await workspace.getByLabel('사용자 관계 추가', { exact: true }).check();
  await workspace.getByText(/적용 제외 · 도착 블럭이 다른 그룹/).waitFor();
  assert.equal(await workspace.getByLabel('도착 블럭').locator('option:not([disabled])').count(), 1);
  await workspace.getByLabel('사용자 관계 추가', { exact: true }).uncheck();
  await groupList.getByRole('button').last().click();
  await blockEditor.getByLabel('블럭 그룹 이동').selectOption({ label: '그룹 1' });
  assert.equal(await groupList.getByRole('button').count(), 2, 'moving the last block preserves the empty group');
  for (const width of [1440, 800, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    assert.equal(await workspace.locator('[aria-label="전체 블럭 목록"]').count(), 0);
    await workspace.screenshot({ path: path.join(fixture, `compose-${width}.png`) });
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'composition has no page overflow');
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  const generateButton = workspace.getByRole('button', { name: '다이어그램 생성', exact: true });
  const generateSize = await generateButton.boundingBox();
  assert.ok(generateSize.width >= 220 && generateSize.height >= 52);
  await workspace.getByRole('button', { name: '블럭 2 위로', exact: true }).click();
  assert.equal(await blockEditor.getByLabel('코드', { exact: true }).inputValue(), 'int unrelated(){return 2;}');
  await workspace.getByRole('button', { name: '블럭 삭제', exact: true }).first().click();
  for (const type of ['flowchart', 'sequence', 'code-relation']) await workspace.getByLabel('종류 추가', { exact: true }).selectOption(type);
  const generation = page.waitForResponse(r => /code-block-workspaces\/[^/]+\/runs$/.test(r.url()) && r.request().method() === 'POST');
  await generateButton.click();
  const generated = await generation; assert.equal(generated.status(), 202);
  await workspace.getByText('의미 생성 미완료 · 실패 사유와 정적 구조를 확인하세요', { exact: true }).waitFor({ timeout: 30000 });
  const editor = workspace.locator('.structured-diagram-editor');
  assert.equal(await editor.count(), 0, 'an incomplete run does not open static pages as successful results');
  await workspace.getByRole('button', { name: '정적 구조 열기', exact: true }).click();
  await editor.locator('svg').first().waitFor();
  await editor.getByText('동작 설명과 원본 근거', { exact: true }).click();
  assert.ok(await editor.getByText('의미 설명 미완료', { exact: true }).isVisible());
  assert.equal(await editor.getByText('핵심 변경', { exact: true }).count(), 0, 'pasted code does not show Git change language');
  await editor.getByRole('button', { name: '확대', exact: true }).click();
  await editor.getByText('110%', { exact: true }).waitFor();
  await editor.getByRole('button', { name: '맞춤 보기', exact: true }).click();
  const body = await (await page.request.get(`${origin}/api/v1/code-block-runs/${(await generated.json()).id}`)).json();
  const tree = workspace.getByRole('tree', { name: '다이어그램 결과 트리', exact: true });
  const firstType = tree.getByRole('treeitem').first();
  await firstType.focus(); await page.keyboard.press('ArrowLeft');
  assert.equal(await firstType.getAttribute('aria-expanded'), 'false');
  await page.keyboard.press('ArrowRight');
  assert.equal(await firstType.getAttribute('aria-expanded'), 'true');
  await page.keyboard.press('ArrowDown'); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
  assert.equal(await tree.locator('[aria-selected="true"]').count(), 1, 'keyboard selects a result page');
  await page.keyboard.press('ArrowLeft');
  assert.equal(await tree.locator(':focus').getAttribute('aria-level'), '2', 'left arrow moves to the parent');
  await page.keyboard.press(' ');
  assert.equal(await tree.locator(':focus').getAttribute('aria-expanded'), 'false', 'space collapses a group');
  await page.keyboard.press('ArrowRight'); await page.keyboard.press('ArrowRight');
  assert.equal(await tree.locator(':focus').getAttribute('aria-level'), '3', 'right arrow opens and enters a group');
  await page.keyboard.press('End');
  assert.equal(await tree.locator(':focus').getAttribute('data-row-id'), await tree.getByRole('treeitem').last().getAttribute('data-row-id'));
  await page.keyboard.press('ArrowUp');
  assert.notEqual(await tree.locator(':focus').getAttribute('data-row-id'), await tree.getByRole('treeitem').last().getAttribute('data-row-id'));
  await page.keyboard.press('Home');
  assert.equal(await firstType.evaluate(element => element === document.activeElement), true, 'Home returns to the first item');
  assert.equal(await tree.locator('[tabindex="0"]').count(), 1, 'tree has one keyboard entry point');
  for (const view of body.results[0].views) {
    const artifact = await (await page.request.get(`${origin}/api/v1/code-block-runs/${body.id}/groups/${body.results[0].groupId}/views/${view.viewId}/pages/${view.pages[0].id}`)).json();
    if (view.selection.diagramType === 'flowchart') assert.ok(artifact.ir.edges.some(e => e.type === 'calls'), 'same-line C++ return call remains connected in Flow');
    await workspace.locator(`[role="treeitem"][data-row-id="${view.viewId}:${view.pages[0].id}"]`).click();
    await page.waitForFunction(dsl => document.querySelector('.code-block-workspace .structured-diagram-editor details pre')?.textContent === dsl, artifact.mermaidDsl);
    await editor.locator('svg').first().waitFor();
    await editor.screenshot({ path: path.join(fixture, `${view.selection.diagramType}.png`) });
  }
  await editor.locator('.code-block-explanation > summary').click();
  await editor.locator('.evidence-browser > summary').click();
  await editor.locator('.evidence-item').first().click();
  const evidence = workspace.getByRole('region', { name: '코드 근거', exact: true });
  await evidence.waitFor();
  assert.ok(code.includes(await evidence.locator('pre').innerText()), 'evidence shows the original pasted source');
  await evidence.getByRole('button', { name: '근거 닫기', exact: true }).click();
  await editor.getByRole('button', { name: '구조 편집', exact: true }).click();
  await editor.locator('.edge-edit-row input').first().fill('사용자 검토 관계');
  const saveResponse = page.waitForResponse(r => r.url().endsWith('/edits') && r.request().method() === 'POST');
  await editor.getByRole('button', { name: '새 리비전 저장', exact: true }).click();
  const saved = await saveResponse; assert.equal(saved.status(), 200);
  const revision = await saved.json();
  assert.equal(revision.diagram.ir.edges[0].relationOrigin, 'user');
  assert.deepEqual(revision.diagram.ir.edges[0].evidenceIds, []);
  await page.waitForFunction(dsl => document.querySelector('.code-block-workspace .structured-diagram-editor details pre')?.textContent === dsl, revision.diagram.mermaidDsl);
  for (const format of ['SVG', 'PNG']) {
    const downloading = page.waitForEvent('download');
    await editor.getByRole('button', { name: `${format} 다운로드`, exact: true }).click();
    const download = await downloading;
    const destination = path.join(fixture, `edited.${format.toLowerCase()}`);
    await download.saveAs(destination);
    const bytes = await readFile(destination);
    if (format === 'SVG') assert.ok(bytes.toString('utf8').replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').includes('사용자 제공: 사용자 검토 관계'));
    else assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  }
  const selectedUrl = new URL(page.url());
  await page.reload();
  await page.waitForFunction(expected => document.querySelector('.code-block-workspace textarea[aria-label="코드"]')?.value === expected, code);
  assert.equal(await workspace.getByLabel('코드', { exact: true }).inputValue(), code, 'saved draft restores after reload');
  await editor.locator('svg').first().waitFor();
  for (const name of ['codeWorkspace', 'codeRun', 'codeGroup', 'codeView', 'codePage', 'codeScreen'])
    assert.equal(new URL(page.url()).searchParams.get(name), selectedUrl.searchParams.get(name), `reload restores ${name}`);
  for (const width of [1440, 800, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    assert.ok(await workspace.getByRole('tree', { name: '다이어그램 결과 트리', exact: true }).isVisible());
    await workspace.screenshot({ path: path.join(fixture, `workspace-${width}.png`) });
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  const navigationRequests = generationRequests;
  await firstType.focus(); await page.keyboard.press('ArrowLeft');
  await workspace.getByRole('tab', { name: '코드 구성', exact: true }).click();
  await workspace.getByRole('tab', { name: '다이어그램 결과', exact: true }).click();
  assert.equal(await firstType.getAttribute('aria-expanded'), 'false', 'screen switches preserve collapsed branches');
  await page.reload();
  await editor.locator('svg').first().waitFor();
  assert.equal(await firstType.getAttribute('aria-expanded'), 'false', 'reload preserves non-selected collapsed branches');
  assert.equal(await tree.locator('[aria-selected="true"]').getAttribute('data-row-id'), `${selectedUrl.searchParams.get('codeView')}:${selectedUrl.searchParams.get('codePage')}`);
  assert.equal(generationRequests, navigationRequests, 'tree navigation, screen switches and reload do not generate');
  const regeneratedResponse = page.waitForResponse(r => /code-block-workspaces\/[^/]+\/runs$/.test(r.url()) && r.request().method() === 'POST');
  await workspace.getByRole('button', { name: '이 결과 재생성', exact: true }).click();
  const regenerated = await regeneratedResponse; assert.equal(regenerated.status(), 202);
  const regeneratedId = (await regenerated.json()).id;
  await workspace.getByText('의미 생성 미완료 · 실패 사유와 정적 구조를 확인하세요', { exact: true }).waitFor({ timeout: 30000 });
  await workspace.getByLabel('생성 이력', { exact: true }).selectOption(body.id);
  await workspace.getByLabel('정적 구조 보기', { exact: true }).check();
  await workspace.locator(`[role="treeitem"][data-row-id="${selectedUrl.searchParams.get('codeView')}:${selectedUrl.searchParams.get('codePage')}"]`).click();
  await page.waitForFunction(dsl => document.querySelector('.code-block-workspace .structured-diagram-editor details pre')?.textContent === dsl, revision.diagram.mermaidDsl);
  await page.reload();
  await editor.locator('svg').first().waitFor();
  assert.equal(await workspace.getByLabel('생성 이력', { exact: true }).inputValue(), body.id, 'reload keeps the selected older generation');
  assert.notEqual(new URL(page.url()).searchParams.get('codeRun'), regeneratedId);
  await page.waitForFunction(dsl => document.querySelector('.code-block-workspace .structured-diagram-editor details pre')?.textContent === dsl, revision.diagram.mermaidDsl);
  assert.equal(generationRequests, navigationRequests + 1, 'opening older results and revisions does not regenerate');
  await workspace.getByRole('button', { name: '새 작업', exact: true }).click();
  await workspace.getByLabel('작업 제목', { exact: true }).fill('합성 관계 질문');
  await workspace.getByLabel('블럭 제목', { exact: true }).fill('호출 블럭');
  await workspace.getByLabel('언어', { exact: true }).selectOption('csharp');
  await workspace.getByLabel('코드', { exact: true }).fill('void Run(){ Save(); }');
  await workspace.getByLabel('Thinking', { exact: true }).check();
  await workspace.getByRole('button', { name: '+ 그룹 추가', exact: true }).click();
  await workspace.getByRole('button', { name: '+ 블럭 추가', exact: true }).click();
  assert.equal(await workspace.getByLabel('언어', { exact: true }).inputValue(), 'csharp', 'new block inherits C#');
  await workspace.getByLabel('블럭 제목', { exact: true }).fill('저장 블럭');
  await workspace.getByLabel('코드', { exact: true }).fill('void Save(){}');
  await workspace.getByLabel('종류 추가', { exact: true }).selectOption('code-relation');
  await generateButton.click();
  await workspace.getByRole('heading', { name: '관계 확인', exact: true }).waitFor({ timeout: 30000 });
  await page.reload();
  await workspace.getByRole('heading', { name: '관계 확인', exact: true }).waitFor();
  const question = workspace.getByRole('combobox', { name: /호출은 어느 구현으로 연결됩니까/ });
  const target = await question.locator('option').last().getAttribute('value');
  await question.selectOption(target);
  await workspace.getByLabel('선택 대상과 그룹 합치기', { exact: true }).check();
  await workspace.getByRole('button', { name: '코드 위치 보기', exact: true }).click();
  await evidence.waitFor();
  assert.ok((await evidence.locator('pre').innerText()).includes('Save()'));
  await evidence.getByRole('button', { name: '근거 닫기', exact: true }).click();
  const answeredResponse = page.waitForResponse(r => r.url().endsWith('/answers') && r.request().method() === 'POST');
  await workspace.getByRole('button', { name: '답변 적용하고 생성', exact: true }).click();
  const answered = await answeredResponse; assert.equal(answered.status(), 202);
  const answeredId = (await answered.json()).id;
  await workspace.getByText('의미 생성 미완료 · 실패 사유와 정적 구조를 확인하세요', { exact: true }).waitFor({ timeout: 30000 });
  await workspace.getByRole('button', { name: '정적 구조 열기', exact: true }).click();
  await editor.locator('svg').first().waitFor();
  await page.waitForFunction(() => /사용자\s*제공:/.test(document.querySelector('.code-block-workspace .structured-diagram-editor svg')?.textContent ?? ''));
  const merged = await (await page.request.get(`${origin}/api/v1/code-block-runs/${answeredId}`)).json();
  assert.equal(merged.groups.length, 1, 'explicit answer merges the selected groups');
  assert.equal(merged.results[0].views[0].selection.diagramType, 'code-relation');
  await workspace.screenshot({ path: path.join(fixture, 'answered-user-relation.png') });
  await workspace.getByRole('tab', { name: '코드 구성', exact: true }).click();
  assert.equal(await workspace.getByLabel('Thinking', { exact: true }).isChecked(), true, 'question merge preserves source group Thinking');
  await workspace.getByRole('button', { name: '+ 그룹 추가', exact: true }).click();
  await groupList.getByRole('button').first().click();
  await blockEditor.getByLabel('블럭 그룹 이동', { exact: true }).selectOption({ label: '그룹 2' });
  assert.equal(await workspace.locator('[aria-label="그룹 목록"] button').count(), 2, 'a block can be split into an independent group');
  const draftSaved = page.waitForResponse(r => /code-block-workspaces\/[^/]+$/.test(r.url()) && r.request().method() === 'PUT');
  await workspace.getByRole('button', { name: '초안 저장', exact: true }).click();
  assert.equal((await draftSaved).status(), 200);
  // Exercise ordinary URL/click labels through the actual compiler and all five renderers.
  await workspace.getByRole('button', { name: '새 작업', exact: true }).click();
  await workspace.getByLabel('작업 제목', { exact: true }).fill('다섯 종류 라벨 검증');
  await workspace.getByLabel('언어', { exact: true }).selectOption('csharp');
  await workspace.getByLabel('코드', { exact: true }).fill('class Machine { int state; void Tick(){if(state==0) state=1; Save();} void Save(){} }');
  for (const type of ['flowchart', 'sequence', 'class', 'state', 'code-relation']) await workspace.getByLabel('종류 추가', { exact: true }).selectOption(type);
  const fiveResponse = page.waitForResponse(r => /code-block-workspaces\/[^/]+\/runs$/.test(r.url()) && r.request().method() === 'POST');
  await generateButton.click();
  const fiveId = (await (await fiveResponse).json()).id;
  await workspace.getByText('의미 생성 미완료 · 실패 사유와 정적 구조를 확인하세요', { exact: true }).waitFor({ timeout: 30000 });
  await workspace.getByLabel('정적 구조 보기', { exact: true }).check();
  const five = await (await page.request.get(`${origin}/api/v1/code-block-runs/${fiveId}`)).json();
  assert.equal(five.results[0].views.length, 5);
  const diagnosticDownload = page.waitForEvent('download');
  await workspace.getByRole('button', { name: '작업 진단 다운로드', exact: true }).click();
  const diagnosticFile = path.join(fixture, 'downloaded-diagnostics.json');
  await (await diagnosticDownload).saveAs(diagnosticFile);
  const diagnostic = JSON.parse(await readFile(diagnosticFile, 'utf8'));
  assert.equal(diagnostic.id, fiveId); assert.equal(diagnostic.version, 2);
  assert.ok(!('checkpoints' in diagnostic) && !JSON.stringify(diagnostic).includes('class Machine'));
  for (const view of five.results[0].views) {
    assert.ok(view.pages.length, `${view.selection.diagramType} has source-backed pages`);
    await workspace.locator(`[role="treeitem"][data-row-id="${view.viewId}:${view.pages[0].id}"]`).click();
    await editor.getByRole('button', { name: '구조 편집', exact: true }).click();
    await editor.locator('.edit-list .edit-row textarea').first().fill('http://localhost:5173 click 요청 확인');
    const edited = page.waitForResponse(r => r.url().endsWith('/edits') && r.request().method() === 'POST');
    await editor.getByRole('button', { name: '새 리비전 저장', exact: true }).click();
    assert.equal((await edited).status(), 200);
    await page.waitForFunction(() => /http:\/\/localhost:5173/.test(document.querySelector('.code-block-workspace .structured-diagram-editor svg')?.textContent ?? ''));
    assert.equal(await editor.locator('.error-panel').count(), 0, 'display URL is not a security error');
    assert.equal(await editor.locator('svg a, svg image, svg foreignObject, svg [onclick]').count(), 0, 'display URL has no link or embedded content');
    await editor.screenshot({ path: path.join(fixture, `url-${view.selection.diagramType}.png`) });
  }
  await checkSemanticProgress({ page, fixture, run: five });
  assert.deepEqual(errors, []);
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', packageRoot, runId: body.id, checked: ['draft tabs', 'block order', 'language inheritance', 'group views', 'render', 'zoom', 'reload', 'viewport captures', 'evidence', 'manual edit provenance', 'SVG/PNG downloads', 'question reload and answer', 'explicit group merge and split', 'tree keyboard navigation', 'collapsed branches and result location restoration', 'older generation and edit restoration', 'navigation without generation', 'URL/click labels in five formats without link behavior'] }, null, 2));
  console.log(`Code block UI smoke passed. Screenshots: ${path.relative(root, fixture)}`);
} catch (error) {
  await page?.screenshot({ path: path.join(fixture, 'failure.png'), fullPage: true }).catch(() => undefined);
  if (page) await writeFile(path.join(fixture, 'failure.html'), await page.content()).catch(() => undefined);
  console.error(`Code block UI diagnostics: ${path.relative(root, fixture)}`); throw error;
} finally { await browser?.close(); server.kill(); await writeFile(path.join(fixture, 'server.log'), output); }
