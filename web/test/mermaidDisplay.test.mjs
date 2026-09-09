import assert from "node:assert/strict";
import test from "node:test";
import { prepareMermaidDisplay } from "../src/mermaidDisplay.ts";

test("class display URLs survive Markdown layout without changing the saved source", () => {
  const source = 'classDiagram\n class a["http://localhost:5173 click 요청, https://example.invalid, www.example.invalid"]';
  const prepared = prepareMermaidDisplay(source);
  assert.ok(prepared.marker);
  assert.doesNotMatch(prepared.source, /https?:\/\/|www\./);
  assert.equal(prepared.source.split(prepared.marker).join(""), source);
});

test("existing zero-width label characters survive the display workaround", () => {
  const source = 'classDiagram\n class a["원본\u2060표시 http://localhost:5173"]';
  const prepared = prepareMermaidDisplay(source);
  assert.notEqual(prepared.marker, "\u2060");
  assert.equal(prepared.source.split(prepared.marker).join(""), source);
});

test("State nodes and transition labels retain display URLs", () => {
  const source = 'stateDiagram-v2\n state "http://localhost:5173 click 요청" as a\n a --> b : https://example.invalid 요청';
  const prepared = prepareMermaidDisplay(source);
  assert.ok(prepared.marker);
  assert.doesNotMatch(prepared.source, /https?:\/\//);
  assert.equal(prepared.source.split(prepared.marker).join(""), source);
});

test("display preparation does not rewrite link commands or other diagram syntax", () => {
  for (const source of ['classDiagram\n class a\n click a href https://example.invalid', 'flowchart LR\n a["http://localhost:5173"]'])
    assert.deepEqual(prepareMermaidDisplay(source), { source, marker: "" });
});
