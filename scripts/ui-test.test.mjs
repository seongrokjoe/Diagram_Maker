import assert from 'node:assert/strict';
import { mkdtemp, mkdir, readFile, rename, rm, stat, symlink, utimes, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { copySourceInputs, readSourceInputs, sourceFingerprint, validBuild } from './ui-test-build.mjs';

async function fixture(t) {
  const root = await mkdtemp(path.join(os.tmpdir(), 'dm-ui-source-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  for (const directory of ['src/DiagramMaker.Api', 'web/src', 'tools/git-worker', 'scripts'])
    await mkdir(path.join(root, directory), { recursive: true });
  for (const name of ['global.json', 'NuGet.Config', 'src/DiagramMaker.Api/DiagramMaker.Api.csproj',
    'web/package-lock.json', 'web/build.mjs', 'tools/git-worker/package-lock.json'])
    await writeFile(path.join(root, name), 'synthetic input');
  return root;
}

test('current-source fingerprint detects content changes with unchanged times, additions and deletions', async t => {
  const root = await fixture(t);
  const file = path.join(root, 'web/src/view.tsx');
  await writeFile(file, 'first');
  const originalTime = await stat(file);
  const initial = sourceFingerprint(await readSourceInputs(root));
  await writeFile(file, 'other');
  await utimes(file, originalTime.atime, originalTime.mtime);
  assert.notEqual(sourceFingerprint(await readSourceInputs(root)), initial);
  await writeFile(file, 'first');
  assert.equal(sourceFingerprint(await readSourceInputs(root)), initial);
  await writeFile(path.join(root, 'web/src/added.css'), '.added {}');
  assert.notEqual(sourceFingerprint(await readSourceInputs(root)), initial);
  await rm(path.join(root, 'web/src/added.css'));
  assert.equal(sourceFingerprint(await readSourceInputs(root)), initial);
  await rm(file);
  assert.notEqual(sourceFingerprint(await readSourceInputs(root)), initial);
  const withoutFile = await readSourceInputs(root);
  await writeFile(path.join(root, 'global.json'), 'changed SDK configuration');
  assert.notEqual(sourceFingerprint(await readSourceInputs(root)), sourceFingerprint(withoutFile));
  assert.notEqual(sourceFingerprint(withoutFile, 'sdk-a'), sourceFingerprint(withoutFile, 'sdk-b'));
});

test('snapshot excludes generated output and local configuration, preserves Unicode, and rejects concurrent edits', async t => {
  const root = await fixture(t);
  await writeFile(path.join(root, 'web/src/view.tsx'), '한글 😀');
  const initial = await readSourceInputs(root);
  for (const directory of ['web/dist', 'web/node_modules', 'src/DiagramMaker.Api/obj', 'src/DiagramMaker.Api/wwwroot']) {
    await mkdir(path.join(root, directory), { recursive: true });
    await writeFile(path.join(root, directory, 'generated.txt'), 'output');
  }
  await writeFile(path.join(root, 'src/DiagramMaker.Api/appsettings.Production.json'), 'private configuration');
  assert.deepEqual(await readSourceInputs(root), initial);
  const snapshot = path.join(root, 'snapshot');
  await copySourceInputs(root, snapshot, initial);
  assert.equal(await readFile(path.join(snapshot, 'web/src/view.tsx'), 'utf8'), '한글 😀');
  await writeFile(path.join(root, 'web/src/view.tsx'), 'Changed during snapshot');
  await assert.rejects(() => copySourceInputs(root, path.join(root, 'changed'), initial), /changed while taking/);
  await assert.rejects(() => copySourceInputs(root, path.join(root, 'traversal'), [{ path: '../outside', sha256: 'invalid' }]), /inside/);
});

test('build cache rejects modified bytes, extra files and missing outputs', async t => {
  const root = await fixture(t);
  const app = path.join(root, 'app');
  await mkdir(app);
  await writeFile(path.join(app, 'file'), '');
  const manifest = { files: [{ path: 'file', sha256: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855' }] };
  assert.equal(await validBuild(app, manifest), true);
  await writeFile(path.join(app, 'file'), 'altered');
  assert.equal(await validBuild(app, manifest), false);
  await writeFile(path.join(app, 'file'), '');
  await writeFile(path.join(app, 'extra'), 'extra');
  assert.equal(await validBuild(app, manifest), false);
  await rm(path.join(app, 'extra'));
  await rm(path.join(app, 'file'));
  assert.equal(await validBuild(app, manifest), false);
});

test('linked source and build directories are rejected without following their targets', { skip: process.platform !== 'win32' }, async t => {
  const root = await fixture(t);
  const outside = path.join(root, 'outside');
  await mkdir(outside);
  await writeFile(path.join(outside, 'sentinel'), 'keep');
  await symlink(outside, path.join(root, 'web/src/linked'), 'junction');
  await assert.rejects(() => readSourceInputs(root), /Linked/);
  await assert.rejects(() => validBuild(path.join(root, 'web'), { files: [] }), /Linked/);
  assert.equal(await readFile(path.join(outside, 'sentinel'), 'utf8'), 'keep');
});

test('linked ancestors of selected sources and paths changed after scanning are rejected', { skip: process.platform !== 'win32' }, async t => {
  const root = await fixture(t);
  const inputs = await readSourceInputs(root);
  // tools/git-worker itself remains an ordinary directory under a linked parent.
  await rename(path.join(root, 'tools'), path.join(root, 'held-tools'));
  await symlink(path.join(root, 'held-tools'), path.join(root, 'tools'), 'junction');
  await assert.rejects(() => readSourceInputs(root), /Linked/);
  await assert.rejects(() => copySourceInputs(root, path.join(root, 'snapshot'), inputs), /Linked/);
  assert.equal(await readFile(path.join(root, 'held-tools/git-worker/package-lock.json'), 'utf8'), 'synthetic input');
});
