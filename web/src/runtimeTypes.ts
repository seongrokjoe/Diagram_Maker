export type RuntimeInfo = {
  mode: "normal";
  llmProvider: "internal-vllm";
  llmConfigured: boolean;
  capabilities: { thinkingControl: boolean; exactTokenLimit: boolean };
};
