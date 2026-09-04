export type Repository = {
  id: string;
  name: string;
  localPath: string;
  defaultBranch: string;
  allowedRoles: string[];
  createdAt: string;
  analysisRules: RepositoryAnalysisRules;
};

export type IndirectCallAlias = { expression: string; targetType: string };
export type IndirectCallRule = {
  id: string;
  name: string;
  enabled: boolean;
  apiName: string;
  targetTypeArgumentIndex: number;
  targetMethodArgumentIndex?: number;
  aliases: IndirectCallAlias[];
};
export type RepositoryAnalysisRules = { revision: number; indirectCalls: IndirectCallRule[] };

export type RepositoryInspection = {
  normalizedPath: string;
  isBare: boolean;
  defaultBranch: string;
  headSha: string;
  headMessage: string;
  branches: string[];
};

export type GitCommit = {
  sha: string;
  parentShas: string[];
  authoredAt: string;
  message: string;
  authorName: string;
  authorEmail: string;
};

export type DiagramStyle = {
  direction?: "LR" | "TB";
  detailLevel?: "compact" | "balanced" | "detailed";
  callerDepth?: number;
  calleeDepth?: number;
  relationDepth?: number;
};

export type DiagramPreset = {
  id: string;
  type: DiagramType;
  name: string;
  description: string;
  thumbnailDsl: string;
  direction: string;
  detailLevel: string;
  callerDepth: number;
  calleeDepth: number;
  relationDepth: number;
  maximumNodes: number;
  maximumEdges: number;
};

export type DiagramType = "flowchart" | "sequence" | "class" | "code-relation" | "state";

export type DiagramViewSelection = {
  id: string;
  diagramType: DiagramType;
  presetId: string;
  overrides?: DiagramStyle;
  focusOnChanges?: boolean;
  compareRevisions?: boolean;
};

export type DiagramNode = {
  id: string;
  label: string;
  kind: string;
  group?: string;
  status: string;
  confidence: string;
  evidenceIds: string[];
  shape?: string;
  details?: string[];
  changeMarker?: DiagramChangeMarker;
};

export type DiagramChangeMarker = {
  kind: "Added" | "Modified" | "Deleted";
  precision: "Exact" | "Symbol";
  filePath?: string;
  startLine?: number;
  endLine?: number;
  evidenceIds: string[];
};

export type DiagramEdge = {
  id: string;
  sourceId: string;
  targetId: string;
  type: string;
  label: string;
  status: string;
  confidence: string;
  evidenceIds: string[];
  sequenceIndex?: number;
  isIndirect?: boolean;
  viaApi?: string;
  controlPath?: Array<{ id: string; kind: string; label: string; branch: string }>;
  changeMarker?: DiagramChangeMarker;
};

export type DiagramArtifact = {
  id: string;
  type: string;
  version: number;
  mermaidDsl: string;
  ir: { type: string; title: string; notes: string[]; direction?: string; nodes: DiagramNode[]; edges: DiagramEdge[] };
  createdAt: string;
};

export type DiagramAvailability = { type: string; available: boolean; reason?: string };

export type Narrative = {
  summary: string;
  intent: string;
  risks: Array<{ severity: string; text: string; evidenceIds: string[] }>;
  warnings: string[];
};

export type NaturalDiagramRecord = {
  id: string;
  request: {
    prompt: string;
    diagramType: DiagramType;
    parentDiagramId?: string;
    enableThinking: boolean;
    forceRegenerate: boolean;
    presetId: string;
    style?: DiagramStyle;
    views?: DiagramViewSelection[];
  };
  diagram: DiagramArtifact;
  createdAt: string;
  ownerUserId: string;
  rootDiagramId?: string;
  parentDiagramId?: string;
  source: "generated" | "manualDsl" | string;
  generatorVersion: string;
  reused: boolean;
  views?: NaturalDiagramViewResult[];
  revision: number;
};

export type NaturalDiagramViewResult = {
  viewId: string;
  selection: DiagramViewSelection;
  diagram?: DiagramArtifact;
  state: string;
  errorCode?: string;
  errorMessage?: string;
  lastSuccessfulDiagram?: DiagramArtifact;
  reused: boolean;
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

export type LlmThinkingContractTestResult = Omit<LlmContractTestResult, "nodeCount" | "edgeCount">;

export type ChangedFile = {
  path: string;
  previousPath?: string;
  changeKind: string;
  hunks: Array<{
    oldStart?: number; oldLines?: number; newStart?: number; newLines?: number; header: string;
    changedRanges?: Array<{ oldStartLine?: number; oldLineCount: number; newStartLine?: number; newLineCount: number }>;
  }>;
};

export type ChangeCandidate = {
  id: string;
  identityId: string;
  qualifiedName: string;
  kind: string;
  changeType: string;
  filePath: string;
  startLine: number;
  endLine: number;
  signature: string;
  confidence: string;
  callerCount: number;
  calleeCount: number;
  evidenceIds: string[];
};

export type EvidenceSnippet = {
  revisionSha: string;
  blobOid: string;
  filePath: string;
  startLine: number;
  endLine: number;
  content: string;
};

export type AnalysisGroupSelection = {
  id: string;
  title: string;
  changeIds: string[];
  diagramType: DiagramType;
  presetId: string;
  overrides?: DiagramStyle;
  views?: DiagramViewSelection[];
};

export type AnalysisPlan = {
  id: string;
  request: {
    repositoryId: string;
    targetRevision: string;
    baseRevision?: string;
    useLlmGrouping: boolean;
    enableThinking: boolean;
  };
  state: "Queued" | "Indexing" | "Grouping" | "Ready" | "Failed" | "Expired";
  baseSha?: string;
  targetSha?: string;
  progress: number;
  stageMessage: string;
  changedFiles?: ChangedFile[];
  candidates: ChangeCandidate[];
  suggestedGroups: Array<{
    id: string;
    title: string;
    description: string;
    changeIds: string[];
    source: string;
    confidence: string;
    suggestedDiagramType: DiagramType;
  }>;
  selections: AnalysisGroupSelection[];
  warnings: string[];
  errorCode?: string;
  errorMessage?: string;
  revision: number;
  createdAt: string;
  updatedAt: string;
  expiresAt: string;
  indexVersion?: string;
  targetCommitMessage?: string;
  notices?: Array<{ code: string; category: string; severity: string; message: string }>;
  exclusions?: {
    totalCount: number;
    fileCount: number;
    truncated: boolean;
    calls: Array<{
      filePath: string;
      line: number;
      sourceSemanticKey: string;
      expression: string;
      reason: string;
      candidateTargets: string[];
    }>;
  };
};

export type AnalysisDiagramGroup = {
  groupId: string;
  title: string;
  changeIds: string[];
  diagram?: DiagramArtifact;
  narrative: Narrative;
  warnings: string[];
  views?: AnalysisDiagramView[];
};

export type AnalysisDiagramView = {
  viewId: string;
  selection: DiagramViewSelection;
  diagram?: DiagramArtifact;
  warnings: string[];
  state: string;
  errorCode?: string;
  errorMessage?: string;
  reused: boolean;
  comparisonBaseDiagram?: DiagramArtifact;
};

export type DiagramEditDocument = {
  title: string;
  direction?: "LR" | "TB";
  nodes: Array<{ id: string; label: string }>;
  edges: Array<{ id: string; sourceId: string; targetId: string; label: string; type?: string }>;
};

export type DiagramRevisionRecord = {
  id: string;
  rootArtifactId: string;
  sourceArtifactId: string;
  parentRevisionId?: string;
  sourceKind: string;
  sourceId: string;
  groupId?: string;
  viewId: string;
  version: number;
  diagram: DiagramArtifact;
  createdAt: string;
};

export type DiagramEditPreview = {
  version: number;
  ir: DiagramArtifact["ir"];
  mermaidDsl: string;
};

export type AnalysisHistorySummary = {
  id: string;
  state: string;
  createdAt: string;
  updatedAt: string;
  baseSha?: string;
  targetSha?: string;
  hasResult: boolean;
  totalGroups: number;
  successfulGroups: number;
  totalViews: number;
  successfulViews: number;
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
    narrative: Narrative;
    diagrams: DiagramArtifact[];
    diagramAvailability?: DiagramAvailability[];
    diagramGroups?: AnalysisDiagramGroup[];
    graph: { identities: unknown[]; versions: unknown[]; edges: unknown[]; evidence: unknown[]; changes: unknown[] };
  };
};
