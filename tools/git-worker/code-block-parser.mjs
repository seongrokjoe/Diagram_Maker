import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import { getParser, parseCppFile, resolveCppCalls } from "./cpp-indexer.mjs";

const hash = (...values) => createHash("sha256").update(values.join("\0")).digest("hex").slice(0, 32);
const children = node => node.namedChildren;
const field = (node, name) => node.childForFieldName(name);
function walk(node, visit) { visit(node); for (const child of children(node)) walk(child, visit); }
function transfers(node) {
  if (!node) return false;
  if (["return_statement", "throw_statement", "break_statement", "continue_statement"].includes(node.type)) return true;
  if (node.type === "compound_statement") return transfers(node.namedChildren.filter(n => n.type !== "comment").at(-1));
  if (node.type === "else_clause") return transfers(node.namedChild(0));
  return node.type === "if_statement" && transfers(field(node, "consequence")) && transfers(field(node, "alternative"));
}
function unreachable(node, declaration) {
  for (let p = node; p && p !== declaration; p = p.parent)
    if (p.parent?.type === "compound_statement" && p.parent.namedChildren.some(n => n.endIndex <= p.startIndex && transfers(n))) return true;
  return false;
}

// web-tree-sitter's JavaScript binding exposes UTF-16 indices. No user paths,
// includes, build commands or repository operations are accepted by this worker.
export async function analyzeCodeBlocks(blocks, limits = { maximumBlocks: 20, maximumBlockCharacters: 100000, maximumTotalCharacters: 1000000 }) {
  if (!Array.isArray(blocks) || blocks.length > limits.maximumBlocks || blocks.some(b => typeof b.code !== "string" || b.code.length > limits.maximumBlockCharacters) ||
    blocks.reduce((sum, b) => sum + b.code.length, 0) > limits.maximumTotalCharacters)
    throw new Error("INPUT_LIMIT");
  const parser = await getParser();
  const files = [];
  const evidence = new Map();
  const warnings = [];
  const transitions = [];
  for (const block of blocks) {
    let prefix = "";
    let parsed = await parseCppFile(`${block.id}.cpp`, block.code);
    let tree = parser.parse(block.code);
    // Standalone declarations are real input, not a function body.
    const declarationOnly = tree.rootNode.namedChildren.every(n => ["comment", "preproc_include", "enum_specifier"].includes(n.type) ||
      n.type === "declaration" && n.namedChildren.some(c => c.type === "function_declarator" || c.type === "enum_specifier"));
    if (parsed.symbols.length === 0 && !declarationOnly) {
      tree.delete();
      prefix = "void __Fragment__() {\n";
      parsed = await parseCppFile(`${block.id}.cpp`, prefix + block.code + "\n}");
      tree = parser.parse(prefix + block.code + "\n}");
      warnings.push(`${block.title}: 함수 내부 코드 조각입니다. 바깥 실행 문맥은 미확인입니다.`);
    }
    const contentHash = createHash("sha256").update(block.code).digest("hex");
    const line = offset => 1 + (block.code.slice(0, offset).match(/\r\n|\r|\n/g) ?? []).length;
    const location = (start, end) => {
      start = Math.max(0, Math.min(block.code.length, start - prefix.length));
      end = Math.max(start, Math.min(block.code.length, end - prefix.length));
      return { blockId: block.id, startLine: line(start), endLine: line(end > start ? end - 1 : end), startOffset: start, endOffset: end };
    };
    const cite = loc => {
      const id = hash("evidence", block.id, contentHash, loc.startOffset, loc.endOffset);
      evidence.set(id, { id, blockId: block.id, contentHash, location: loc }); return id;
    };
    const nodes = [];
    walk(tree.rootNode, node => nodes.push(node));
    const scopes = node => {
      const result = [];
      for (let p = node.parent; p && p.type !== "function_definition"; p = p.parent) {
        if (p.type === "compound_statement") {
          for (const guard of p.namedChildren.filter(n => n.type === "if_statement" && n.endIndex <= node.startIndex).reverse()) {
            const thenExits = transfers(field(guard, "consequence")); const elseExits = transfers(field(guard, "alternative"));
            if (thenExits !== elseExits) result.unshift({ id: hash(block.id, contentHash, guard.startIndex), kind: "alt",
              label: field(guard, "condition")?.text ?? "조건", branch: thenExits ? "else" : "then" });
          }
        }
        if (p.type === "if_statement" && !(field(p, "condition")?.startIndex <= node.startIndex && field(p, "condition")?.endIndex >= node.endIndex)) result.unshift({ id: hash(block.id, contentHash, p.startIndex), kind: "alt",
          label: field(p, "condition")?.text ?? "조건", branch: field(p, "consequence")?.endIndex >= node.endIndex && field(p, "consequence")?.startIndex <= node.startIndex ? "then" : "else" });
        if (["while_statement", "for_statement", "do_statement", "for_range_loop"].includes(p.type))
          result.unshift({ id: hash(block.id, contentHash, p.startIndex), kind: "loop", label: field(p, "condition")?.text ?? p.text.split("{")[0], branch: "body" });
        if (p.type === "case_statement") result.unshift({ id: hash(block.id, contentHash, p.parent.startIndex), kind: "alt", label: "switch", branch: field(p, "value")?.text ?? "default" });
      }
      return result;
    };
    for (const symbol of parsed.symbols) {
      const declaration = nodes.find(n => ["function_definition", "class_specifier", "struct_specifier", "union_specifier"].includes(n.type)
        && n.startIndex === symbol.startOffset && n.endIndex === symbol.endOffset);
      symbol.id = hash("symbol", block.id, contentHash, symbol.semanticKey, declaration?.startIndex ?? 0);
      symbol.blockId = block.id;
      symbol.location = location(declaration?.startIndex ?? 0, declaration?.endIndex ?? block.code.length + prefix.length);
      symbol.evidenceIds = [cite(symbol.location)];
      if (["class", "type"].includes(symbol.kind) && declaration) {
        const clause = declaration.namedChildren.find(n => n.type === "base_class_clause");
        if (clause) symbol.bases = clause.namedChildren.filter(n => /type_identifier|qualified_identifier|template_type/.test(n.type)).map(n => n.text);
      }
      symbol.isFragment = Boolean(prefix);
      symbol.fileLocal = /\bstatic\b/.test(symbol.signature) || /namespace\s*\{/.test(block.code);
      symbol.localBindings = [];
      for (const binding of nodes.filter(n => ["parameter_declaration", "declaration"].includes(n.type) &&
        n.startIndex > declaration?.startIndex && n.endIndex < declaration?.endIndex)) {
        const declarator = field(binding, "declarator");
        if (declarator) walk(declarator, n => { if (n.type === "identifier") symbol.localBindings.push(n.text); });
      }
      symbol.steps = (symbol.controlNodes ?? []).map(n => {
        const ast = nodes.find(x => x.startIndex === n.startOffset && x.endIndex === n.endOffset) ??
          nodes.filter(x => x.startIndex <= n.startOffset && x.endIndex >= n.endOffset).sort((a, b) => a.text.length - b.text.length)[0];
        const loc = location(n.startOffset, n.endOffset);
        const call = symbol.calls.find(c => c.order === n.callOrder);
        return { id: hash(symbol.id, n.id), kind: n.kind, label: n.label,
          statement: block.code.slice(loc.startOffset, loc.endOffset), location: loc, evidenceIds: [cite(loc)],
          controlPath: ast ? scopes(ast) : [], target: call?.name, arguments: call?.arguments ?? [], purpose: n.kind };
      });
      symbol.flowEdges = (symbol.controlEdges ?? []).map(e => ({ ...e, sourceId: hash(symbol.id, e.sourceId), targetId: hash(symbol.id, e.targetId) }));
      symbol.calls = symbol.calls.filter(c => {
        const ast = nodes.find(n => n.type === "call_expression" && n.startIndex === c.startOffset && n.endIndex === c.endOffset);
        return !ast || !unreachable(ast, declaration);
      }).map(c => {
        const loc = location(c.startOffset, c.endOffset); cite(loc);
        const ast = nodes.find(n => n.type === "call_expression" && n.startIndex === c.startOffset && n.endIndex === c.endOffset);
        const controlPath = ast ? [...scopes(ast), ...c.controlPath.filter(s => s.kind === "unordered").map(s => ({ ...s, id: hash(symbol.id, s.id) }))] : c.controlPath;
        return { ...c, id: hash(symbol.id, "call", loc.startOffset), symbolId: symbol.id, location: loc,
          statement: block.code.slice(loc.startOffset, loc.endOffset), receiver: c.receiver,
          controlPath, orderUncertain: c.controlPath.some(s => s.kind === "unordered") };
      });
      const mapExecution = items => items.map(e => {
        const loc = location(e.startOffset, e.endOffset);
        return { ...e, id: hash(symbol.id, e.id), startOffset: loc.startOffset, endOffset: loc.endOffset,
          evidenceIds: [cite(loc)], children: mapExecution(e.children), alternative: mapExecution(e.alternative),
          evaluation: mapExecution(e.evaluation), terminationTarget: e.terminationTarget?.startsWith("loop_") ? hash(symbol.id, e.terminationTarget) : e.terminationTarget,
          callSiteId: e.kind === "call" ? symbol.calls.find(c => c.location.startOffset === loc.startOffset && c.location.endOffset === loc.endOffset)?.id : null };
      });
      symbol.execution = mapExecution(symbol.execution ?? []);
      if (declaration?.type === "function_definition" && !tree.rootNode.hasError) {
        for (const a of nodes.filter(n => n.type === "assignment_expression" && n.startIndex > declaration.startIndex && n.endIndex < declaration.endIndex)) {
          const left = field(a, "left")?.text; const right = field(a, "right")?.text;
          if (!left || !right || field(a, "operator")?.text !== "=" || !/^[\w:.]+$/.test(right)) continue;
          if (unreachable(a, declaration)) continue;
          let from = null; let origin = null;
          for (let p = a.parent; p && p !== declaration; p = p.parent) {
            if (p.type === "if_statement" && field(p, "consequence")?.startIndex <= a.startIndex && field(p, "consequence")?.endIndex >= a.endIndex) {
              const condition = field(p, "condition");
              const expression = condition?.namedChild(0);
              if (expression?.type === "binary_expression" && field(expression, "operator")?.text === "==") {
                if (field(expression, "left")?.text === left) { from = field(expression, "right")?.text; origin = expression; }
                else if (field(expression, "right")?.text === left) { from = field(expression, "left")?.text; origin = expression; }
              }
            }
            if (p.type === "case_statement" && field(p, "value")) {
              const selection = p.parent?.parent;
              const clauses = p.parent.namedChildren.filter(n => n.type === "case_statement");
              const prior = clauses[clauses.indexOf(p) - 1];
              const mayFallThrough = prior && !["break_statement", "return_statement", "throw_statement"].includes(prior.namedChildren.at(-1)?.type);
              if (!mayFallThrough && selection?.type === "switch_statement" && field(selection, "condition")?.namedChild(0)?.text === left) { from = field(p, "value").text; origin = p; }
            }
            if (from) break;
          }
          const invalidated = origin && nodes.some(n => (n.type === "call_expression" || n.type === "assignment_expression" && field(n, "left")?.text === left) &&
            n.startIndex > origin.startIndex && n.endIndex < a.startIndex && scopes(n).every(scope => scopes(a).some(s => s.id === scope.id && s.branch === scope.branch)));
          if (from && origin && !invalidated) transitions.push({ id: hash(symbol.id, "state", a.startIndex), symbolId: symbol.id, variable: left, from, to: right,
            condition: scopes(a).map(s => s.branch === "else" ? `!(${s.label})` : s.label).join(" && "),
            evidenceIds: [cite(location(a.startIndex, a.endIndex)), cite(location(origin.startIndex, origin.endIndex))] });
        }
      }
    }
    if (tree.rootNode.hasError) warnings.push(`${block.title}: 문법 오류 또는 잘린 구문에서 복구한 결과입니다. 누락된 구문을 확인하세요.`);
    if (parsed.symbols.length === 0) warnings.push(`${block.title}: 분석할 함수나 타입을 찾지 못했습니다. 선언의 구현 또는 처리 구문을 포함해 주세요.`);
    files.push(parsed); tree.delete();
  }
  const resolution = resolveCppCalls(files);
  const symbols = files.flatMap(f => f.symbols);
  for (const source of symbols) for (const call of source.calls) {
    const candidates = symbols.filter(t => call.candidateSemanticKeys?.includes(t.semanticKey) && (!t.fileLocal || t.blockId === source.blockId));
    const exact = candidates.filter(t => t.semanticKey === call.resolvedSemanticKey);
    const target = call.resolutionConfidence === "Exact" && exact.length === 1 && !source.isFragment &&
      (call.receiver || !source.localBindings.includes(call.name)) ? exact[0] : null;
    call.targetSymbolId = target?.id ?? null;
    call.candidateSymbolIds = candidates.map(t => t.id);
  }
  return { symbols: symbols.map(s => ({ id: s.id, blockId: s.blockId, name: s.isFragment ? blocks.find(b => b.id === s.blockId).title : s.qualifiedName,
    kind: s.isFragment ? "fragment" : s.kind, signature: s.isFragment ? "코드 조각" : s.signature, location: s.location, evidenceIds: s.evidenceIds,
    steps: s.steps, flowEdges: s.flowEdges, calls: s.calls, execution: s.execution, members: s.members ?? [], baseTypes: s.bases, parameterCount: s.parameterCount,
    isFragment: s.isFragment, ownerId: symbols.find(t => t.semanticKey === s.ownerSemanticKey)?.id ?? null })),
    relations: [], evidence: [...evidence.values()], transitions, warnings, analyzerVersion: "code-block-v1" };
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    let input = "";
    for await (const chunk of process.stdin) { input += chunk; if (input.length > 12000000) throw new Error("INPUT_LIMIT"); }
    const request = JSON.parse(input);
    process.stdout.write(JSON.stringify(await analyzeCodeBlocks(request.blocks, request.limits)));
  } catch { process.stderr.write("CODE_BLOCK_PARSE_FAILED\n"); process.exitCode = 1; }
}
