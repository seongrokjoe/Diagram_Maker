// Display-only line breaks: the saved Mermaid source and source evidence are unchanged.
export function prepareSequenceLayout(source: string, measure: (text: string) => number) {
  if (!/^sequenceDiagram\b/.test(source.trimStart())) return { source, sequence: undefined };
  let controlWidth = 0, actorWidth = 0, messageWidth = 0, depth = 0, maxDepth = 0;
  const wrap = (label: string) => label.split(/<br\s*\/?\s*>/i).flatMap(line => {
    const lines: string[] = [];
    let current = "";
    // Whitespace boundaries only: never split an identifier or a Mermaid entity.
    for (const token of line.split(/(\s+)/).filter(Boolean)) {
      if (current.trim() && measure(current + token) > 480 && token.trim()) {
        lines.push(current.trimEnd()); current = token;
      } else current += token;
    }
    lines.push(current.trimEnd());
    return lines;
  });
  const display = source.split(/\r?\n/).map(line => {
    if (/^\s*end\b/.test(line)) depth = Math.max(0, depth - 1);
    const control = line.match(/^(\s*(?:alt|else|loop|opt|par|and|critical|option|break)\s+)(.*)$/);
    if (/^\s*(?:alt|loop|opt|par|critical|break)\b/.test(line)) maxDepth = Math.max(maxDepth, ++depth);
    const actor = line.match(/^(\s*(?:participant|actor)\s+\S+\s+as\s+)(.*)$/);
    const message = line.match(/^(\s*(?:\w+\s*(?:--?>>|--?x|--?\))\s*\w+|Note\s+(?:over|left of|right of)\s+[\w, ]+)\s*:\s*)(.*)$/i);
    const match = control ?? actor ?? message;
    if (!match) return line;
    const lines = wrap(match[2]);
    const width = Math.max(0, ...lines.map(measure));
    if (control) controlWidth = Math.max(controlWidth, width);
    else if (actor) actorWidth = Math.max(actorWidth, width);
    else messageWidth = Math.max(messageWidth, width);
    return match[1] + lines.join("<br/>");
  }).join("\n");
  const width = Math.ceil(Math.max(240, actorWidth + 32, controlWidth + 100));
  return { source: display, sequence: {
    width, actorMargin: Math.ceil(Math.max(100, messageWidth - width + 48)),
    boxMargin: 16 + Math.min(maxDepth, 16) * 2, boxTextMargin: 12,
    noteMargin: 16, messageMargin: 48, wrap: false, wrapPadding: 16,
    actorFontSize: 16, messageFontSize: 16, noteFontSize: 16,
    actorFontFamily: '"Pretendard Variable", "Malgun Gothic", sans-serif',
    messageFontFamily: '"Pretendard Variable", "Malgun Gothic", sans-serif',
    noteFontFamily: '"Pretendard Variable", "Malgun Gothic", sans-serif'
  } };
}
