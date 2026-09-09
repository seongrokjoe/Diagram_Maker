import type { CodeBlockDraft, CodeBlockGroup, CodeBlockInput, CodeBlockRelation } from "./codeBlockTypes";

export function CodeBlockGroupRelations({ draft, group, blocks, onChange }: {
  draft: CodeBlockDraft; group: CodeBlockGroup; blocks: CodeBlockInput[]; onChange: (draft: CodeBlockDraft) => void;
}) {
  const relations = draft.relations?.filter(r => group.blockIds.includes(r.fromBlockId)) ?? [];
  function update(id: string, value: Partial<CodeBlockRelation>) {
    onChange({ ...draft, relations: draft.relations?.map(r => r.id === id ? { ...r, ...value } : r) });
  }
  return <div className="code-block-relations" aria-label="그룹 사용자 관계" hidden={!group.enableUserRelations}>
    <p className="help">이 그룹의 블럭 사이에 관계를 추가합니다. 사용자 제공 관계는 코드 관계도에 표시되며 실행 위치나 순서를 확정하지 않습니다.</p>
    {relations.map(r => {
      const excluded = !group.blockIds.includes(r.toBlockId);
      return <div key={r.id} className="code-block-relation">
        {excluded && <p className="warning">적용 제외 · 도착 블럭이 다른 그룹에 있습니다. 같은 그룹의 블럭으로 변경하면 다시 적용됩니다.</p>}
        <div className="form-row">
          <label>출발 블럭<select aria-label="출발 블럭" value={r.fromBlockId} onChange={e => update(r.id, { fromBlockId: e.target.value })}>
            {blocks.map(b => <option key={b.id} value={b.id}>{b.title}</option>)}</select></label><span>→</span>
          <label>도착 블럭<select aria-label="도착 블럭" value={excluded ? "" : r.toBlockId} onChange={e => update(r.id, { toBlockId: e.target.value })}>
            {excluded && <option value="" disabled>{draft.blocks.find(b => b.id === r.toBlockId)?.title} (다른 그룹)</option>}
            {blocks.map(b => <option key={b.id} value={b.id}>{b.title}</option>)}</select></label>
          <label>관계 종류<select aria-label="관계 종류" value={r.kind} onChange={e => update(r.id, { kind: e.target.value as CodeBlockRelation["kind"] })}>
            <option value="calls">호출</option><option value="dataflow">데이터 전달</option><option value="uses">의존</option></select></label>
          <label>관계 설명<input aria-label="관계 설명" placeholder="관계 설명" value={r.description} maxLength={500} onChange={e => update(r.id, { description: e.target.value })} /></label>
          <button type="button" onClick={() => onChange({ ...draft, relations: draft.relations?.filter(x => x.id !== r.id) })}>관계 삭제</button>
        </div>
      </div>;
    })}
    <button type="button" disabled={!blocks.length || (draft.relations?.length ?? 0) >= 200} onClick={() => onChange({ ...draft, relations: [...(draft.relations ?? []),
      { id: crypto.randomUUID(), fromBlockId: blocks[0].id, toBlockId: blocks.at(-1)!.id, kind: "calls", origin: "user", description: "" }] })}>관계 추가</button>
  </div>;
}
