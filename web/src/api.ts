import type {
  AnalysisGroupSelection,
  AnalysisHistorySummary,
  AnalysisPlan,
  AnalysisResponse,
  DiagramPreset,
  DiagramEditDocument,
  DiagramEditPreview,
  DiagramRevisionRecord,
  DiagramStyle,
  DiagramType,
  DiagramViewSelection,
  EvidenceSnippet,
  GitCommit,
  IndirectCallRule,
  LlmConnectionTestResult,
  LlmContractTestResult,
  LlmThinkingContractTestResult,
  NaturalDiagramRecord,
  Repository,
  RepositoryInspection,
} from "./types";

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: { "Content-Type": "application/json", ...init?.headers },
  });
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as {
      error?: string;
      detail?: string;
      errorCode?: string;
      failureKind?: string;
      currentRevision?: number;
      requestedMaxOutputTokens?: number;
      completionTokens?: number;
    } | null;
    const message = body?.error ?? body?.detail ?? `요청 실패 (${response.status})`;
    const diagnostics = [
      body?.errorCode,
      body?.failureKind,
      body?.currentRevision !== undefined ? `revision=${body.currentRevision}` : undefined,
      body?.requestedMaxOutputTokens !== undefined ? `max=${body.requestedMaxOutputTokens}` : undefined,
      body?.completionTokens !== undefined ? `completion=${body.completionTokens}` : undefined,
    ].filter((value): value is string => Boolean(value));
    throw new Error(diagnostics.length ? `${message} [${diagnostics.join(" / ")}]` : message);
  }
  return (await response.json()) as T;
}

export const api = {
  listRepositories: () => request<Repository[]>("/api/v1/repositories"),
  inspectRepository: (localPath: string) =>
    request<RepositoryInspection>("/api/v1/repositories/inspect", {
      method: "POST",
      body: JSON.stringify({ localPath }),
    }),
  registerRepository: (input: { name: string; localPath: string; defaultBranch: string }) =>
    request<Repository>("/api/v1/repositories", { method: "POST", body: JSON.stringify(input) }),
  updateRepositoryAnalysisRules: (repositoryId: string, expectedRevision: number, indirectCalls: IndirectCallRule[]) =>
    request<Repository>(`/api/v1/repositories/${repositoryId}/analysis-rules`, {
      method: "PUT",
      body: JSON.stringify({ expectedRevision, indirectCalls }),
    }),
  listCommits: (repositoryId: string, query = "", skip = 0, limit = 50) =>
    request<GitCommit[]>(`/api/v1/repositories/${repositoryId}/commits?query=${encodeURIComponent(query)}&skip=${skip}&limit=${limit}`),
  resolveCommit: (repositoryId: string, revision: string) =>
    request<GitCommit>(`/api/v1/repositories/${repositoryId}/commits/resolve?revision=${encodeURIComponent(revision)}`),
  listPresets: (type?: DiagramType) =>
    request<DiagramPreset[]>(`/api/v1/diagram-presets${type ? `?type=${type}` : ""}`),

  listNaturalDiagrams: (limit = 20) => request<NaturalDiagramRecord[]>(`/api/v1/natural-diagrams?limit=${limit}`),
  listNaturalDiagramRevisions: (id: string) => request<NaturalDiagramRecord[]>(`/api/v1/natural-diagrams/${id}/revisions`),
  createNaturalDiagram: (input: {
    prompt: string;
    diagramType: DiagramType;
    enableThinking: boolean;
    presetId: string;
    style?: DiagramStyle;
    views?: DiagramViewSelection[];
    forceRegenerate?: boolean;
  }) => request<NaturalDiagramRecord>("/api/v1/natural-diagrams", { method: "POST", body: JSON.stringify(input) }),
  regenerateNaturalDiagram: (id: string) =>
    request<NaturalDiagramRecord>(`/api/v1/natural-diagrams/${id}/regenerate`, { method: "POST" }),
  reviseNaturalDiagramViews: (id: string, views: DiagramViewSelection[], regenerateViewIds: string[]) =>
    request<NaturalDiagramRecord>(`/api/v1/natural-diagrams/${id}/views/revise`, {
      method: "POST",
      body: JSON.stringify({ views, regenerateViewIds }),
    }),
  saveNaturalDiagramDslRevision: (id: string, mermaidDsl: string) =>
    request<NaturalDiagramRecord>(`/api/v1/natural-diagrams/${id}/dsl-revisions`, { method: "POST", body: JSON.stringify({ mermaidDsl }) }),

  createAnalysisPlan: (input: {
    repositoryId: string;
    targetRevision: string;
    baseRevision?: string;
    useLlmGrouping: boolean;
    enableThinking: boolean;
  }) => request<AnalysisPlan>("/api/v1/analysis-plans", { method: "POST", body: JSON.stringify(input) }),
  listAnalysisPlans: (limit = 20) => request<AnalysisPlan[]>(`/api/v1/analysis-plans?limit=${limit}`),
  getAnalysisPlan: (id: string) => request<AnalysisPlan>(`/api/v1/analysis-plans/${id}`),
  listAnalysisPlanAnalyses: (id: string, limit = 20) =>
    request<AnalysisHistorySummary[]>(`/api/v1/analysis-plans/${id}/analyses?limit=${limit}`),
  getAnalysisPlanEvidence: (id: string, changeId: string) =>
    request<EvidenceSnippet>(`/api/v1/analysis-plans/${id}/evidence/${encodeURIComponent(changeId)}`),
  saveAnalysisPlan: (id: string, expectedRevision: number, groups: AnalysisGroupSelection[]) =>
    request<AnalysisPlan>(`/api/v1/analysis-plans/${id}/selection`, {
      method: "PUT",
      body: JSON.stringify({ expectedRevision, groups }),
    }),
  generateAnalysisPlan: (id: string, expectedRevision: number, sourceAnalysisId?: string, requestedViewIds?: string[]) =>
    request<AnalysisResponse>(`/api/v1/analysis-plans/${id}/generate`, {
      method: "POST",
      body: JSON.stringify({ expectedRevision, sourceAnalysisId, requestedViewIds }),
    }),
  getAnalysis: (id: string) => request<AnalysisResponse>(`/api/v1/analyses/${id}`),

  listDiagramRevisions: (rootArtifactId: string) =>
    request<DiagramRevisionRecord[]>(`/api/v1/diagram-artifacts/${rootArtifactId}/revisions`),
  saveNaturalDiagramEdit: (recordId: string, viewId: string, input: {
    rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument;
  }) => request<DiagramRevisionRecord>(`/api/v1/natural-diagrams/${recordId}/views/${encodeURIComponent(viewId)}/edits`, {
    method: "POST", body: JSON.stringify(input),
  }),
  previewNaturalDiagramEdit: (recordId: string, viewId: string, input: {
    rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument;
  }, signal?: AbortSignal) => request<DiagramEditPreview>(`/api/v1/natural-diagrams/${recordId}/views/${encodeURIComponent(viewId)}/edit-preview`, {
    method: "POST", body: JSON.stringify(input), signal,
  }),
  saveAnalysisDiagramEdit: (analysisId: string, groupId: string, viewId: string, input: {
    rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument;
  }, revisionSide: "base" | "target" = "target") => request<DiagramRevisionRecord>(`/api/v1/analyses/${analysisId}/groups/${encodeURIComponent(groupId)}/views/${encodeURIComponent(viewId)}/edits?revisionSide=${revisionSide}`, {
    method: "POST", body: JSON.stringify(input),
  }),
  previewAnalysisDiagramEdit: (analysisId: string, groupId: string, viewId: string, input: {
    rootArtifactId: string; parentRevisionId?: string; expectedVersion: number; document: DiagramEditDocument;
  }, signal?: AbortSignal, revisionSide: "base" | "target" = "target") => request<DiagramEditPreview>(`/api/v1/analyses/${analysisId}/groups/${encodeURIComponent(groupId)}/views/${encodeURIComponent(viewId)}/edit-preview?revisionSide=${revisionSide}`, {
    method: "POST", body: JSON.stringify(input), signal,
  }),

  testLlmConnection: () => request<LlmConnectionTestResult>("/api/v1/llm/tests/connection", { method: "POST" }),
  testLlmDiagramContract: () => request<LlmContractTestResult>("/api/v1/llm/tests/diagram-contract", { method: "POST" }),
  testLlmThinkingContract: () => request<LlmThinkingContractTestResult>("/api/v1/llm/tests/thinking-contract", { method: "POST" }),
};
