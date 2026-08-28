export type Repository = {
  id: string;
  name: string;
  localPath: string;
  defaultBranch: string;
  allowedRoles: string[];
  createdAt: string;
};

export type RepositoryInspection = {
  normalizedPath: string;
  isBare: boolean;
  defaultBranch: string;
  headSha: string;
  headMessage: string;
  branches: string[];
};

export type DiagramArtifact = {
  id: string;
  type: string;
  version: number;
  mermaidDsl: string;
  ir: {
    type: string;
    title: string;
    notes: string[];
  };
  createdAt: string;
};

export type NaturalDiagramRecord = {
  id: string;
  diagram: DiagramArtifact;
  createdAt: string;
};

export type LlmConnectionTestResult = {
  success: boolean;
  elapsedMilliseconds: number;
  finishReason: string;
  responseCharacters: number;
  requestedMaxOutputTokens: number;
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
};

export type LlmContractTestResult = {
  success: boolean;
  nodeCount: number;
  edgeCount: number;
  elapsedMilliseconds: number;
  finishReason: string;
  structuredOutputApplied: boolean;
  structuredOutputFallbackUsed: boolean;
  repairUsed: boolean;
  thinkingEnabled: boolean;
  requestedMaxOutputTokens: number;
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
};

export type ChangedFile = {
  path: string;
  previousPath?: string;
  changeKind: string;
  hunks: Array<{ header: string }>;
};

export type AnalysisResponse = {
  id: string;
  state: string;
  baseSha?: string;
  targetSha?: string;
  progress: number;
  stageMessage: string;
  errorCode?: string;
  errorMessage?: string;
  result?: {
    changedFiles: ChangedFile[];
    narrative: {
      summary: string;
      intent: string;
      risks: Array<{ severity: string; text: string; evidenceIds: string[] }>;
      warnings: string[];
    };
    diagrams: DiagramArtifact[];
    graph: {
      identities: unknown[];
      versions: unknown[];
      edges: unknown[];
      evidence: unknown[];
      changes: unknown[];
    };
  };
};
