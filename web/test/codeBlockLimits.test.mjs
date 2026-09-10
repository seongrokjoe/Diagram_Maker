import assert from 'node:assert/strict';
import test from 'node:test';
import { codeInputError, defaultCodeBlockLimits as limits } from '../src/codeBlockLimits.ts';
const draft = code => ({ blocks: [{ title: '테스트', code }] });
test('large pasted code is retained and limits are inclusive', () => {
  const source = '한글\r\n😀'.repeat(20000).slice(0, 100000);
  assert.equal(codeInputError(draft(source), limits), null);
  const pasted = draft(source + 'x');
  assert.match(codeInputError(pasted, limits), /1자 초과/);
  assert.equal(pasted.blocks[0].code, source + 'x');
  const full = { blocks: Array.from({ length: 10 }, () => ({ title: '코드', code: source })) };
  assert.equal(codeInputError(full, limits), null);
  assert.match(codeInputError({ blocks: [...full.blocks, { code: 'x' }] }, limits), /전체 코드/);
});
