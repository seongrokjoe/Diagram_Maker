import { createRequire } from 'node:module';
import { existsSync, realpathSync } from 'node:fs';
import path from 'node:path';

const command = path.resolve(process.argv[2]);
if (path.extname(command).toLowerCase() === '.exe') {
  console.log(realpathSync(command));
} else {
  const packageRoot = path.join(path.dirname(command), 'node_modules/@openai/codex');
  const require = createRequire(path.join(packageRoot, 'package.json'));
  const arm = process.arch === 'arm64';
  const triple = arm ? 'aarch64-pc-windows-msvc' : 'x86_64-pc-windows-msvc';
  let vendor;
  try {
    vendor = path.join(path.dirname(require.resolve(`@openai/codex-win32-${arm ? 'arm64' : 'x64'}/package.json`)), 'vendor');
  } catch { vendor = path.join(packageRoot, 'vendor'); }
  const executable = path.join(vendor, triple, 'bin/codex.exe');
  if (!existsSync(executable)) throw new Error('Native Codex executable not found. Supply -CodexExecutable with an absolute codex.exe path.');
  console.log(realpathSync(executable));
}
