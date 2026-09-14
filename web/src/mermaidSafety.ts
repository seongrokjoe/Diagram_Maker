// Inspect syntax after removing only recognized display strings. Mermaid strict
// mode and SVG sanitizing remain separate layers at the rendering boundary.
export const maximumMermaidCharacters = 1_000_000;
export function mermaidSafetyError(source: string): string | null {
  if (source.length > maximumMermaidCharacters) return "그림이 표시 크기 한도를 넘었습니다. 상세 페이지에서 확인하세요.";
  const blocked = "보안 차단: 링크 동작, click 명령, 설정 지시문 또는 삽입 구문은 사용할 수 없습니다.";
  if (/```|%%\{|^\s*---|<\/?[a-z][^>]*>/im.test(source)) return blocked;
  const type = source.trimStart().split(/\s/, 1)[0];
  const syntax = source.split(/[\r\n\u2028\u2029]/).map(line => {
    // Quoted labels cannot extend across statements or physical lines.
    let code = line.replace(/"[^"\r\n]*"/g, '""');
    if (type === "sequenceDiagram") {
      code = code.replace(/^(\s*participant\s+\w+\s+as\s+)[^;]+$/, "$1");
      code = code.replace(/^(\s*(?:\w+\s*(?:--?>>|--?x|--?\))\s*\w+|Note\s+(?:over|left of|right of)\s+[\w, ]+)\s*:)[^;]*$/i, "$1");
      code = code.replace(/^(\s*(?:alt|else|loop|opt|par|and|critical|break)\s+)[^;]*$/, "$1");
    } else if (type === "classDiagram" || type === "stateDiagram-v2") {
      code = code.replace(/^(\s*(?:\w+|\[\*\])(?:\s+(?:<\|--|<\|\.\.|-->|\.\.>|--|--\*)\s+(?:\w+|\[\*\]))?\s+:)[^;]*$/, "$1");
    } else if (type === "flowchart" || type === "graph") {
      code = code.replace(/\[[^\[\]\r\n]*\]/g, "[]").replace(/\|[^|\r\n]*\|/g, "||");
      code = code.replace(/-\.\s+[^;]*?\s+\.->/g, "-.->");
    }
    return code;
  }).join("\n");
  return /%%|\b(?:click|link|links|callback|href)\s|(?:https?|javascript|data|vbscript):|url\s*\(|@\s*\{/i.test(syntax) ? blocked : null;
}
