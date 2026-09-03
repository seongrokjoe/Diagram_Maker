import { useEffect, useState } from "react";
import { api } from "./api";
import type { IndirectCallRule, Repository } from "./types";

export function RepositoryRuleEditor({ repository, onSaved, reportError }: {
  repository: Repository;
  onSaved: (repository: Repository) => void;
  reportError: (message: string) => void;
}) {
  const [rules, setRules] = useState<IndirectCallRule[]>(repository.analysisRules?.indirectCalls ?? []);
  const [saving, setSaving] = useState(false);

  useEffect(() => setRules(repository.analysisRules?.indirectCalls ?? []), [repository]);

  function patchRule(id: string, patch: Partial<IndirectCallRule>) {
    setRules((current) => current.map((rule) => rule.id === id ? { ...rule, ...patch } : rule));
  }

  function addRule() {
    setRules((current) => [...current, {
      id: crypto.randomUUID(),
      name: "간접 호출 규칙 " + (current.length + 1),
      enabled: true,
      apiName: "RunFunction",
      targetTypeArgumentIndex: 0,
      targetMethodArgumentIndex: 1,
      aliases: [],
    }]);
  }

  async function save() {
    setSaving(true);
    reportError("");
    try {
      const updated = await api.updateRepositoryAnalysisRules(
        repository.id,
        repository.analysisRules?.revision ?? 0,
        rules,
      );
      onSaved(updated);
    } catch (reason) {
      reportError(reason instanceof Error ? reason.message : "간접 호출 규칙을 저장하지 못했습니다.");
    } finally {
      setSaving(false);
    }
  }

  return <details className="repository-rules">
    <summary>간접 호출 규칙 ({rules.length})</summary>
    <p className="help">문자열로 클래스와 메서드를 지정하는 C++ API를 등록합니다. 화면의 인자 번호는 1부터 시작합니다.</p>
    <div className="rule-stack">{rules.map((rule) => <article className="rule-card" key={rule.id}>
      <div className="rule-heading">
        <label className="checkbox"><input type="checkbox" checked={rule.enabled} onChange={(event) => patchRule(rule.id, { enabled: event.target.checked })} /> 사용</label>
        <button type="button" className="text-button" onClick={() => setRules((current) => current.filter((item) => item.id !== rule.id))}>삭제</button>
      </div>
      <div className="field-row">
        <label>규칙 이름<input value={rule.name} maxLength={120} onChange={(event) => patchRule(rule.id, { name: event.target.value })} /></label>
        <label>API 함수명<input value={rule.apiName} maxLength={160} placeholder="RunFunction" onChange={(event) => patchRule(rule.id, { apiName: event.target.value })} /></label>
      </div>
      <div className="field-row">
        <label>클래스 인자 번호<input type="number" min={1} max={32} value={rule.targetTypeArgumentIndex + 1} onChange={(event) => patchRule(rule.id, { targetTypeArgumentIndex: Math.max(0, Number(event.target.value) - 1) })} /></label>
        <label>메서드 인자 번호 (선택)<input type="number" min={1} max={32} value={rule.targetMethodArgumentIndex === undefined || rule.targetMethodArgumentIndex === null ? "" : rule.targetMethodArgumentIndex + 1} onChange={(event) => patchRule(rule.id, { targetMethodArgumentIndex: event.target.value ? Math.max(0, Number(event.target.value) - 1) : undefined })} /></label>
      </div>
      <details className="alias-editor"><summary>변수 별칭 ({rule.aliases.length})</summary>
        {rule.aliases.map((alias, index) => <div className="alias-row" key={rule.id + "-" + index}>
          <input aria-label="별칭 표현식" value={alias.expression} placeholder="m_strFunctionOprXfer" onChange={(event) => patchRule(rule.id, { aliases: rule.aliases.map((item, itemIndex) => itemIndex === index ? { ...item, expression: event.target.value } : item) })} />
          <span>→</span>
          <input aria-label="대상 클래스" value={alias.targetType} placeholder="Opr_Xfer" onChange={(event) => patchRule(rule.id, { aliases: rule.aliases.map((item, itemIndex) => itemIndex === index ? { ...item, targetType: event.target.value } : item) })} />
          <button type="button" className="text-button" onClick={() => patchRule(rule.id, { aliases: rule.aliases.filter((_, itemIndex) => itemIndex !== index) })}>삭제</button>
        </div>)}
        <button type="button" className="secondary" onClick={() => patchRule(rule.id, { aliases: [...rule.aliases, { expression: "", targetType: "" }] })}>별칭 추가</button>
      </details>
    </article>)}</div>
    <div className="button-row">
      <button type="button" className="secondary" onClick={addRule}>규칙 추가</button>
      <button type="button" className="primary" disabled={saving} onClick={() => void save()}>{saving ? "저장 중…" : "규칙 저장"}</button>
    </div>
  </details>;
}
