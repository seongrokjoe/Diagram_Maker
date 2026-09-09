import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import fs from 'node:fs';
import { rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { promisify } from 'node:util';

const exec = promisify(execFile);

test('PowerShell builds need no default policy and enforce every explicitly supplied policy', { skip: process.platform !== 'win32' }, async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'diagram-ps-policy-'));
  t.after(() => rm(root, { recursive: true }));
  fs.mkdirSync(path.join(root, 'DiagramMaker'));
  fs.writeFileSync(path.join(root, 'DiagramMaker/network-policy.json'), 'not-json');
  const policy = path.join(root, 'explicit.json');
  const run = (candidate, policyPath = '') => exec('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
    "$ErrorActionPreference = 'Stop'\n. $env:DM_TEST_COMMON_SCRIPT\nAssert-ApprovedWorkRoot $env:DM_TEST_CANDIDATE"], {
    windowsHide: true, timeout: 15000, env: { ...process.env, LOCALAPPDATA: root,
      DIAGRAMMAKER_NETWORK_POLICY_PATH: policyPath, DM_TEST_CANDIDATE: candidate,
      DM_TEST_COMMON_SCRIPT: fileURLToPath(new URL('./offline-common.ps1', import.meta.url)) },
  });
  await assert.doesNotReject(() => run(root));
  await assert.rejects(() => run(path.join(root, 'OneDrive/source')));
  await assert.rejects(() => run('relative/source'));
  await assert.rejects(() => run(root, policy));
  fs.writeFileSync(policy, 'not-json'); await assert.rejects(() => run(root, policy));
  fs.writeFileSync(policy, JSON.stringify({ LocalRoots: [] })); await assert.rejects(() => run(root, policy));
  fs.writeFileSync(policy, JSON.stringify({ LocalRoots: [root] }));
  await assert.doesNotReject(() => run(path.join(root, 'output'), policy));
  await assert.rejects(() => run(root + '-sibling', policy));
});
