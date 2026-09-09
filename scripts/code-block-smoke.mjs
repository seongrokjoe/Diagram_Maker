import assert from "node:assert/strict";

export async function smokeCodeBlocks(request, poll) {
  const source = "// 한글 😀\r\nenum S { Idle, Running } class App { S state; void Tick(){ if(state == S.Idle) { state = S.Running; Store.Save(1); } } }";
  const views = [["flowchart", "flow-vertical-overview"], ["sequence", "sequence-caller-context"], ["class", "class-related"], ["state", "state-horizontal"], ["code-relation", "code-class-grouped"]]
    .map(([diagramType, presetId]) => ({ id: diagramType, diagramType, presetId }));
  const input = { title: "코드 블럭 smoke", blocks: [{ id: "app", language: "csharp", title: "상태 처리", code: source },
    { id: "store", language: "csharp", title: "저장", code: "class Store { public static void Save(int value){} }" }],
    groups: [{ id: "main", title: "상태와 저장", blockIds: ["app", "store"], views }] };
  const workspace = await request("/code-block-workspaces", "POST", input, 201);
  const queued = await request(`/code-block-workspaces/${workspace.id}/runs`, "POST", { expectedRevision: 1 }, 202);
  const run = await poll(`/code-block-runs/${queued.id}`, ["Partial", "Completed"]);
  assert.equal(run.results[0].views.length, 5);
  assert.ok(!JSON.stringify(run).includes(source), "summaries omit source");
  for (const view of run.results[0].views) {
    assert.ok(view.pages.length, JSON.stringify(view));
    assert.equal(view.llmStatus, "Incomplete", "disabled LLM must not claim semantic success");
    for (const page of view.pages) {
      const base = `/code-block-runs/${run.id}/groups/main/views/${view.viewId}/pages/${page.id}`;
      const artifact = await request(base);
      assert.ok(artifact.ir.nodes.length);
      if (view.viewId === "flowchart" && page.id === "overview") assert.ok(artifact.ir.edges.some(e => e.type === "calls"), 'Flow overview preserves cross-block calls');
      assert.ok(!artifact.mermaidDsl.includes("__CodeBlock_") && !artifact.mermaidDsl.includes("__Fragment__"));
      assert.ok(artifact.ir.edges.every(e => e.relationOrigin === "code"));
      for (const id of new Set(artifact.ir.nodes.flatMap(n => n.evidenceIds))) {
        const evidence = await request(`/code-block-runs/${run.id}/evidence/${id}`);
        assert.ok(evidence.content.length); assert.equal(evidence.contentHash.length, 64);
      }
      if (page.id === view.pages[0].id) {
        const document = { title: artifact.ir.title, direction: artifact.ir.direction, nodes: artifact.ir.nodes.map(n => ({ id: n.id, label: n.label })),
          edges: artifact.ir.edges.map(e => ({ id: e.id, sourceId: e.sourceId, targetId: e.targetId, type: e.type, label: e.label })) };
        const edit = { rootArtifactId: artifact.id, expectedVersion: 1, document };
        await request(base + "/edit-preview", "POST", edit);
        await request(base + "/edits", "POST", edit);
        await request(base + "/edits", "POST", edit, 409);
      }
    }
  }
  await request(`/code-block-workspaces/${workspace.id}`, "PUT", { expectedRevision: 1, input: { ...input, title: "수정한 입력" } });
  assert.equal((await request(`/code-block-runs/${run.id}/input`)).blocks[0].code, source);
  await request(`/code-block-workspaces/${workspace.id}/runs`, "POST", { expectedRevision: 1 }, 409);
  const cpp = await request("/code-block-workspaces", "POST", { title: "C 호출", blocks: [
    { id: "a", language: "cpp", title: "호출", code: "int run(){return save(1);}" }, { id: "b", language: "cpp", title: "저장", code: "int save(int x){return x;}" }] }, 201);
  const cppQueued = await request(`/code-block-workspaces/${cpp.id}/runs`, "POST", { expectedRevision: 1 }, 202);
  const cppRun = await poll(`/code-block-runs/${cppQueued.id}`, ["Partial", "Completed"]);
  assert.equal(cppRun.groups.length, 1); assert.equal(cppRun.results[0].views[0].selection.diagramType, "sequence");
  const ambiguous = await request("/code-block-workspaces", "POST", { title: "관계 질문", blocks: [
    { id: "a", language: "csharp", title: "호출 조각", code: "void Run(){ Save(); }" }, { id: "b", language: "csharp", title: "저장 조각", code: "void Save(){}" }],
    groups: [{ id: "caller", title: "호출", blockIds: ["a"] }, { id: "callee", title: "저장", blockIds: ["b"] }] }, 201);
  const questionQueued = await request(`/code-block-workspaces/${ambiguous.id}/runs`, "POST", { expectedRevision: 1 }, 202);
  const waiting = await poll(`/code-block-runs/${questionQueued.id}`, ["NeedsClarification"]);
  assert.equal(waiting.questions.length, 1); assert.equal(waiting.groups.length, 2);
  const answered = await request(`/code-block-runs/${waiting.id}/answers`, "POST", { expectedInputRevision: 1, expectedRunRevision: waiting.revision, answers: [], skipRemaining: true }, 202);
  await poll(`/code-block-runs/${answered.id}`, ["Partial", "Completed"]);
  await request(`/code-block-runs/${waiting.id}/answers`, "POST", { expectedInputRevision: 1, expectedRunRevision: waiting.revision, answers: [], skipRemaining: true }, 409);
  await request(`/code-block-workspaces/${workspace.id}?expectedRevision=2`, "DELETE");
  await request(`/code-block-runs/${run.id}`, "GET", undefined, 404);
  console.log("Code block API smoke passed: five formats, C/C++, immutable evidence, clarification/resume, edit conflicts and deletion.");
}
