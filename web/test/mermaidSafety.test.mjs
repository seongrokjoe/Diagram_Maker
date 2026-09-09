import assert from "node:assert/strict";
import test from "node:test";
import { mermaidSafetyError } from "../src/mermaidSafety.ts";

for (const source of [
  'flowchart LR\n a["http://localhost:5173 click 확인"] --> b["종료"]',
  'flowchart LR\n a[http://localhost:5173 click 확인] --> b[종료]',
  'flowchart LR\n a -. http://localhost:5173 click 확인 .-> b',
  'sequenceDiagram\n participant a as http://localhost:5173 click 확인\n a->>a: http://localhost:5173 click 요청',
  'classDiagram\n class a["http://localhost:5173 click 확인"]\n a : click http://localhost:5173',
  'stateDiagram-v2\n state "http://localhost:5173 click 확인" as a\n a --> a : http://localhost:5173 click 확인'
]) test(`display labels: ${source.split("\n")[0]}`, () => assert.equal(mermaidSafetyError(source), null));

for (const source of [
  'flowchart LR\n a["safe"]; click a "https://example.com"',
  'flowchart LR\n a["safe"] click a "https://example.com"',
  'flowchart LR\n click a call callback()',
  'classDiagram\n link a "https://example.com"',
  'stateDiagram-v2\n click a href "https://example.com"',
  'sequenceDiagram\n links a: {"web":"https://example.com"}',
  '%%{init: {securityLevel: "loose"}}%%\nflowchart LR',
  '---\nconfig:\n securityLevel: loose\n---\nflowchart LR',
  'flowchart LR\n a["<img src=x onerror=alert(1)>"]',
  'flowchart LR\n a@{ img: "https://example.com/a.png" }',
  'flowchart LR\n style a fill:url(https://example.com)',
  'sequenceDiagram\n a->>a: hello; click a "https://example.com"'
  , 'sequenceDiagram\n participant a as safe; links a: {"link":"https://example.com"}'
]) test(`blocked syntax: ${source}`, () => assert.match(mermaidSafetyError(source), /보안 차단/));
