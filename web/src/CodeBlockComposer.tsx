import { useState } from "react";
import { PresetPicker } from "./PresetPicker";
import { CodeBlockGroupRelations } from "./CodeBlockGroupRelations";
import type { CodeBlockDraft, CodeBlockGroup, CodeBlockInput } from "./codeBlockTypes";
import type { DiagramPreset, DiagramType } from "./types";
import { mergeGroups, moveBlock, nextTitle, reorderGroupBlock, typeLabels } from "./codeBlockWorkspaceState";

export function CodeBlockComposer({ draft, groups, presets, onChange }: {
  draft: CodeBlockDraft; groups: CodeBlockGroup[]; presets: DiagramPreset[]; onChange: (draft: CodeBlockDraft) => void;
}) {
  const [groupId, setGroupId] = useState(groups[0]?.id);
  const [opened, setOpened] = useState<Record<string, string | null>>({});
  const [viewId, setViewId] = useState("");
  const selectedGroup = groups.find(g => g.id === groupId) ?? groups[0];
  const blocks = selectedGroup.blockIds.map(id => draft.blocks.find(b => b.id === id)!).filter(Boolean);
  const remembered = opened[selectedGroup.id];
  const blockId = remembered === null ? null : blocks.some(b => b.id === remembered) ? remembered : blocks[0]?.id;
  const selectedView = selectedGroup.views?.find(v => v.id === viewId) ?? selectedGroup.views?.[0];
  function expand(id: string, group = selectedGroup.id) { setOpened(current => ({ ...current, [group]: id })); }
  function updateBlock(id: string, value: Partial<CodeBlockInput>) { onChange({ ...draft, blocks: draft.blocks.map(b => b.id === id ? { ...b, ...value } : b) }); }
  function updateGroup(value: Partial<CodeBlockGroup>) { onChange({ ...draft, groups: groups.map(g => g.id === selectedGroup.id ? { ...g, ...value } : g) }); }
  function move(id: string, target: CodeBlockGroup) { if (target.id === selectedGroup.id) return; onChange({ ...draft, groups: moveBlock(groups, id, target) }); expand(id, target.id); setGroupId(target.id); }
  function addGroup() {
    const group: CodeBlockGroup = { id: crypto.randomUUID(), title: nextTitle("그룹", groups.map(g => g.title)), blockIds: [], enableThinking: false, enableUserRelations: false };
    onChange({ ...draft, groups: [...groups, group] }); setGroupId(group.id);
  }
  function addBlock() {
    const b: CodeBlockInput = { id: crypto.randomUUID(), language: draft.blocks.at(-1)!.language, title: nextTitle("블럭", draft.blocks.map(b => b.title)), code: "", description: "" };
    onChange({ ...draft, blocks: [...draft.blocks, b], groups: groups.map(g => g.id === selectedGroup.id ? { ...g, blockIds: [...g.blockIds, b.id] } : g) }); expand(b.id);
  }
  function removeBlock(id: string) {
    onChange({ ...draft, blocks: draft.blocks.filter(b => b.id !== id), groups: groups.map(g => ({ ...g, blockIds: g.blockIds.filter(b => b !== id) })),
      relations: draft.relations?.filter(r => r.fromBlockId !== id && r.toBlockId !== id) });
  }
  return <div className="code-block-composer">
    <aside className="panel code-block-lists">
      <div className="code-block-add-actions"><button type="button" onClick={addBlock} disabled={draft.blocks.length >= 20}>+ 블럭 추가</button>
        <button type="button" onClick={addGroup} disabled={groups.length >= 20}>+ 그룹 추가</button></div>
      <h3>그룹 <small>{groups.length}개</small></h3>
      <div className="code-block-list" aria-label="그룹 목록">{groups.map(g => <button type="button" key={g.id} className="code-block-list-item"
        aria-pressed={selectedGroup.id === g.id} onClick={() => setGroupId(g.id)}>
        <span>{g.title}</span><small>블럭 {g.blockIds.length}개</small></button>)}</div>
    </aside>
    <div className="code-block-editing">
      <section className="panel code-block-group-header" aria-label="선택 그룹 설정">
        <label>그룹 제목<input aria-label="그룹 제목" maxLength={200} value={selectedGroup.title} onChange={e => updateGroup({ title: e.target.value })} /></label>
        <div className="code-block-group-options">
          <label><input type="checkbox" checked={selectedGroup.enableUserRelations ?? false} onChange={e => updateGroup({ enableUserRelations: e.target.checked })} />사용자 관계 추가</label>
          <label><input type="checkbox" checked={selectedGroup.enableThinking ?? false} onChange={e => updateGroup({ enableThinking: e.target.checked })} />Thinking</label>
        </div>
        <CodeBlockGroupRelations draft={draft} group={selectedGroup} blocks={blocks} onChange={onChange} />
        <details key={selectedGroup.id}><summary>그룹 관리</summary><div className="form-row">
          <label>그룹 병합<select aria-label="그룹 병합" value="" onChange={e => { const target = e.target.value; if (target) {
            onChange({ ...draft, groups: mergeGroups(groups, selectedGroup.id, target) }); setGroupId(target);
          } }}><option value="">합칠 대상 선택</option>{groups.filter(g => g.id !== selectedGroup.id).map(g => <option key={g.id} value={g.id}>{g.title}</option>)}</select></label>
          <button type="button" disabled={groups.length === 1 || blocks.length > 0} onClick={() => {
            const next = groups.filter(g => g.id !== selectedGroup.id); onChange({ ...draft, groups: next }); setGroupId(next[0].id);
          }}>빈 그룹 삭제</button></div><p className="help">블럭이 있는 그룹은 블럭을 이동한 뒤 삭제할 수 있습니다. 병합하면 대상 그룹의 옵션을 따릅니다.</p></details>
      </section>
      <div className="code-block-cards" aria-label="그룹 블럭 목록">
        {!blocks.length && <div className="panel empty-state">아직 블럭이 없습니다. 왼쪽의 ‘+ 블럭 추가’로 코드를 추가하세요. 빈 그룹은 생성에서 제외됩니다.</div>}
        {blocks.map((block, index) => <section className="panel code-block-card" key={block.id}>
          <div className="code-block-card-heading"><h3><button type="button" className="code-block-card-toggle" id={`block-tab-${block.id}`} aria-expanded={blockId === block.id} aria-controls={`block-panel-${block.id}`}
            onClick={() => setOpened(current => ({ ...current, [selectedGroup.id]: blockId === block.id ? null : block.id }))}>
            <span aria-hidden="true">{blockId === block.id ? "▾" : "▸"}</span> {block.title || `블럭 ${index + 1}`} <small>{block.language === "cpp" ? "C/C++" : "C#"}</small></button></h3>
            <div className="code-block-card-actions"><button type="button" aria-label={`블럭 ${index + 1} 위로`} disabled={index === 0} onClick={() => onChange(reorderGroupBlock(draft, selectedGroup.id, block.id, -1))}>↑</button>
              <button type="button" aria-label={`블럭 ${index + 1} 아래로`} disabled={index === blocks.length - 1} onClick={() => onChange(reorderGroupBlock(draft, selectedGroup.id, block.id, 1))}>↓</button>
              <button type="button" disabled={draft.blocks.length === 1} onClick={() => removeBlock(block.id)}>블럭 삭제</button></div></div>
          <div id={`block-panel-${block.id}`} role="region" aria-labelledby={`block-tab-${block.id}`} hidden={blockId !== block.id} className="code-block-card-body">
            <div className="form-row"><label>블럭 제목<input maxLength={200} value={block.title} onChange={e => updateBlock(block.id, { title: e.target.value })} /></label>
              <label>언어<select aria-label="언어" value={block.language} onChange={e => updateBlock(block.id, { language: e.target.value as CodeBlockInput["language"] })}><option value="cpp">C/C++</option><option value="csharp">C#</option></select></label>
              <label>소속 그룹<select aria-label="블럭 그룹 이동" value={selectedGroup.id} onChange={e => move(block.id, groups.find(g => g.id === e.target.value)!)}>
                {groups.map(g => <option key={g.id} value={g.id}>{g.title}</option>)}</select></label></div>
            <label>코드<textarea aria-label="코드" className="code-block-source" rows={14} spellCheck={false} maxLength={20000} value={block.code}
              placeholder="파일, 클래스, 함수 또는 함수 내부 구문을 붙여 넣으세요." onChange={e => updateBlock(block.id, { code: e.target.value })} /></label>
            <label>설명 (선택)<input maxLength={2000} value={block.description ?? ""} onChange={e => updateBlock(block.id, { description: e.target.value })} /></label>
          </div>
        </section>)}
      </div>
      <section className="panel" aria-label="그룹 출력 설정">
        <h3>그룹 출력 설정</h3>
        <p className="code-block-scope">이 그룹의 블럭 {selectedGroup.blockIds.length}개에 적용</p>
        <div className="form-row"><label>종류 추가<select aria-label="종류 추가" value="" disabled={(selectedGroup.views?.length ?? 0) >= 5} onChange={e => {
          if (!e.target.value) return; const type = e.target.value as DiagramType; const id = crypto.randomUUID();
          updateGroup({ views: [...(selectedGroup.views ?? []), { id, diagramType: type, presetId: presets.find(p => p.type === type && p.detailLevel === "balanced")?.id ?? "balanced" }] }); setViewId(id);
        }}><option value="">출력 종류 선택</option>{typeLabels.filter(([t]) => !selectedGroup.views?.some(v => v.diagramType === t)).map(([t, label]) => <option key={t} value={t}>{label}</option>)}</select></label>
          <span>{selectedGroup.views?.length ? `${selectedGroup.views.length} / 5종` : "선택하지 않으면 적합한 1종을 자동 추천합니다."}</span></div>
        {Boolean(selectedGroup.views?.length) && <div className="form-row"><label>선택한 출력<select aria-label="선택한 출력" value={selectedView?.id} onChange={e => setViewId(e.target.value)}>
          {selectedGroup.views?.map(v => <option key={v.id} value={v.id}>{typeLabels.find(([t]) => t === v.diagramType)?.[1]}</option>)}</select></label>
          <button onClick={() => updateGroup({ views: selectedGroup.views?.filter(v => v.id !== selectedView?.id) })}>출력 종류 제거</button></div>}
        {selectedView && <div className="code-block-view-options"><PresetPicker presets={presets.filter(p => p.type === selectedView.diagramType).map(neutralPreset)} selectedId={selectedView.presetId}
          onSelect={p => updateGroup({ views: selectedGroup.views?.map(v => v.id === selectedView.id ? { ...v, presetId: p.id } : v) })} />
          <label>상세도<select aria-label="상세도" value={selectedView.overrides?.detailLevel ?? "balanced"} onChange={e => updateGroup({ views: selectedGroup.views?.map(v => v.id === selectedView.id
            ? { ...v, overrides: { ...v.overrides, detailLevel: e.target.value as "compact" | "balanced" | "detailed" } } : v) })}><option value="compact">간결하게</option><option value="balanced">균형 있게</option><option value="detailed">자세하게</option></select></label>
          <label>보완 요청<input maxLength={2000} value={selectedView.refinementInstruction ?? ""} onChange={e => updateGroup({ views: selectedGroup.views?.map(v => v.id === selectedView.id ? { ...v, refinementInstruction: e.target.value } : v) })} /></label></div>}
      </section>
    </div>
  </div>;
}

function neutralPreset(preset: DiagramPreset): DiagramPreset {
  const neutral = (text: string) => text.replaceAll("변경 지점", "호출 지점").replaceAll("변경 구현", "코드 구현").replaceAll("변경된", "선택한").replaceAll("변경", "코드");
  return { ...preset, name: neutral(preset.name), description: neutral(preset.description), thumbnailDsl: neutral(preset.thumbnailDsl) };
}
