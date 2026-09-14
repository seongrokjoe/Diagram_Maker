import test from "node:test";
import assert from "node:assert/strict";
import { parseCppFile, resolveCppCalls } from "../cpp-indexer.mjs";

test("Git resolves cast receivers but does not replace local callbacks with global functions", async () => {
  const parsed = await parseCppFile("Calls.cpp", `
    void save(int n){}
    struct Worker { void save(int n){} };
    void run(void* value, void (*save)(int)) {
      static_cast<Worker*>(value)->save(1);
      save(2);
    }`);
  const resolution = resolveCppCalls([parsed]);
  const run = parsed.symbols.find(s => s.simpleName === "run");
  const calls = resolution.edges.filter(e => e.sourceSemanticKey === run.semanticKey);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].targetSemanticKey, parsed.symbols.find(s => s.qualifiedName === "Worker::save").semanticKey);
  assert.equal(run.calls[1].resolutionReason, "localCallable");
});

test("switch preserves fallthrough, no-match and break destinations", async () => {
  const parsed = await parseCppFile("Switch.cpp", `void Save() {} void Run(int n) {
    switch(n) { case 1: n++; case 2: Save(); break; } Save();
  }`);
  const run = parsed.symbols.find(symbol => symbol.simpleName === "Run");
  const nodes = run.controlNodes;
  const edges = run.controlEdges;
  const decision = nodes.find(node => node.label.startsWith("switch"));
  const secondCase = nodes.find(node => node.label === "case 2");
  assert.ok(edges.some(edge => edge.targetId === secondCase.id && edge.label === "fallthrough"));
  assert.ok(edges.some(edge => edge.sourceId === decision.id && edge.label === "일치 없음"));
  const broken = nodes.find(node => node.kind === "break");
  const after = edges.find(edge => edge.sourceId === broken.id);
  assert.equal(nodes.find(node => node.id === after.targetId).kind, "call");
});

test("do while enters the body first and return has no unreachable successors", async () => {
  const parsed = await parseCppFile("Loop.cpp", `void Save() {} void Run() {
    do { Save(); } while(false); return; Save();
  }`);
  const run = parsed.symbols.find(symbol => symbol.simpleName === "Run");
  const entry = run.controlNodes.find(node => node.kind === "entry");
  const next = run.controlEdges.find(edge => edge.sourceId === entry.id);
  assert.notEqual(run.controlNodes.find(node => node.id === next.targetId).kind, "loop");
  assert.equal(run.controlNodes.filter(node => node.kind === "call").length, 1);
});

test("same-line calls remain distinct and nested calls precede the enclosing call", async () => {
  const parsed = await parseCppFile("Calls.cpp", `int Parse() { return 1; } void Save(int n) {} void Run() { Save(Parse()); Save(1); Save(2); }`);
  const run = parsed.symbols.find(symbol => symbol.simpleName === "Run");
  assert.deepEqual(run.calls.map(call => call.name), ["Parse", "Save", "Save", "Save"]);
  assert.equal(new Set(run.calls.map(call => call.startOffset)).size, 4);
  assert.equal(resolveCppCalls([parsed]).edges.length, 4);
});

test("C++ sibling argument calls explicitly retain evaluation order uncertainty", async () => {
  const parsed = await parseCppFile("Arguments.cpp", `int Read() { return 1; } int Next() { return 2; } void Save(int a, int b) {} void Run() { Save(Read(), Next()); }`);
  const run = parsed.symbols.find(symbol => symbol.simpleName === "Run");
  assert.ok(run.calls.filter(call => call.name !== "Save").every(call => call.controlPath.some(scope => scope.kind === "unordered")));
});

test("sequence facts distinguish switch branches and make fallthrough uncertainty explicit", async () => {
  const parsed = await parseCppFile("Switch.cpp", `void Save() {} void Run(int n) {
    switch(n) { case 1: Save(); break; default: Save(); break; }
    switch(n) { case 1: Save(); default: Save(); }
  }`);
  const calls = parsed.symbols.find(symbol => symbol.simpleName === "Run").calls;
  assert.deepEqual(calls.slice(0, 2).map(call => call.controlPath[0].branch), ["case 1", "default"]);
  assert.ok(calls.slice(0, 2).every(call => call.controlPath[0].kind === "alt"));
  assert.ok(calls.slice(2).every(call => call.controlPath[0].kind === "unordered"));
});

test("method owner preserves class versus struct identity and excludes namespace-only qualification", async () => {
  const parsed = await parseCppFile("Owners.cpp", `struct S { void Run() {} }; class C { public: void Save() {} }; namespace N { void Read() {} }`);
  const resolved = resolveCppCalls([parsed]);
  const run = resolved.symbols.find(symbol => symbol.simpleName === "Run");
  assert.equal(run.ownerSemanticKey, "type:S");
  assert.equal(run.ownerKind, "type");
  assert.equal(resolved.symbols.find(symbol => symbol.simpleName === "Save").ownerKind, "class");
  assert.equal(resolved.symbols.find(symbol => symbol.simpleName === "Read").ownerSemanticKey, undefined);
});

test("C++ overload identities include canonical parameter types and qualifiers", async () => {
  const parsed = await parseCppFile("Service.cpp", `
    struct Service {
      void Save(int value = 1) const & {}
      void Save(const char* value) {}
      void Run() { Save(1); }
    };
  `);
  const overloads = parsed.symbols.filter((symbol) => symbol.simpleName === "Save");

  assert.equal(overloads.length, 2);
  assert.equal(new Set(overloads.map((symbol) => symbol.semanticKey)).size, 2);
  assert.ok(overloads.some((symbol) => symbol.semanticKey === "function:Service::Save(int) const&"));
  assert.ok(overloads.some((symbol) => symbol.semanticKey === "function:Service::Save(const char*)"));

  const resolved = resolveCppCalls([parsed]);
  assert.equal(resolved.ambiguousCallCount, 0);
  assert.equal(resolved.edges.length, 1);
  assert.equal(resolved.edges[0].targetSemanticKey, "function:Service::Save(int) const&");
  assert.equal(resolved.edges[0].confidence, "Exact");
});

test("C++ call resolver keeps a unique name and arity match", async () => {
  const parsed = await parseCppFile("Service.cpp", `
    struct Service {
      void Save() {}
      void Save(int value) {}
      void Run() { Save(); }
    };
  `);

  const resolved = resolveCppCalls([parsed]);
  assert.equal(resolved.ambiguousCallCount, 0);
  assert.equal(resolved.edges.length, 1);
  const [edge] = resolved.edges;
  assert.equal(edge.sourceSemanticKey, "function:Service::Run()");
  assert.equal(edge.targetSemanticKey, "function:Service::Save()");
});

test("C++ indexer preserves method control flow and indirect API calls", async () => {
  const parsed = await parseCppFile("Service.cpp", `
    #define OPR_XFER_NAME "Opr_Xfer"
    struct Opr_Xfer {
      void runOrgReturn() {}
    };
    struct InterfaceCustom {
      const char* m_strFunctionOprXfer = OPR_XFER_NAME;
      void Execute() {
        for (int index = 0; index < 2; ++index) {
          if (index == 1) break;
          if (index > 0) {
            RunFunction(m_strFunctionOprXfer, "runOrgReturn");
          }
        }
        return;
      }
    };
  `);
  const resolved = resolveCppCalls([parsed], [{
    id: "run-function",
    name: "RunFunction",
    enabled: true,
    apiName: "RunFunction",
    targetTypeArgumentIndex: 0,
    targetMethodArgumentIndex: 1,
    aliases: [],
  }]);

  const execute = resolved.symbols.find((symbol) => symbol.qualifiedName === "InterfaceCustom::Execute");
  assert.ok(execute);
  assert.ok(execute.controlNodes.some((node) => node.kind === "loop"));
  assert.ok(execute.controlNodes.some((node) => node.kind === "condition"));
  assert.ok(execute.controlNodes.some((node) => node.kind === "break"));
  assert.ok(execute.controlNodes.some((node) => node.kind === "return"));
  const edge = resolved.edges.find((item) => item.sourceSemanticKey === execute.semanticKey);
  assert.equal(edge.targetSemanticKey, "function:Opr_Xfer::runOrgReturn()");
  assert.equal(edge.isIndirect, true);
  assert.equal(edge.viaApi, "RunFunction");
  assert.ok(edge.endLine >= edge.line);
  assert.equal(edge.controlPath.map((scope) => scope.kind).join(","), "loop,alt");
  const loopNode = execute.controlNodes.find((node) => node.kind === "loop");
  assert.ok(loopNode.endLine < execute.endLine, "loop range should cover the header rather than the whole body");
  const breakNode = execute.controlNodes.find((node) => node.kind === "break");
  assert.ok(!execute.controlEdges.some((item) => item.sourceId === breakNode.id && item.type === "loopBack"));
  assert.equal(resolved.excludedCallCount, 0);
});

test("C++ control flow preserves assignments, suppresses nested calls, and groups adjacent simple assignments", async () => {
  const parsed = await parseCppFile("Service.cpp", `
    struct Service {
      const char* GetCJInfo(int value) { return "value"; }
      void Execute(int temp) {
        auto cjData = GetCJInfo(temp);
        WRITE_LOG(true, "TEST %s", cjData.c_str());
        int a = 0;
        int b = 1;
        int c = 10;
      }
    };
  `);

  const execute = parsed.symbols.find((symbol) => symbol.qualifiedName === "Service::Execute");
  assert.ok(execute);
  const labels = execute.controlNodes.map((node) => node.label);
  assert.ok(labels.includes("auto cjData = GetCJInfo(temp);"));
  assert.ok(labels.includes('WRITE_LOG(true, "TEST %s", cjData.c_str());'));
  assert.ok(!labels.some((label) => label === "GetCJInfo(temp)" || label === "cjData.c_str()"));
  assert.ok(labels.includes("int a = 0;\nint b = 1;\nint c = 10;"));
});

test("C++ indexer extracts member visibility and separates switch cases", async () => {
  const parsed = await parseCppFile("Service.cpp", `
    class Service {
      int count, limit;
    public:
      void Execute(int kind) {
        switch (kind) {
          case 1: Save(); break;
          case 2: Load(); break;
          default: Reset(); break;
        }
      }
      void Save() {}
      void Load() {}
      void Reset() {}
    };
  `);

  const service = parsed.symbols.find((symbol) => symbol.qualifiedName === "Service" && symbol.kind === "class");
  assert.ok(service);
  assert.ok(service.members.some((member) => member.name === "count" && member.accessibility === "private" && member.kind === "field"));
  assert.ok(service.members.some((member) => member.name === "limit" && member.accessibility === "private" && member.kind === "field"));
  assert.ok(service.members.some((member) => member.name === "Execute" && member.accessibility === "public" && member.kind === "method"));
  const execute = parsed.symbols.find((symbol) => symbol.qualifiedName === "Service::Execute");
  const branchLabels = execute.controlEdges.map((edge) => edge.label);
  assert.ok(branchLabels.includes("case 1"));
  assert.ok(branchLabels.includes("case 2"));
  assert.ok(branchLabels.includes("default"));
});

test("indirect API uses an explicit alias and reports unresolved targets", async () => {
  const parsed = await parseCppFile("Service.cpp", `
    struct Opr_Xfer { void runOrgReturn() {} };
    struct InterfaceCustom {
      void Execute() { RunFunction(runtimeTarget, "runOrgReturn"); }
      void Broken() { RunFunction(unknownTarget, "runOrgReturn"); }
    };
  `);
  const resolved = resolveCppCalls([parsed], [{
    id: "run-function",
    name: "RunFunction",
    enabled: true,
    apiName: "RunFunction",
    targetTypeArgumentIndex: 0,
    targetMethodArgumentIndex: 1,
    aliases: [{ expression: "runtimeTarget", targetType: "Opr_Xfer" }],
  }]);

  assert.equal(resolved.edges.length, 1);
  assert.equal(resolved.edges[0].confidence, "Inferred");
  assert.equal(resolved.excludedCallCount, 1);
  assert.equal(resolved.excludedCalls[0].reason, "indirectTypeUnresolved");
});
test("Unicode parameter names and defaults preserve canonical C++ types", async () => {
  const parsed = await parseCppFile("unicode.cpp", 'void save(const char* 메시지 = "한글 😀", int 횟수 = 1) {}');
  assert.equal(parsed.symbols[0].semanticKey, "function:save(const char*,int)");
});
