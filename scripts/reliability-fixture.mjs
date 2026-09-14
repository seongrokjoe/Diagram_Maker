// Fixed synthetic input shared by regression tests and the onsite measurement kit.
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const reliabilitySource = increment => Array.from({ length: 4 }, (_, c) => `class Example${c} {\npublic:\n` +
  Array.from({ length: 10 }, (_, m) => `  int Task${m}(int input) {\n` +
    Array.from({ length: 27 }, (_, i) => `    input += ${i + increment};`).join('\n') + '\n    return input;\n  }').join('\n') + '\n};').join('\n');

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) throw new Error('An output directory is required.');
  const output = path.resolve(process.argv[2]);
  await mkdir(output, { recursive: true });
  for (const [name, increment] of [['before.cpp', 1], ['after.cpp', 2]])
    await writeFile(path.join(output, name), reliabilitySource(increment), { flag: 'wx' });
}
