import test from "node:test";
import assert from "node:assert/strict";
import { analyzeCodeBlocks } from "../code-block-parser.mjs";
const block = (id, code) => ({ id, title: id, code, language: "cpp" });

test("pasted C functions retain cross-block calls and UTF-16 evidence", async () => {
  const code = '// 한글 😀\r\nint foo(int x) { if(x > 0) return bar(x); return 0; }';
  const g = await analyzeCodeBlocks([block("a", code), block("b", "int bar(int x) { return x; }")]);
  const call = g.symbols.find(s => s.name === "foo").calls[0];
  assert.equal(code.slice(call.location.startOffset, call.location.endOffset), "bar(x)");
  assert.equal(call.location.startLine, 2);
  assert.equal(call.targetSymbolId, g.symbols.find(s => s.name === "bar").id);
  const returnedCall = g.symbols.find(s => s.name === "foo").steps.find(s => s.label.includes("bar"));
  assert.equal(returnedCall.statement, "return bar(x);");
  assert.ok(returnedCall.location.startOffset <= call.location.startOffset && returnedCall.location.endOffset >= call.location.endOffset);
});
test("fragments expose real statements without a synthetic function name", async () => {
  const g = await analyzeCodeBlocks([block("fragment", "if (ready) { save(); } return;")]);
  assert.equal(g.symbols[0].kind, "fragment");
  assert.equal(g.symbols[0].name, "fragment");
  assert.ok(g.symbols[0].steps.some(s => s.kind === "condition"));
  assert.ok(g.evidence.every(e => e.location.endOffset <= 30));
});
test("duplicates, receivers, overloads and file-local functions are not name-only calls", async () => {
  for (const target of ["int bar(int x){return x;}", "static int bar(int x){return x;}"]) {
    const g = await analyzeCodeBlocks([block("a", "int foo(){ return obj.bar(1); }"), block("b", target)]);
    assert.equal(g.symbols[0].calls[0].targetSymbolId, null);
  }
  const g = await analyzeCodeBlocks([block("a", "void foo(){bar(1);}"), block("b", "void bar(int x){}"), block("c", "void bar(int x){}")]);
  assert.equal(g.symbols[0].calls[0].targetSymbolId, null);
  assert.equal(g.symbols[0].calls[0].candidateSymbolIds.length, 2);
});
test("state transitions require a same-variable guard and assignment", async () => {
  const g = await analyzeCodeBlocks([block("state", "void tick(){if(state == Idle) {state = Running;}}")]);
  assert.equal(g.transitions.length, 1);
  assert.equal(g.transitions[0].from, "Idle");
  assert.equal(g.transitions[0].to, "Running");
  const enumOnly = await analyzeCodeBlocks([block("enum", "enum State { Idle, Running };")]);
  assert.equal(enumOnly.transitions.length, 0);
  assert.ok(enumOnly.warnings.length);
});
test("malformed input reports recovery and never executes includes", async () => {
  const g = await analyzeCodeBlocks([block("bad", '#include "C:/missing/secret.h"\nvoid f(){ if (')]);
  assert.ok(g.warnings.length);
});
test("callback shadowing and unreachable state writes are not confirmed", async () => {
  const callback = await analyzeCodeBlocks([block("a", "void run(void (*save)(int)){ save(1); }"), block("b", "void save(int x){}")]);
  assert.equal(callback.symbols[0].calls[0].targetSymbolId, null);
  const state = await analyzeCodeBlocks([block("s", "void tick(){ if(state == Idle){ return; state = Running; } }")]);
  assert.equal(state.transitions.length, 0);
});
test("early-return guards survive on later calls and dead calls are omitted", async () => {
  const graph = await analyzeCodeBlocks([block("a", "void run(){if(!ready)return; save(1); return; save(2);}"), block("b", "void save(int x){}")]);
  const calls = graph.symbols[0].calls;
  assert.equal(calls.length, 1);
  assert.equal(calls[0].controlPath[0].branch, "else");
  assert.ok(calls[0].controlPath[0].label.includes("!ready"));
});
test("real C++ base type syntax is retained", async () => {
  const graph = await analyzeCodeBlocks([block("a", "struct Base {}; struct Derived : public Base {};")]);
  assert.deepEqual(graph.symbols.find(s => s.name === "Derived").baseTypes, ["Base"]);
  assert.notEqual(graph.symbols[0].location.startOffset, graph.symbols[1].location.startOffset);
});
