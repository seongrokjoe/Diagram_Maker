// Mermaid's SVG Class/State renderers drop Markdown auto-link tokens. Interrupt only
// display URLs with a zero-width marker while it lays out the text, then remove
// that marker from SVG text nodes. The original DSL and security checks stay intact.
export function prepareMermaidDisplay(source: string): { source: string; marker: string } {
  if (!/^(?:classDiagram|stateDiagram-v2)\b/.test(source.trimStart())) return { source, marker: "" };
  let marker = "\u2060";
  while (source.includes(marker)) marker += marker;
  const protect = (label: string) => label.replace(/\b(?:https?:\/\/|www\.)/gi, prefix => prefix[0] + marker + prefix.slice(1));
  const prepared = source.replace(/"[^"\r\n]*"/g, protect).replace(
    /^(\s*(?:\w+|\[\*\])(?:\s+(?:<\|--|<\|\.\.|-->|\.\.>|--|--\*)\s+(?:\w+|\[\*\]))?\s*:)([^;\r\n]*)$/gm,
    (_match, syntax: string, label: string) => syntax + protect(label));
  return { source: prepared, marker: prepared === source ? "" : marker };
}
