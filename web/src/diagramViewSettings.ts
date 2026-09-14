export type ViewDirection = "original" | "LR" | "TB";

// Only compiler-emitted direction statements change; labels and saved IR stay intact.
export function withViewDirection(source: string, direction: ViewDirection, type: string) {
  if (direction === "original" || type === "sequence") return source;
  return source.replace(/^(flowchart) (LR|TB)\s*$/m, `$1 ${direction}`)
    .replace(/^(\s*direction) (LR|TB)\s*$/gm, `$1 ${direction}`);
}

export function fitZoom(width: number, height: number, availableWidth: number, availableHeight: number) {
  return Math.max(0.01, Math.min(1, Math.max(1, availableWidth) / width, Math.max(1, availableHeight) / height));
}
