// Build the user's current project in a private local snapshot. No release ZIP is used.
import { createHash, randomUUID } from 'node:crypto';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { cp, lstat, mkdir, readdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertApprovedBuildPath } from '../web/offline-policy.mjs';

const sourceDirectories = ['src', 'web', 'tools/git-worker', 'scripts'];
const rootInputs = ['global.json', 'NuGet.Config', 'Directory.Build.props', 'Directory.Build.targets',
  'Directory.Packages.props', '.npmrc'];
const excludedDirectories = new Set(['node_modules', 'bin', 'obj', 'dist', 'wwwroot', '.git', '.codex', '.agents',
  'data', 'artifacts', 'coverage', 'TestResults', '.vs', '.vscode', '.cache']);
const excludedFile = /(?:\.tsbuildinfo$|^\.env(?:\.|$)|^appsettings\.(?!Development\.json$).+\.json$|^(?:llm|network)-policy(?:\.local)?\.json$|\.user$|\.suo$)/i;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

function within(parent, candidate) {
  const relative = path.relative(path.resolve(parent), path.resolve(candidate));
  if (!relative || relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative))
    throw new Error('UI build path must remain inside its private directory.');
  return candidate;
}

async function assertUnlinkedSourcePath(candidate) {
  for (let current = candidate; ; current = path.dirname(current)) {
    if ((await lstat(current)).isSymbolicLink()) throw new Error('Linked project paths are prohibited.');
    if (current === path.dirname(current)) break;
  }
}

async function walk(directory, prefix = '', filter = false) {
  const files = [];
  const directoryInfo = await lstat(directory);
  if (!directoryInfo.isDirectory() || directoryInfo.isSymbolicLink()) throw new Error('Linked UI source/build directories are prohibited.');
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (filter && ((entry.isDirectory() && excludedDirectories.has(entry.name)) || excludedFile.test(entry.name))) continue;
    if (entry.isSymbolicLink()) throw new Error('Linked UI source/build entries are prohibited.');
    const full = path.join(directory, entry.name);
    const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isDirectory()) files.push(...await walk(full, relative, filter));
    else if (entry.isFile()) files.push({ path: relative, sha256: hash(await readFile(full)) });
    else throw new Error('Unsupported UI source/build entry.');
  }
  return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}

export async function readSourceInputs(source) {
  if (!path.isAbsolute(source) || /^[/\\]{2}/.test(source)) throw new Error('An absolute local project path is required.');
  // Reading this explicitly selected project may occur on OneDrive; all builds,
  // dependencies, runtime files and user data remain in the approved private root.
  await assertUnlinkedSourcePath(source);
  const files = [];
  for (const directory of sourceDirectories) {
    const input = path.join(source, directory);
    await assertUnlinkedSourcePath(input);
    files.push(...await walk(input, directory, true));
  }
  for (const name of rootInputs) {
    try {
      if ((await lstat(path.join(source, name))).isSymbolicLink()) throw new Error('Linked project configuration is prohibited.');
      files.push({ path: name, sha256: hash(await readFile(path.join(source, name))) });
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  for (const required of ['global.json', 'NuGet.Config', 'src/DiagramMaker.Api/DiagramMaker.Api.csproj',
    'web/package-lock.json', 'web/build.mjs', 'tools/git-worker/package-lock.json']) {
    if (!files.some(file => file.path === required)) throw new Error(`Missing UI build input: ${required}`);
  }
  return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}

export function sourceFingerprint(files, toolchain = '') {
  return hash(JSON.stringify({ version: 1, files, toolchain }));
}

export async function copySourceInputs(source, destination, files) {
  assertApprovedBuildPath(destination);
  await mkdir(destination, { recursive: true });
  for (const file of files) {
    const input = within(source, path.resolve(source, file.path));
    const output = within(destination, path.resolve(destination, file.path));
    await assertUnlinkedSourcePath(input);
    const bytes = await readFile(input);
    if (hash(bytes) !== file.sha256) throw new Error('Project changed while taking a UI snapshot. Run start-ui-test.cmd again.');
    await mkdir(path.dirname(output), { recursive: true });
    await writeFile(output, bytes, { flag: 'wx' });
  }
}

export async function validBuild(app, manifest) {
  try {
    assertApprovedBuildPath(app);
    return JSON.stringify(await walk(app)) === JSON.stringify(manifest.files);
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}

async function run(executable, args, cwd, label, quiet = false) {
  const child = spawn(executable, args, { cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, npm_config_offline: 'true', npm_config_audit: 'false', npm_config_ignore_scripts: 'true',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', NUGET_CERT_REVOCATION_MODE: 'offline' } });
  let output = '';
  child.stdout.on('data', bytes => { output += bytes; if (!quiet) process.stdout.write(bytes); });
  child.stderr.on('data', bytes => { output += bytes; if (!quiet) process.stderr.write(bytes); });
  const [exitCode] = await once(child, 'close');
  if (exitCode !== 0) throw new Error(`${label} failed (exit ${exitCode}). Check the build log; the previous UI server was kept.`);
  return output.trim();
}

async function json(file) {
  return JSON.parse((await readFile(file, 'utf8')).replace(/^\uFEFF/, ''));
}

async function atomicJson(file, value) {
  const temporary = `${file}.${randomUUID()}.tmp`;
  await writeFile(temporary, JSON.stringify(value, null, 2));
  await rename(temporary, file);
}

export async function buildCurrentUi(source, root, resultPath) {
  assertApprovedBuildPath(root);
  within(root, resultPath);
  if (Number(process.versions.node.split('.')[0]) < 24) throw new Error('Node.js 24 or newer is required for current-source UI tests.');
  const dotnet = process.env.DIAGRAMMAKER_UI_DOTNET;
  const npmCli = path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js');
  if (!dotnet) throw new Error('The .NET SDK required by global.json is missing. See UI_TEST_KO.md.');
  try { await lstat(npmCli); } catch { throw new Error('The local Node.js installation must include npm. See UI_TEST_KO.md.'); }
  const sdkList = await run(dotnet, ['--list-sdks'], root, '.NET SDK check', true);
  if (!sdkList) throw new Error('Install the .NET SDK required by global.json before starting UI tests.');
  const toolchain = JSON.stringify({ node: process.version, architecture: process.arch, sdkList });
  const inputs = await readSourceInputs(source);
  const sourceHash = sourceFingerprint(inputs, toolchain);
  const installationPath = path.join(root, 'source-installation.json');
  let previous;
  try { previous = await json(installationPath); } catch (error) { if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error; }
  if (previous?.version === 1 && previous.sourceHash === sourceHash && /^[a-f0-9]{12}-[a-f0-9]{8}$/.test(previous.appFolder)
    && await validBuild(path.join(root, 'apps', previous.appFolder), previous)) {
    await atomicJson(resultPath, { ...previous, nodeExecutable: process.execPath, reused: true });
    console.log('Current project is unchanged; verified UI build reused.');
    return;
  }
  const buildId = randomUUID().replaceAll('-', '').slice(0, 8);
  const buildRoot = within(path.join(root, 'builds'), path.join(root, 'builds', buildId));
  const snapshot = path.join(buildRoot, 'source');
  const appFolder = `${sourceHash.slice(0, 12)}-${buildId}`;
  const app = within(path.join(root, 'apps'), path.join(root, 'apps', appFolder));
  assertApprovedBuildPath(buildRoot);
  assertApprovedBuildPath(app);
  await mkdir(path.dirname(buildRoot), { recursive: true });
  // Exclusive creation makes cleanup safe even if a generated identifier collides.
  await mkdir(buildRoot);
  console.log('Building the current UI, API and code worker in a private local snapshot...');
  try {
    await copySourceInputs(source, snapshot, inputs);
    // Resolve global.json before restoring npm packages or preparing an app folder.
    await run(dotnet, ['--version'], snapshot, 'Project .NET SDK check');
    for (const directory of ['web', 'tools/git-worker']) {
      await run(process.execPath, [npmCli, 'ci', '--offline', '--ignore-scripts', '--no-audit', '--no-fund'],
        path.join(snapshot, directory), `${directory} offline dependency restore`);
    }
    await run(process.execPath, [npmCli, 'run', 'build'], path.join(snapshot, 'web'), 'UI build');
    // Restore from provisioned packages only, even if project sources list registries.
    const restoreConfig = path.join(buildRoot, 'offline-nuget.config');
    await writeFile(restoreConfig, '<configuration><packageSources><clear /></packageSources><auditSources><clear /></auditSources></configuration>');
    const project = 'src/DiagramMaker.Api/DiagramMaker.Api.csproj';
    await run(dotnet, ['restore', project, '--locked-mode', '--configfile', restoreConfig, '-p:NuGetAudit=false'], snapshot, 'Offline .NET restore');
    await mkdir(path.dirname(app), { recursive: true });
    await mkdir(app);
    await run(dotnet, ['publish', project, '-c', 'Release', '--no-restore', '--self-contained', 'false', '-p:UseAppHost=true', '-o', app], snapshot, 'API build');
    await cp(path.join(snapshot, 'web/dist'), path.join(app, 'wwwroot'), { recursive: true });
    await cp(path.join(snapshot, 'tools/git-worker'), path.join(app, 'tools/git-worker'), { recursive: true });
    const htmlPath = path.join(app, 'wwwroot/index.html');
    const html = (await readFile(htmlPath, 'utf8')).replaceAll('/assets/app.js', `/assets/app.js?ui-build=${sourceHash}`)
      .replaceAll('/assets/app.css', `/assets/app.css?ui-build=${sourceHash}`);
    await writeFile(htmlPath, html);
    if (sourceFingerprint(await readSourceInputs(source), toolchain) !== sourceHash)
      throw new Error('Project changed during the UI build. Run start-ui-test.cmd again; the previous server was kept.');
    for (const required of ['DiagramMaker.Api.exe', 'DiagramMaker.Api.dll', 'wwwroot/assets/app.js', 'tools/git-worker/index.mjs']) await lstat(path.join(app, required));
    const manifest = { version: 1, sourceHash, appFolder, nodeExecutable: process.execPath,
      webSha256: hash(await readFile(path.join(app, 'wwwroot/assets/app.js'))), files: await walk(app) };
    await atomicJson(installationPath, manifest);
    await atomicJson(resultPath, { ...manifest, reused: false });
    console.log('Current-source UI build completed.');
  } finally {
    // Delete only the uniquely created scratch snapshot, never a project, app or data directory.
    within(path.join(root, 'builds'), buildRoot);
    assertApprovedBuildPath(buildRoot);
    await rm(buildRoot, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await buildCurrentUi(...process.argv.slice(2)); }
  catch (error) { console.error(`[ERROR] ${error.message}`); process.exitCode = 1; }
}
