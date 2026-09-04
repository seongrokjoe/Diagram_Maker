import test from "node:test";
import assert from "node:assert/strict";
import { parseCppFile, resolveCppCalls } from "../cpp-indexer.mjs";

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
  assert.equal(resolved.ambiguousCallCount, 1);
  assert.equal(resolved.edges.length, 0);
  assert.equal(resolved.excludedCallCount, 1);
  assert.equal(resolved.excludedCalls[0].reason, "multipleTargets");
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
