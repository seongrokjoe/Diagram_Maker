import { useEffect, useRef, useState } from "react";
import { api } from "./api";
import type { GitCommit } from "./types";

const pageSize = 50;

export function CommitPicker({
  repositoryId,
  defaultBranch,
  label,
  value,
  autoSelectFirst = false,
  onSelect,
}: {
  repositoryId: string;
  defaultBranch: string;
  label: string;
  value: string;
  autoSelectFirst?: boolean;
  onSelect: (commit: GitCommit | null) => void;
}) {
  const [query, setQuery] = useState("");
  const [debouncedQuery, setDebouncedQuery] = useState("");
  const [commits, setCommits] = useState<GitCommit[]>([]);
  const [selected, setSelected] = useState<GitCommit | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [shaInput, setShaInput] = useState("");
  const [resolving, setResolving] = useState(false);
  const [shaError, setShaError] = useState("");
  const requestSequence = useRef(0);

  useEffect(() => {
    const normalized = query.trim();
    const timer = window.setTimeout(() => setDebouncedQuery(normalized.length >= 2 ? normalized : ""), 300);
    return () => window.clearTimeout(timer);
  }, [query]);

  useEffect(() => {
    setQuery("");
    setDebouncedQuery("");
    setCommits([]);
    setSelected(null);
    setHasMore(false);
    setError("");
    setShaInput("");
    setShaError("");
    requestSequence.current += 1;
  }, [repositoryId]);

  useEffect(() => {
    if (!repositoryId) return;
    const sequence = ++requestSequence.current;
    setLoading(true);
    setError("");
    void api.listCommits(repositoryId, debouncedQuery, 0, pageSize + 1)
      .then((items) => {
        if (requestSequence.current !== sequence) return;
        const visible = items.slice(0, pageSize);
        setCommits(visible);
        setHasMore(items.length > pageSize);
        const current = visible.find((commit) => commit.sha === value);
        if (current) setSelected(current);
        if (autoSelectFirst && !value && debouncedQuery === "" && visible[0]) {
          setSelected(visible[0]);
          onSelect(visible[0]);
        }
      })
      .catch((reason: unknown) => {
        if (requestSequence.current === sequence) setError(messageOf(reason, "커밋 목록을 불러오지 못했습니다."));
      })
      .finally(() => {
        if (requestSequence.current === sequence) setLoading(false);
      });
  }, [repositoryId, debouncedQuery]);

  useEffect(() => {
    if (!value) {
      setSelected(null);
      return;
    }
    const known = commits.find((commit) => commit.sha === value);
    if (known) setSelected(known);
  }, [value, commits]);

  function choose(commit: GitCommit) {
    setSelected(commit);
    onSelect(commit);
  }

  async function loadMore() {
    if (!repositoryId || loading || !hasMore) return;
    const sequence = ++requestSequence.current;
    setLoading(true);
    setError("");
    try {
      const items = await api.listCommits(repositoryId, debouncedQuery, commits.length, pageSize + 1);
      if (requestSequence.current !== sequence) return;
      const visible = items.slice(0, pageSize);
      setCommits((current) => [...current, ...visible.filter((item) => !current.some((known) => known.sha === item.sha))]);
      setHasMore(items.length > pageSize);
    } catch (reason) {
      if (requestSequence.current === sequence) setError(messageOf(reason, "이전 커밋을 불러오지 못했습니다."));
    } finally {
      if (requestSequence.current === sequence) setLoading(false);
    }
  }

  async function resolveSha() {
    const revision = shaInput.trim();
    setShaError("");
    if (!/^[0-9a-fA-F]{7,64}$/.test(revision)) {
      setShaError("7~64자리의 16진수 커밋 SHA를 입력하세요.");
      return;
    }
    setResolving(true);
    try {
      const commit = await api.resolveCommit(repositoryId, revision);
      setSelected(commit);
      onSelect(commit);
      setShaInput("");
    } catch (reason) {
      setShaError(messageOf(reason, "해당 커밋을 찾지 못했습니다."));
    } finally {
      setResolving(false);
    }
  }

  const selectedCommit = selected?.sha === value ? selected : commits.find((commit) => commit.sha === value) ?? null;
  return <fieldset className="commit-picker" disabled={!repositoryId}>
    <legend>{label}</legend>
    <p className="help">기본 브랜치 <strong>{defaultBranch || "-"}</strong>의 전체 이력에서 찾습니다.</p>
    {selectedCommit && <div className="selected-commit" aria-live="polite">
      <strong>{oneLine(selectedCommit.message)}</strong>
      <span><code>{selectedCommit.sha.slice(0, 12)}</code> · {selectedCommit.authorName || "작성자 미상"} · {formatDate(selectedCommit.authoredAt)}</span>
    </div>}
    <label>메시지, SHA 또는 작성자 검색
      <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="검색어 2자 이상" />
    </label>
    {query.trim().length === 1 && <p className="help">검색어를 두 글자 이상 입력하면 전체 이력을 검색합니다.</p>}
    <div className="commit-results" role="listbox" aria-label={`${label} 검색 결과`}>
      {commits.map((commit) => <button
        type="button"
        role="option"
        aria-selected={commit.sha === value}
        className={commit.sha === value ? "active" : ""}
        key={commit.sha}
        onClick={() => choose(commit)}
      >
        <strong>{oneLine(commit.message)}</strong>
        <span><code>{commit.sha.slice(0, 10)}</code> · {commit.authorName || "작성자 미상"} · {formatDate(commit.authoredAt)}</span>
      </button>)}
      {!loading && commits.length === 0 && <p className="commit-empty">표시할 커밋이 없습니다.</p>}
    </div>
    {error && <p className="error-text">{error}</p>}
    {hasMore && <button type="button" className="secondary commit-more" disabled={loading} onClick={() => void loadMore()}>{loading ? "불러오는 중..." : "이전 커밋 50개 더 보기"}</button>}
    {loading && commits.length === 0 && <p className="help">커밋을 불러오는 중입니다...</p>}
    <details className="sha-picker">
      <summary>목록에 없는 커밋 SHA 직접 입력</summary>
      <div className="sha-input-row">
        <input
          value={shaInput}
          onChange={(event) => setShaInput(event.target.value)}
          onKeyDown={(event) => { if (event.key === "Enter") { event.preventDefault(); void resolveSha(); } }}
          placeholder="7자리 이상의 커밋 SHA"
        />
        <button type="button" className="secondary" disabled={resolving || !shaInput.trim()} onClick={() => void resolveSha()}>{resolving ? "확인 중" : "SHA 확인"}</button>
      </div>
      {shaError && <p className="error-text">{shaError}</p>}
    </details>
  </fieldset>;
}

function oneLine(value: string) {
  return value.replace(/\s+/g, " ").trim() || "메시지 없음";
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString("ko-KR", { dateStyle: "medium", timeStyle: "short" });
}

function messageOf(reason: unknown, fallback: string) {
  return reason instanceof Error ? reason.message : fallback;
}
