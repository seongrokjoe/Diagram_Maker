import type { CodeBlockDraft } from "./codeBlockTypes";

export type CodeBlockLimits = { maximumBlocks: number; maximumBlockCharacters: number; maximumTotalCharacters: number; maximumRequestBytes: number };
export const defaultCodeBlockLimits: CodeBlockLimits = { maximumBlocks: 20, maximumBlockCharacters: 100_000, maximumTotalCharacters: 1_000_000, maximumRequestBytes: 8 * 1024 * 1024 };

export function codeInputError(draft: CodeBlockDraft, limits: CodeBlockLimits): string | null {
  if (draft.blocks.length > limits.maximumBlocks) return `최대 ${limits.maximumBlocks}블럭까지 저장할 수 있습니다.`;
  const excess = draft.blocks.find(b => b.code.length > limits.maximumBlockCharacters);
  if (excess) return `${excess.title || "코드 블럭"}: ${(excess.code.length - limits.maximumBlockCharacters).toLocaleString()}자 초과. 입력은 보존했습니다. 블럭을 나누어 주세요.`;
  const count = draft.blocks.reduce((total, block) => total + block.code.length, 0);
  return count > limits.maximumTotalCharacters ? `전체 코드가 ${(count - limits.maximumTotalCharacters).toLocaleString()}자 초과했습니다. 작업을 나누어 주세요.` : null;
}
