import type {
  AnalysisResponse,
  LlmConnectionTestResult,
  LlmContractTestResult,
  NaturalDiagramRecord,
  Repository,
  RepositoryInspection,
} from "./types";

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...init?.headers,
    },
  });
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as {
      error?: string;
      detail?: string;
      errorCode?: string;
      failureKind?: string;
      initialFailureKind?: string;
      repairAttempted?: boolean;
    } | null;
    const message = body?.error ?? body?.detail ?? `요청 실패 (${response.status})`;
    const diagnostics = [body?.errorCode, body?.failureKind]
      .filter((value): value is string => Boolean(value));
    throw new Error(diagnostics.length > 0 ? `${message} [${diagnostics.join(" / ")}]` : message);
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
  createNaturalDiagram: (input: { prompt: string; diagramType: string; enableThinking: boolean }) =>
    request<NaturalDiagramRecord>("/api/v1/natural-diagrams", { method: "POST", body: JSON.stringify(input) }),
  createAnalysis: (input: {
    repositoryId: string;
    baseRevision: string;
    targetRevision: string;
    includeLlmSummary: boolean;
    enableThinking: boolean;
  }) => request<AnalysisResponse>("/api/v1/analyses", { method: "POST", body: JSON.stringify(input) }),
  getAnalysis: (id: string) => request<AnalysisResponse>(`/api/v1/analyses/${id}`),
  testLlmConnection: () => request<LlmConnectionTestResult>("/api/v1/llm/tests/connection", { method: "POST" }),
  testLlmDiagramContract: () => request<LlmContractTestResult>("/api/v1/llm/tests/diagram-contract", { method: "POST" }),
  testLlmThinkingContract: () => request<LlmContractTestResult>("/api/v1/llm/tests/thinking-contract", { method: "POST" }),
};
