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
