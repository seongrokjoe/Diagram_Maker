import { useEffect, useMemo, useState } from "react";
import type { DiagramArtifact } from "./types";
import { buildEvidenceItems, evidencePage, filterEvidenceItems } from "./resultPresentation";

export function ResultNotices({ notices, title = "분석 안내·주의사항" }: { notices: string[]; title?: string }) {
  const [open, setOpen] = useState(false);
  const unique = [...new Set(notices.filter(notice => notice.trim()))];
  if (!unique.length) return null;
  return <details className="result-notices" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>{title} <span className="result-count">{unique.length}건</span><span className="disclosure-hint">{open ? "접기" : "펼쳐보기"}</span></summary>
    {open && <ul className="result-notice-list">{unique.map(notice => <li key={notice}>{notice}</li>)}</ul>}
  </details>;
}

export function EvidenceBrowser({ evidenceIds, diagram, onEvidence, title = "코드 근거" }: {
  evidenceIds: string[];
  diagram?: DiagramArtifact["ir"];
  onEvidence: (id: string) => void;
  title?: string;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [pageIndex, setPageIndex] = useState(0);
  const [selectedId, setSelectedId] = useState("");
  const items = useMemo(() => buildEvidenceItems(evidenceIds, diagram), [evidenceIds, diagram]);
  const filtered = useMemo(() => filterEvidenceItems(items, query), [items, query]);
  const page = evidencePage(filtered, pageIndex);
  useEffect(() => { setQuery(""); setPageIndex(0); setSelectedId(""); }, [evidenceIds]);
  return <details className="evidence-browser" onToggle={event => setOpen(event.currentTarget.open)}>
    <summary>{title} <span className="result-count">{items.length}건</span><span className="disclosure-hint">{open ? "접기" : "펼쳐보기"}</span></summary>
    {open && <div className="evidence-browser-content">
      {items.length > 0 ? <>
        <label>근거 검색<input type="search" value={query} placeholder="근거 번호, 관련 단계 또는 ID"
          onChange={event => { setQuery(event.target.value); setPageIndex(0); }} /></label>
        <p className="help">목록에서 선택한 근거의 코드만 불러옵니다. 한 번에 최대 20개를 표시합니다.</p>
        <div className="evidence-results">{page.items.map(item => <button type="button" className="evidence-item" key={item.id}
          aria-pressed={selectedId === item.id} title={item.contextLabel ? `관련 단계: ${item.contextLabel}` : item.id}
          onClick={() => { setSelectedId(item.id); onEvidence(item.id); }}>
          <strong>근거 {item.number}</strong><span>{item.contextLabel || "관련 코드 확인"}</span><span aria-hidden="true">→</span>
        </button>)}</div>
        {!filtered.length && <p className="help" role="status">검색 결과가 없습니다.</p>}
        <nav className="evidence-pagination" aria-label="코드 근거 목록 페이지">
          <button type="button" className="secondary" disabled={page.pageIndex === 0} onClick={() => setPageIndex(page.pageIndex - 1)}>이전 근거</button>
          <span role="status">{page.first}–{page.last} / {filtered.length}건 · {page.pageIndex + 1}/{page.totalPages} 페이지</span>
          <button type="button" className="secondary" disabled={page.pageIndex + 1 >= page.totalPages} onClick={() => setPageIndex(page.pageIndex + 1)}>다음 근거</button>
        </nav>
      </> : <p className="help">이 페이지에 연결된 코드 근거가 없습니다.</p>}
    </div>}
  </details>;
}
