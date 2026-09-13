// Synthetic UI layout and commit interaction checks; never contacts corporate systems.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { cp, mkdir, mkdtemp, realpath, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';
import { checkDesignUi } from './design-ui-checks.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
const { chromium } = createRequire(path.join(root, 'artifacts/ui-check/package.json'))('playwright');
await mkdir(path.join(root, 'artifacts'), { recursive: true });
const fixture = await mkdtemp(path.join(root, 'artifacts/design-ui-'));
const packageRoot = process.argv[2] ? await realpath(path.resolve(root, process.argv[2])) : null;
if (packageRoot) {
  const relativePackage = path.relative(await realpath(path.join(root, 'artifacts/stage')), packageRoot);
  assert.ok(relativePackage && relativePackage !== '..' && !relativePackage.startsWith('..' + path.sep) && !path.isAbsolute(relativePackage),
    'The resolved package directory must stay under artifacts/stage');
}
const runtime = packageRoot ?? path.join(fixture, 'api');
if (!packageRoot) {
  await cp(path.join(root, 'src/DiagramMaker.Api/bin/Release/net9.0'), runtime, { recursive: true });
  await cp(process.env.CODE_BLOCK_WEB_DIST ?? path.join(root, 'artifacts/verify-web-dist'), path.join(runtime, 'wwwroot'), { recursive: true });
}
const policy = path.join(fixture, 'disabled-llm.json');
await writeFile(policy, JSON.stringify({ Llm: { Enabled: false, AllowDevelopmentStub: false } }));
const network = path.join(fixture, 'network.json');
await writeFile(network, JSON.stringify({ LocalRoots: [root], LlmOrigins: [], LlmAddressRanges: [], Databases: [] }));
const server = spawn(packageRoot ? path.join(runtime, 'DiagramMaker.Api.exe') : 'dotnet',
  [...(packageRoot ? [] : [path.join(runtime, 'DiagramMaker.Api.dll')]), '--urls', 'http://127.0.0.1:0'], {
  cwd: runtime, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', DOTNET_ENVIRONMENT: 'Development',
    Storage__Provider: 'InMemory', Llm__Enabled: 'false', CodexTest__Enabled: 'false', DIAGRAMMAKER_LLM_POLICY_PATH: policy, DIAGRAMMAKER_NETWORK_POLICY_PATH: network },
});
let output = '', browser, page;
server.stdout.on('data', data => { output += data; }); server.stderr.on('data', data => { output += data; });
try {
  let origin;
  for (let i = 0; i < 100; i++) { origin = output.match(/Now listening on: (http:\/\/127\.0\.0\.1:\d+)/)?.[1]; if (origin) break; await delay(100); }
  assert.ok(origin, output);
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [], external = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    external.push(route.request().url()); return route.abort();
  });
  await checkDesignUi({ page, origin, fixture });
  assert.deepEqual(errors, []);
  assert.deepEqual(external, []);
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', checks: ['natural sample rows', 'bundled font', 'Git commit popup keyboard/search/paging/SHA/stale requests', 'Git grouping and results', 'five viewport widths', 'no external requests'] }, null, 2));
  console.log(`Design UI smoke passed. Screenshots: ${path.relative(root, fixture)}`);
} catch (error) {
  await page?.screenshot({ path: path.join(fixture, 'failure.png'), fullPage: true }).catch(() => {});
  await writeFile(path.join(fixture, 'failure.txt'), String(error.stack ?? error));
  console.error(`Design UI failed: ${fixture}`);
  throw error;
} finally {
  await browser?.close();
  server.kill();
}
