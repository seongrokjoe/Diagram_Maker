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
  refinementInstruction?: string;
};

export type DiagramNode = {
  originalExpression?: string;
  qualifiedName?: string;
  context?: CodeContext;
  sourceFactIds?: string[];
  detailPageId?: string;
  abstractionKind?: string;
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
  call?: { target: string; arguments: string[]; assignedTo?: string; returnType?: string; basis: string; contractUrl?: string;
    outputs: Array<{ expression: string; mode: string; description: string; basis: string; evidenceIds: string[] }> };
  relationOrigin?: string; originalExpression?: string; returnValue?: string; terminationTarget?: string;
  context?: CodeContext;
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
  sourceFactIds?: string[];
};

export type DiagramArtifact = {
  explanation?: DiagramExplanation | null;
  id: string;
  type: string;
  version: number;
  mermaidDsl: string;
  ir: { type: string; title: string; notes: string[]; provenance?: string[]; direction?: string; nodes: DiagramNode[]; edges: DiagramEdge[]; sequenceBlocks?: SequenceBlock[] };
  createdAt: string;
};

export type CodeContext = {
  statement: string; target?: string; receiver?: string; arguments: string[]; assignedTo?: string; createdType?: string;
  initializers: string[]; controlPath: Array<{ id: string; kind: string; label: string; branch: string }>;
  span: { revisionSha: string; blobOid: string; filePath: string; startLine: number; endLine: number; startOffset?: number; endOffset?: number };
  purpose: string;
  definitions?: Array<{ name: string; statement: string; span: CodeContext["span"] }>;
};

export type DiagramExplanation = {
  coverage?: { totalUnits: number; verifiedUnits: number; pendingUnits: number; failedUnits: number } | null;
  failures?: Array<{ itemIds: string[]; stage: string; code: string; factIds: string[]; correctionInstructions: string[];
    fields?: string[]; issueCodes?: string[]; category?: string }> | null;
  behaviors?: Array<{ id: string; summary: string; factIds: string[]; nodeIds: string[]; edgeIds: string[] }> | null;
  summary: string;
  changes: Array<{ changeId: string; summary: string; factIds: string[]; nodeIds: string[]; edgeIds: string[] }>;
  factIds: string[]; evidenceIds: string[];
  status: string; warnings: string[]; basis: string;
};

export type SequenceBlock = { id: string; kind: string; label: string; children: SequenceBlock[]; edgeId?: string;
  participantIds?: string[]; detailPageId?: string; evidenceIds?: string[]; sourceFactIds?: string[];
  originalExpression?: string; terminationTarget?: string };

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
  requirements?: { title: string; entities: string[]; requirements: Array<{ id: string; text: string; kind: string; origin: string; sourceQuote: string;
      sourceRangeId?: string; scenarioId?: string }>;
    sourceRanges?: Array<{ id: string; startOffset: number; endOffset: number; text: string }>;
    scenarios?: Array<{ id: string; title: string; requirementIds: string[]; sourceRangeIds: string[] }> };
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
  designQuality?: { protocol: string; status: string; reviewedRequirementIds: string[]; assumptionElementIds: string[]; repairUsed: boolean };
  pages?: NaturalDiagramPageResult[];
};

export type NaturalDiagramPageResult = {
  id: string;
  scenarioId: string;
  title: string;
  diagram?: DiagramArtifact;
  state: string;
  errorCode?: string;
  errorMessage?: string;
  lastSuccessfulDiagram?: DiagramArtifact;
  reused: boolean;
  designQuality?: NaturalDiagramViewResult["designQuality"];
};

export type NaturalDiagramRun = {
  id: string;
  ownerUserId: string;
  request: NaturalDiagramRecord["request"];
  state: "Queued" | "Generating" | "Completed" | "Partial" | "Failed" | "Cancelled";
  createdAt: string;
  updatedAt: string;
  revision: number;
  progress: number;
  stageMessage: string;
  sourceDiagramId?: string;
  regenerateViewIds?: string[];
  regeneratePageIds?: string[];
  requirements?: NaturalDiagramRecord["requirements"];
  views?: NaturalDiagramViewResult[];
  resultDiagramId?: string;
  errorCode?: string;
  errorMessage?: string;
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
  document?: { overviewPageId: string; pages: Array<{ id: string; title: string; diagram: DiagramArtifact; codeDiagram?: DiagramArtifact; aiState?: string; resultKind?: string }>;
    coverage: Array<{ changeId: string; state: string; pageIds: string[]; reason?: string }> };
  viewId: string;
  selection: DiagramViewSelection;
  diagram?: DiagramArtifact;
  warnings: string[];
  state: string;
  errorCode?: string;
  errorMessage?: string;
  reused: boolean;
  generationMetadata?: {
    changeIds: string[];
    sources: Array<{ filePath: string; startLine: number; endLine: number }>;
    evidenceIds: string[];
    llmStatus: "Applied" | "Fallback" | "Disabled" | "NotRun" | string;
    refinementInstruction?: string;
    bundleHash?: string;
    analyzerVersion?: string;
    promptVersion?: string;
    effectiveOptions?: DiagramStyle;
    instructionResults?: string[];
    attempts?: number;
    warnings: string[];
  };
  failureStage?: string;
};

export type DiagramEditDocument = {
  title: string;
  direction?: "LR" | "TB";
  nodes: Array<{ id: string; label: string; details?: string[] }>;
  edges: Array<{ id: string; sourceId: string; targetId: string; label: string; type?: string }>;
  sequenceAnnotations?: Array<{ id: string; kind: "alt" | "loop" | "break" | "opt" | "note" | "scenario"; label: string }>;
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
  resultCounts?: import("./diagramOrigin").DiagramResultCounts;
  id: string;
  state: string;
  revision?: number;
  canResume?: boolean;
  stopReason?: string;
  execution?: import("./SemanticProgressView").SemanticProgress;
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
    graph?: { identities: unknown[]; versions: unknown[]; edges: unknown[]; evidence: unknown[]; changes: unknown[] };
  };
};
