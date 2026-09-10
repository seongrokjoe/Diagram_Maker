import { useEffect, useState } from "react";

export type SemanticProgress = {
  stage: string; completedUnits: number; reusedUnits: number; requests: number;
  elapsedSeconds: number; budgetSeconds: number; totalUnits?: number;
  attemptCompletedUnits?: number; attemptRequests?: number; totalElapsedSeconds?: number;
  attemptNumber?: number; startedAt?: string; transportRequests?: number; tokenizationRequests?: number;
  failedUnits?: number; rejectedBeforeSend?: number; attemptTransportRequests?: number;
  attemptTokenizationRequests?: number; waitMilliseconds?: number; attemptWaitMilliseconds?: number;
  waitingSince?: string;
};

export function SemanticProgressView({ value, running }: { value: SemanticProgress; running: boolean }) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [running]);
  const elapsed = running && value.startedAt
    ? Math.max(value.elapsedSeconds, Math.floor((now - Date.parse(value.startedAt)) / 1000)) : value.elapsedSeconds;
  const total = (value.totalElapsedSeconds ?? value.elapsedSeconds) + elapsed - value.elapsedSeconds;
  const waiting = running && value.waitingSince ? Math.max(0, now - Date.parse(value.waitingSince)) : 0;
  return <div className="help semantic-progress" aria-label="전체 및 이번 실행 진척">
    <p>전체: 완료 {value.completedUnits}단위 · 요청 {value.requests}회 · 실제 전송 {value.transportRequests ?? value.requests}회 · 누적 {total}초</p>
    <p>이번 실행 {value.attemptNumber ?? 1}: 새 완료 {value.attemptCompletedUnits ?? value.completedUnits}단위 · 재사용 {value.reusedUnits}단위 · 요청 {value.attemptRequests ?? value.requests}회 · 실제 전송 {value.attemptTransportRequests ?? value.requests}회 · {elapsed} / {value.budgetSeconds}초</p>
    <p>LLM 대기: 전체 {Math.floor(((value.waitMilliseconds ?? 0) + waiting) / 1000)}초 · 이번 실행 {Math.floor(((value.attemptWaitMilliseconds ?? 0) + waiting) / 1000)}초
      {(value.failedUnits ?? 0) > 0 && ` · 실패 ${value.failedUnits}단위`}</p>
    {running && elapsed >= 300 && <p>5분이 지났습니다. 완료된 작업을 저장하면서 실행 한도까지 계속 생성합니다.</p>}
  </div>;
}
