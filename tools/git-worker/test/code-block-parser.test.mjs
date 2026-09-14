import test from "node:test";
import assert from "node:assert/strict";
import { analyzeCodeBlocks } from "../code-block-parser.mjs";
import { readFile } from "node:fs/promises";
import path from "node:path";
const block = (id, code) => ({ id, title: id, code, language: "cpp" });

test("if decisions have distinct IDs from short circuit and ternary evaluation", async () => {
  const flatten = events => events.flatMap(e => [e, ...flatten(e.evaluation), ...flatten(e.children), ...flatten(e.alternative)]);
  for (const condition of ["First() && Second()", "First() || Second()", "Ready() ? First() : Second()"]) {
    const code = `// 한글 😀\r\nbool Run(){if(${condition})return false;Save();return true;}`;
    const graph = await analyzeCodeBlocks([block("a", code)]);
    const events = graph.symbols[0].execution;
    const facts = flatten(events);
    assert.equal(new Set(facts.map(e => e.id)).size, facts.length);
    assert.equal(events[0].expression, condition);
    assert.equal(code.slice(events[0].startOffset, events[0].endOffset), condition);
    assert.ok(events[0].evaluation.some(e => e.kind === "branch" && e.id !== events[0].id));
    assert.deepEqual(facts.filter(e => e.kind === "return").map(e => e.value), ["false", "true"]);
  }
});

test("nested argument regions retain their actual spans and loop transfers reference the mapped loop", async () => {
  const code = "void Run(){ Outer(0, First(), Second()); while(Check()){ if(Skip())continue; break; } }";
  const graph = await analyzeCodeBlocks([block("a", code)]);
  const all = events => events.flatMap(e => [e, ...all(e.evaluation), ...all(e.children), ...all(e.alternative)]);
  const facts = all(graph.symbols[0].execution);
  const unordered = facts.find(e => e.kind === "unordered");
  assert.deepEqual(unordered.children.map(e => code.slice(e.startOffset, e.endOffset)), ["First()", "Second()"]);
  const loop = facts.find(e => e.kind === "loop");
  for (const transfer of facts.filter(e => ["break", "continue"].includes(e.kind))) assert.equal(transfer.terminationTarget, loop.id);
  assert.equal(new Set(facts.map(e => e.id)).size, facts.length);
});

test("state observations cannot cross a call that can change the observed variable", async () => {
  const graph = await analyzeCodeBlocks([block("a", "void Tick(){ if(state==0){Mutate();state=1;} }")]);
  assert.equal(graph.transitions.length, 0);
});

test("short circuit calls stay conditional and unsupported control retains observed calls", async () => {
  const graph = await analyzeCodeBlocks([block("a", "void Run(){if(First() && Second())Save(); try{Work();}catch(...){Recover();}}")]);
  const branch = graph.symbols[0].execution[0];
  assert.equal(branch.evaluation[0].expression, "First()");
  assert.equal(branch.evaluation[1].children[0].expression, "Second()");
  assert.deepEqual(branch.evaluation[1].alternative, []);
  const unknown = graph.symbols[0].execution.find(e => e.kind === "unsupported");
  assert.deepEqual(unknown.children.filter(e => e.kind === "call").map(e => e.expression), ["Work()", "Recover()"]);
});

test("successive failure guards have seven call sites and six complete exit paths", async () => {
  const code = await readFile(process.env.DIAGRAMMAKER_TEST_FIXTURE_ROOT
    ? path.join(process.env.DIAGRAMMAKER_TEST_FIXTURE_ROOT, "SendInitDataRequest.cpp")
    : new URL("../../../tests/fixtures/SendInitDataRequest.cpp", import.meta.url), "utf8");
  const graph = await analyzeCodeBlocks([block("guards", code)]);
  const symbol = graph.symbols[0];
  const expected = ["IsInitDataRequestStatus()", "Sleep(10)", "SendUnitIntervalTime()", "SetUnitMode()",
    "RequestUpdateVersion()", "Sleep(100)", "RequestUpdateFirmwareVersion()"];
  assert.deepEqual(symbol.calls.map(c => c.statement), expected);
  assert.equal(symbol.calls[0].controlPath.length, 0);
  assert.equal(symbol.calls[4].controlPath.length, 3);
  const guards = symbol.execution.filter(e => e.kind === "branch");
  assert.equal(guards.length, 5);
  assert.ok(guards[4].expression.includes("enumFunctionResunt::Success"));
  assert.ok(guards.every(g => g.evaluation.length === 1 && g.evaluation[0].kind === "call" && g.children[0].kind === "return"));
  for (let failure = 0; failure < 6; failure++) {
    let condition = 0, value, returned;
    const calls = [];
    function run(events) {
      for (const event of events) {
        if (event.kind === "call") calls.push(event.expression);
        else if (event.kind === "declare" || event.kind === "assign") value = event.value;
        else if (event.kind === "branch") {
          run(event.evaluation);
          if (condition++ === failure && run(event.children)) return true;
        } else if (event.kind === "return") { returned = value; return true; }
      }
      return false;
    }
    run(symbol.execution);
    assert.deepEqual(calls, expected.slice(0, [1, 3, 4, 5, 7, 7][failure]));
    assert.equal(returned, failure === 5 ? "true" : "false");
  }
  const visit = events => events.flatMap(e => [e, ...visit(e.evaluation), ...visit(e.children), ...visit(e.alternative)]);
  const all = visit(symbol.execution);
  assert.equal(new Set(all.map(e => e.id)).size, all.length);
  for (const e of all) assert.ok(graph.evidence.some(ref => e.evidenceIds.includes(ref.id) && ref.location.startOffset === e.startOffset));
});

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

test("cast receivers resolve across blocks without fabricating cast calls", async () => {
  const graph = await analyzeCodeBlocks([
    block("entry", "void CCommand::Run(void *dummy) { static_cast<CCommand*>(dummy)->portMonThread(dummy); }"),
    block("worker", "BOOL CCommand::portMonThread(void *dummy) { return 1; }")
  ]);
  const entry = graph.symbols.find(s => s.name === "CCommand::Run");
  assert.equal(entry.calls.length, 1);
  assert.equal(entry.calls[0].targetSymbolId, graph.symbols.find(s => s.name === "CCommand::portMonThread").id);
  assert.equal(entry.calls[0].receiverType, "CCommand");
  assert.equal(entry.execution.filter(e => e.kind === "call").length, 1);
});

test("typed aliases, nested arguments and lexical shadowing retain honest targets", async () => {
  const graph = await analyzeCodeBlocks([block("all", `
    using Alias = Worker;
    void Worker::save(int value) {}
    void Other::save(int value) {}
    int load() { return 1; }
    void run(Worker* worker) {
      Alias* alias = static_cast<Alias*>(worker);
      alias->save(load());
      { Other* worker; worker->save(1); }
      worker->save(2);
    }`)]);
  const run = graph.symbols.find(s => s.name === "run");
  assert.deepEqual(run.calls.map(c => c.name), ["load", "save", "save", "save"]);
  assert.deepEqual(run.calls.map(c => graph.symbols.find(s => s.id === c.targetSymbolId)?.name), ["load", "Worker::save", "Other::save", "Worker::save"]);
});

test("unknown receivers and virtual dispatch do not become exact calls", async () => {
  const graph = await analyzeCodeBlocks([block("all", `
    struct Base { virtual void save(int value) {} };
    void run(Base* value) { value->save(1); missing->outside(); }
    void unrelated() {}`)]);
  const calls = graph.symbols.find(s => s.name === "run").calls;
  assert.equal(calls[0].targetSymbolId, null);
  assert.equal(calls[0].resolutionReason, "virtualDispatch");
  assert.deepEqual(calls[1].candidateSymbolIds, []);
});

test("receiver casts preserve lexical namespaces and distinguish overload argument types", async () => {
  const graph = await analyzeCodeBlocks([block("all", `
    namespace N {
      struct Worker { void save(int n){} void save(bool n){} };
      void run(void* value) {
        static_cast<Worker*>(value)->save(1);
        ((Worker*)value)->save(true);
      }
    }`)]);
  const calls = graph.symbols.find(s => s.name === "N::run").calls;
  assert.equal(calls.length, 2);
  assert.ok(calls.every(call => call.targetSymbolId));
  assert.notEqual(calls[0].targetSymbolId, calls[1].targetSymbolId);
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
