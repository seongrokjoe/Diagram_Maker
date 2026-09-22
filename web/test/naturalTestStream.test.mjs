import assert from 'node:assert/strict';
import test from 'node:test';
import { readNaturalTestStream } from '../src/naturalTestStream.ts';

test('NDJSON preserves Korean characters split across bytes and multiple events', async () => {
  const bytes = new TextEncoder().encode('{"type":"progress","stage":"의미 검토"}\n{"type":"result"}');
  const body = new ReadableStream({ start(controller) {
    for (const byte of bytes) controller.enqueue(new Uint8Array([byte])); controller.close();
  } });
  const events = []; await readNaturalTestStream(body, event => events.push(event));
  assert.deepEqual(events, [{ type: 'progress', stage: '의미 검토' }, { type: 'result' }]);
});
test('broken event stream rejects while preserving received progress', async () => {
  const events = [];
  const body = new ReadableStream({ start(controller) {
    controller.enqueue(new TextEncoder().encode('{"type":"progress"}\n{"type":')); controller.close();
  } });
  await assert.rejects(readNaturalTestStream(body, event => events.push(event)), SyntaxError);
  assert.equal(events.length, 1);
});
