// Launch the packaged executable with an isolated in-memory store and LLM disabled.
// Checks the actual packaged Mermaid runtime and all preset types in Edge.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, realpath, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { smokeCodeBlocks } from './code-block-smoke.mjs';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
assert.ok(process.argv[2], 'Pass the unpacked package directory under artifacts/stage');
const stageRoot = await realpath(path.join(root, 'artifacts', 'stage'));
const packageRoot = await realpath(path.resolve(root, process.argv[2]));
const relativePackage = path.relative(stageRoot, packageRoot);
assert.ok(relativePackage && relativePackage !== '..' && !relativePackage.startsWith('..' + path.sep) && !path.isAbsolute(relativePackage),
  'The resolved package directory must stay under artifacts/stage');
const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
await mkdir(path.join(root, 'artifacts'), { recursive: true });
const run = await mkdtemp(path.join(root, 'artifacts/offline-preview-'));
const policy = path.join(run, 'disabled-llm.json');
await writeFile(policy, JSON.stringify({ Llm: { Enabled: false } }));
const networkPolicy = path.join(run, 'test-network-policy.json');
await writeFile(networkPolicy, JSON.stringify({ LocalRoots: [root], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
let output = '';
const server = spawn(path.join(packageRoot, 'DiagramMaker.Api.exe'), ['--urls', 'http://127.0.0.1:0'], {
  cwd: packageRoot, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    Storage__Provider: 'InMemory', Llm__Enabled: 'false', CodexTest__Enabled: 'false',
    GitWorker__NodeExecutable: path.join(packageRoot, 'runtime/node/node.exe'),
    GitWorker__ScriptPath: path.join(packageRoot, 'tools/git-worker/index.mjs'),
    Llm__AllowDevelopmentStub: 'false', Security__TrustReverseProxyHeaders: 'false', DIAGRAMMAKER_LLM_POLICY_PATH: policy,
    DIAGRAMMAKER_NETWORK_POLICY_PATH: networkPolicy },
});
let startupError;
server.on('error', error => { startupError = error; });
const closed = new Promise(resolve => server.once('close', resolve));
server.stdout.on('data', data => { output += data; });
server.stderr.on('data', data => { output += data; });
let browser, page;
const checks = [];
const assetHashes = {};
const previewSizes = {};
async function saveResult(status, error) {
  await writeFile(path.join(run, 'result.json'), JSON.stringify({ status, packageRoot, checks, assetHashes, previewSizes,
    environment: 'Development', llmEnabled: false, error: error ? String(error) : undefined }, null, 2));
}
try {
  await saveResult('running');
  let origin;
  for (let index = 0; index < 100; index++) {
    if (startupError) throw startupError;
    origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
    if (origin) break;
    assert.equal(server.exitCode, null, `package startup: ${output}`);
    await delay(100);
  }
  assert.ok(origin, `package startup: ${output}`);
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  page = await browser.newPage({ viewport: { width: 1024, height: 1100 } });
  const errors = [], remoteRequests = [], mutations = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => {
    if (!route.request().url().startsWith(origin + '/')) { remoteRequests.push(route.request().url()); return route.abort(); }
    if (route.request().method() !== 'GET') { mutations.push(route.request().url()); return route.abort(); }
    return route.continue();
  });
  await page.goto(origin);
  const hash = value => createHash('sha256').update(value).digest('hex');
  for (const asset of ['index.html', 'assets/app.js', 'assets/app.css', 'vendor/mermaid.min.js']) {
    const response = await page.request.get(origin + '/' + asset);
    assert.equal(response.status(), 200, asset + ': packaged asset must load');
    assetHashes[asset] = hash(await response.body());
    assert.equal(assetHashes[asset], hash(await readFile(path.join(packageRoot, 'wwwroot', asset))),
      asset + ': must serve the packaged file, not a development build');
  }
  const presetsResponse = await page.request.get(origin + '/api/v1/diagram-presets');
  assert.equal(presetsResponse.status(), 200);
  const presets = await presetsResponse.json();
  const picker = page.getByRole('combobox', { name: '다이어그램 종류', exact: true });
  for (const width of [1024, 1250, 1366, 1920]) {
    await page.setViewportSize({ width, height: 1100 });
    for (const type of ['flowchart', 'sequence', 'class', 'state']) {
      await picker.selectOption(type);
      const expectedNames = presets.filter(preset => preset.type === type).map(preset => preset.name);
      assert.ok(expectedNames.length > 0, type + ': preset catalog must not be empty');
      const measured = await page.waitForFunction(names => {
        const cards = [...document.querySelectorAll('.preset-card')];
        if (cards.length !== names.length) return false;
        const minimumCardWidth = Math.min(document.querySelector('.preset-grid').clientWidth, 180);
        const sizes = cards.map((card, index) => {
          const svg = card.querySelector('.diagram-canvas.compact svg');
          const box = svg?.getBoundingClientRect();
          const cardWidth = card.getBoundingClientRect().width;
          if (card.querySelector('strong')?.textContent !== names[index] || !svg || !box ||
            getComputedStyle(svg).visibility !== 'visible' || box.width <= 30 || box.height <= 10 || cardWidth < minimumCardWidth - 1) return null;
          return { name: names[index], width: box.width, height: box.height, cardWidth };
        });
        return sizes.every(size => size !== null) ? sizes : false;
      }, expectedNames);
      previewSizes[type + '-' + width] = await measured.jsonValue();
      await measured.dispose();
      assert.equal(await page.locator('.preset-grid .error-panel').count(), 0, type + ': no Mermaid render errors');
      await page.locator('.preset-grid').screenshot({ path: path.join(run, `${type}-${width}.png`) });
      checks.push(`${type}: ${width}px`);
      await saveResult('running');
    }
  }
  assert.deepEqual(errors, []);
  assert.deepEqual(remoteRequests, [], 'previews must work with no external connections');
  assert.deepEqual(mutations, [], 'previews must not request generation');
  await page.getByRole('button', { name: '코드 블럭 다이어그램', exact: true }).click();
  await page.locator('.code-block-workspace').getByRole('button', { name: '다이어그램 생성', exact: true }).waitFor();
  await page.locator('.code-block-workspace').screenshot({ path: path.join(run, 'code-block-workspace.png') });
  const codeArtifacts = [];
  async function codeRequest(url, method = 'GET', body, status = 200) {
    const response = await fetch(origin + '/api/v1' + url, { method, headers: { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
    const value = await response.json(); assert.equal(response.status, status, JSON.stringify(value));
    if (method === 'GET' && value.mermaidDsl) codeArtifacts.push(value);
    return value;
  }
  async function codePoll(url, terminal) {
    for (let i = 0; i < 120; i++) { const value = await codeRequest(url); if (terminal.includes(value.state)) return value;
      assert.notEqual(value.state, 'Failed', JSON.stringify(value)); await delay(250); }
    throw new Error('Packaged code block worker timed out');
  }
  await smokeCodeBlocks(codeRequest, codePoll);
  assert.equal(new Set(codeArtifacts.map(a => a.type)).size, 5);
  for (const [index, artifact] of codeArtifacts.entries()) {
    // Parse and render every generated code page with the actual packaged runtime.
    await page.evaluate(async ({ index, dsl }) => {
      const { svg } = await window.mermaid.render(`packaged_code_${index}`, dsl);
      let result = document.getElementById('packaged-code-result');
      if (!result) {
        result = document.createElement('section'); result.id = 'packaged-code-result';
        result.style.cssText = 'width:1200px;padding:24px;background:white'; document.body.append(result);
      }
      result.innerHTML = svg;
      const rendered = result.querySelector('svg');
      if (!rendered || !rendered.getBoundingClientRect().height) throw new Error('Generated code SVG is empty');
    }, { index, dsl: artifact.mermaidDsl });
    await page.locator('#packaged-code-result').screenshot({ path: path.join(run, `code-${artifact.type}-${index}.png`) });
  }
  assert.deepEqual(errors, []);
  assert.deepEqual(remoteRequests, [], 'generated code diagrams must render without external connections');
  checks.push('packaged code-block tab, five formats, C++ stdin worker, evidence, clarification, edits and deletion');
  checks.push(`all ${codeArtifacts.length} generated code pages rendered using packaged Mermaid`);
  await saveResult('passed');
  console.log(`Offline previews passed: 4 diagram types × 4 widths, packaged Mermaid, LLM disabled. ${path.relative(root, run)}`);
} catch (error) {
  await page?.screenshot({ path: path.join(run, 'failure.png'), fullPage: true }).catch(() => undefined);
  if (page) await writeFile(path.join(run, 'failure.html'), await page.content()).catch(() => undefined);
  await saveResult('failed', error);
  throw error;
} finally {
  if (browser) await browser.close().catch(() => undefined);
  if (server.exitCode === null) {
    server.kill();
    await Promise.race([closed, delay(5000)]);
  }
  await writeFile(path.join(run, 'server.log'), output);
}
