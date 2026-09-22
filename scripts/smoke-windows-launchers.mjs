// Run the shipped CMD entry points with synthetic settings in a private copy.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { cp, mkdir, mkdtemp, readFile, readdir, realpath, writeFile } from 'node:fs/promises';
import { createServer } from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { assertLocalPath } from '../tools/git-worker/local-security.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
assertLocalPath(root);
assert.ok(process.argv[2], 'Pass the unpacked package directory under artifacts/stage');
const packageRoot = await realpath(path.resolve(root, process.argv[2]));
const relative = path.relative(await realpath(path.join(root, 'artifacts/stage')), packageRoot);
assert.ok(relative && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative));
const fixture = await mkdtemp(path.join(root, 'artifacts/windows-launchers-'));
const app = path.join(fixture, 'app');
const localAppData = path.join(fixture, 'local');
const policyDirectory = path.join(localAppData, 'DiagramMaker');
await cp(packageRoot, app, { recursive: true });
await mkdir(policyDirectory, { recursive: true });
await writeFile(path.join(policyDirectory, 'llm-policy.json'), JSON.stringify({ Llm: { Enabled: false, AllowDevelopmentStub: false } }));
const defaultPolicy = path.join(policyDirectory, 'network-policy.json');
const explicitPolicy = path.join(fixture, 'explicit.json');
const approved = JSON.stringify({ LocalRoots: [fixture], LlmOrigins: [], LlmAddressRanges: [], Databases: [] });
await writeFile(explicitPolicy, approved);
const cases = [
  { name: 'start ignores malformed leftover default policy', script: 'start.cmd', defaultContent: 'not-json', starts: true },
  { name: 'optional launcher loads default policy', script: 'start-with-network-policy.cmd', defaultContent: approved, starts: true },
  { name: 'start honors explicit policy', script: 'start.cmd', defaultContent: 'not-json', policyPath: explicitPolicy, starts: true },
  { name: 'optional launcher rejects malformed policy', script: 'start-with-network-policy.cmd', defaultContent: 'not-json', match: /JsonException/ },
  { name: 'start rejects missing explicit policy', script: 'start.cmd', policyPath: path.join(fixture, 'missing.json'), match: /FileNotFoundException/ },
  { name: 'optional launcher rejects missing explicit policy', script: 'start-with-network-policy.cmd', policyPath: path.join(fixture, 'missing.json'), match: /Optional network policy was not found/ },
];
const checks = [];

async function waitForLauncherPort() {
  const deadline = Date.now() + 5000;
  while (true) {
    const probe = createServer();
    try {
      const listening = once(probe, 'listening');
      probe.listen(5080, '127.0.0.1');
      await listening;
      await new Promise(resolve => probe.close(resolve));
      return;
    } catch (error) {
      probe.close();
      if (error.code !== 'EADDRINUSE' || Date.now() >= deadline) throw error;
      // Windows may release the socket just after taskkill reports completion.
      await delay(100);
    }
  }
}

try {
  for (const item of cases) {
    // Do not interfere with an existing local application using the launcher's port.
    await waitForLauncherPort();
    await writeFile(defaultPolicy, item.defaultContent ?? approved);
    const child = spawn(process.env.ComSpec ?? 'cmd.exe', ['/d', '/c', item.script], {
      cwd: app, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, LOCALAPPDATA: localAppData, DIAGRAMMAKER_NETWORK_POLICY_PATH: item.policyPath ?? '',
        DOTNET_ENVIRONMENT: 'Development', Security__TrustReverseProxyHeaders: 'false' },
    });
    let output = '';
    child.stdout.on('data', data => output += data);
    child.stderr.on('data', data => output += data);
    const closed = new Promise((resolve, reject) => { child.once('error', reject); child.once('close', resolve); });
    try {
      for (let attempt = 0; attempt < 150; attempt++) {
        if (child.exitCode !== null || /Now listening on: http:\/\/127\.0\.0\.1:5080/.test(output)) break;
        await delay(100);
      }
      if (item.starts) {
        assert.match(output, /Now listening on: http:\/\/127\.0\.0\.1:5080/, output);
        const response = await fetch('http://127.0.0.1:5080/health', { signal: AbortSignal.timeout(5000) });
        assert.equal(response.status, 200);
        assert.equal((await response.json()).service, 'diagram-maker-api');
        if (item === cases[0]) {
          const test = spawn(process.env.ComSpec ?? 'cmd.exe', ['/d', '/c', 'test-code-diagram.cmd'], {
            cwd: app, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
          });
          let testOutput = '';
          test.stdout.on('data', data => testOutput += data); test.stderr.on('data', data => testOutput += data);
          const [testExit] = await once(test, 'close');
          assert.notEqual(testExit, 0, 'Disabled LLM must fail the command');
          const reports = await readdir(path.join(app, 'diagnostics'));
          assert.equal(reports.length, 1);
          const report = await readFile(path.join(app, 'diagnostics', reports[0]), 'utf8');
          assert.match(report, /LLM_DISABLED/); assert.match(report, /Thinking OFF/);
          await writeFile(path.join(fixture, 'code-diagram-command.txt'), report);
          await writeFile(path.join(fixture, 'code-diagram-command.log'), testOutput);
          checks.push('code diagram command retains failure report and nonzero exit');
          const natural = spawn(process.env.ComSpec ?? 'cmd.exe', ['/d', '/c', 'test-natural-diagram.cmd'], {
            cwd: app, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
          });
          let naturalOutput = '';
          natural.stdout.on('data', data => naturalOutput += data); natural.stderr.on('data', data => naturalOutput += data);
          const [naturalExit] = await once(natural, 'close');
          assert.notEqual(naturalExit, 0, 'Disabled LLM must fail natural test');
          const naturalName = (await readdir(path.join(app, 'diagnostics'))).find(name => name.startsWith('natural-diagram-'));
          assert.ok(naturalName);
          const naturalReport = await readFile(path.join(app, 'diagnostics', naturalName), 'utf8');
          assert.match(naturalReport, /LLM_DISABLED/); assert.match(naturalReport, /natural-design-v5/);
          await writeFile(path.join(fixture, 'natural-diagram-command.txt'), naturalReport);
          await writeFile(path.join(fixture, 'natural-diagram-command.log'), naturalOutput);
          checks.push('natural diagram command retains failure report and nonzero exit');
        }
      } else {
        assert.notEqual(child.exitCode, null, output);
        assert.notEqual(child.exitCode, 0, output);
        assert.doesNotMatch(output, /Now listening on:/);
        assert.match(output, item.match, output);
      }
      checks.push(item.name);
    } finally {
      await writeFile(path.join(fixture, `${checks.length}-${item.script}.log`), output);
      // Kill only the process tree created by this case, including its API child.
      if (child.exitCode === null) {
        const stop = spawn('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
        const [exitCode] = await once(stop, 'close');
        if (exitCode !== 0) {
          child.stdout.destroy(); child.stderr.destroy(); child.unref();
          throw new Error(`Could not stop test process tree ${child.pid}; taskkill exited ${exitCode}.`);
        }
      }
      await closed;
    }
  }
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'passed', checks, syntheticOnly: true }, null, 2));
  console.log(`Windows launchers: ${checks.length} checks passed. ${path.relative(root, fixture)}`);
} catch (error) {
  await writeFile(path.join(fixture, 'result.json'), JSON.stringify({ status: 'failed', checks, error: String(error) }, null, 2));
  throw error;
}
