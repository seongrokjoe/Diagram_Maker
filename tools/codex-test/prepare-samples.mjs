// Creates only new synthetic fixtures. Never opens an existing user repository.
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';

const assets = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(assets, '../..');
const runtime = process.argv[2] ? path.resolve(process.argv[2]) : path.join(project, 'artifacts/codex-test');
if (!runtime.startsWith(path.join(project, 'artifacts') + path.sep) || path.basename(runtime) !== 'codex-test' || path.basename(path.dirname(runtime)) !== 'artifacts') {
  throw new Error('Sample runtime must be an isolated artifacts/codex-test directory under project artifacts.');
}
const require = createRequire(path.join(project, 'tools/git-worker/package.json'));
const git = require('isomorphic-git');
const catalog = JSON.parse(await readFile(path.join(assets, 'samples.json'), 'utf8'));
await mkdir(path.join(runtime, 'repositories'), { recursive: true });
const instance = await mkdtemp(path.join(runtime, 'repositories/session-'));
const repositories = [];
for (const sample of catalog.scenarios.filter(item => item.kind === 'git')) {
  if (!/^[a-z0-9-]+$/.test(sample.id) || !/^[A-Za-z0-9]+\.(cpp|cs)$/.test(sample.fileName)) throw new Error('Invalid sample asset');
  const dir = path.join(instance, sample.id);
  await mkdir(dir);
  await git.init({ fs, dir, defaultBranch: 'main' });
  const author = { name: 'Synthetic Test', email: 'synthetic@example.invalid', timestamp: 1700000000, timezoneOffset: 0 };
  await writeFile(path.join(dir, sample.fileName), sample.before, 'utf8');
  await git.add({ fs, dir, filepath: sample.fileName });
  const baseSha = await git.commit({ fs, dir, author, message: 'Synthetic baseline' });
  await writeFile(path.join(dir, sample.fileName), sample.after, 'utf8');
  await git.add({ fs, dir, filepath: sample.fileName });
  const targetSha = await git.commit({ fs, dir, author: { ...author, timestamp: 1700000060 }, message: 'Synthetic change' });
  const digest = createHash('sha256').update(`codex-sample-${catalog.version}-${sample.id}`).digest('hex').slice(0, 32);
  const repositoryId = `${digest.slice(0, 8)}-${digest.slice(8, 12)}-${digest.slice(12, 16)}-${digest.slice(16, 20)}-${digest.slice(20)}`;
  repositories.push({ scenarioId: sample.id, repositoryId, localPath: dir, baseSha, targetSha });
}
await writeFile(path.join(runtime, 'samples-manifest.json'), JSON.stringify({ version: catalog.version, repositories }, null, 2));
console.log(`Prepared ${repositories.length} synthetic repositories. Existing test results were preserved.`);
