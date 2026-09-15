import test from "node:test";
import assert from "node:assert/strict";
import { prepareSequenceLayout } from "../src/sequenceLayout.ts";

test("long control labels reserve width for full identifiers and preserve source", () => {
  const identifier = "veryLongIdentifier".repeat(8);
  const source = `sequenceDiagram\nparticipant A as Main\nalt ${identifier} != Ready && allowed\nA->>A: 확인 및 저장 요청\nend`;
  const result = prepareSequenceLayout(source, text => text.length * 10);
  assert.ok(result.sequence.width >= identifier.length * 10 + 100);
  assert.ok(result.source.includes(identifier));
  assert.equal(result.source.replaceAll('<br/>', ' ').replace(/\s+/g, ' '), source.replace(/\s+/g, ' '));
});

test("other diagram formats and sequence syntax remain untouched", () => {
  const source = 'flowchart LR\n A["a"] --> B["b"]';
  assert.deepEqual(prepareSequenceLayout(source, () => 100), { source, sequence: undefined });
  const sequence = 'sequenceDiagram\nparticipant A as Actor\nNote over A: local state\nA->>A: Save(1)\n';
  assert.equal(prepareSequenceLayout(sequence, text => text.length).source, sequence);
});
