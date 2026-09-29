import type { NaturalDiagramPageResult, NaturalDiagramViewResult } from "./types";

export function naturalResultState(view?: NaturalDiagramViewResult, page?: NaturalDiagramPageResult) {
  // A page is an independent result, including when it has no diagram or error.
  const result = page ?? view;
  const diagram = result?.diagram ?? result?.lastSuccessfulDiagram;
  return {
    diagram, error: result?.errorMessage,
    quality: result?.designQuality,
    label: result?.state === "Failed" || result?.state === "Partial"
      ? !diagram ? "생성된 결과 없음" : result.lastSuccessfulDiagram
        ? "생성 미완료 · 이전 정상 결과 표시" : "생성 부분 완료 · 검토 필요"
      : result?.reused ? "이전 결과 재사용" : "LLM 생성",
  };
}

export function naturalResultCounts(views: NaturalDiagramViewResult[]) {
  const results = views.flatMap<NaturalDiagramPageResult | NaturalDiagramViewResult>(view => view.pages?.length ? view.pages : [view]);
  return { completed: results.filter(result => result.state === "Completed").length,
    partial: results.filter(result => result.state === "Partial").length,
    failed: results.filter(result => result.state === "Failed").length };
}
