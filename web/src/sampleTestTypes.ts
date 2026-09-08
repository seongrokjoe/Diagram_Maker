import type { NaturalDiagramRecord } from "./types";

export type RuntimeInfo = {
  mode: "normal" | "codex-sample";
  llmProvider: string;
  sampleOnly: boolean;
  model?: string;
  capabilities: { thinkingControl: boolean; exactTokenLimit: boolean };
  codex?: { busy: boolean; calls: number; errorCode?: string; error?: string };
};
export type SampleMetadata = { provider: string; scenarioId: string; sampleVersion: string; refinementId: string };
export type SampleCatalog = {
  version: string;
  scenarios: Array<{ id: string; title: string; kind: "git" | "natural"; fileName?: string; before?: string; after?: string; prompt?: string }>;
  refinements: Array<{ id: string; label: string; instruction: string }>;
};
export type SampleGenerateInput = {
  planId?: string; refinementId: string; diagramTypes?: string[]; presetId?: string;
  direction?: string; detailLevel?: string; callerDepth?: number; calleeDepth?: number;
  relationDepth?: number; focusOnChanges?: boolean;
};
export type SampleGenerateResult = { kind: "git"; id: string } | { kind: "natural"; record: NaturalDiagramRecord };
