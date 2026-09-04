import path from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import { Language, Parser } from "web-tree-sitter";

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));
let parserPromise;

async function createParser() {
  await Parser.init({
    locateFile() {
      return path.join(moduleDirectory, "node_modules", "web-tree-sitter", "web-tree-sitter.wasm");
    },
  });
  const language = await Language.load(
    path.join(moduleDirectory, "node_modules", "tree-sitter-cpp", "tree-sitter-cpp.wasm"),
  );
  const parser = new Parser();
  parser.setLanguage(language);
  return parser;
}

async function getParser() {
  parserPromise ??= createParser();
  return parserPromise;
}

function children(node) {
  const values = [];
  for (let index = 0; index < node.namedChildCount; index += 1) {
    values.push(node.namedChild(index));
  }
  return values;
}

function field(node, name) {
  return node.childForFieldName(name);
}

function cleanName(value) {
  return value
    .replace(/<[^<>]*>/g, "")
    .replace(/^\s*[&*]+\s*/, "")
    .replace(/\s+/g, "")
    .trim();
}

function fingerprint(value) {
  return createHash("sha256").update(value).digest("hex");
}

function lastName(value) {
  const cleaned = cleanName(value);
  return cleaned.split(/::|\.|->/).filter(Boolean).at(-1) ?? cleaned;
}

function countArguments(node) {
  const argumentsNode = field(node, "arguments");
  if (!argumentsNode) return 0;
  return children(argumentsNode).filter((child) => child.type !== "comment").length;
}

function functionDeclarator(declarator) {
  let current = declarator;
  while (current) {
    if (current.type === "function_declarator") return current;
    current = field(current, "declarator") ?? current.namedChild(0);
  }
  return null;
}

function declaratorIdentifier(declarator) {
  if (!declarator) return null;
  if (["identifier", "field_identifier", "operator_name", "destructor_name", "qualified_identifier"].includes(declarator.type)) {
    return declarator;
  }
  const nested = field(declarator, "declarator");
  if (nested && nested !== declarator) {
    const result = declaratorIdentifier(nested);
    if (result) return result;
  }
  for (const child of children(declarator)) {
    const result = declaratorIdentifier(child);
    if (result) return result;
  }
  return null;
}

function removeUtf8Ranges(node, ranges) {
  const bytes = Buffer.from(node.text, "utf8");
  const normalized = ranges
    .map(({ startIndex, endIndex }) => ({
      start: Math.max(0, startIndex - node.startIndex),
      end: Math.min(bytes.length, endIndex - node.startIndex),
    }))
    .filter((range) => range.end > range.start)
    .sort((left, right) => left.start - right.start);
  const chunks = [];
  let offset = 0;
  for (const range of normalized) {
    if (range.start > offset) chunks.push(bytes.subarray(offset, range.start));
    offset = Math.max(offset, range.end);
  }
  if (offset < bytes.length) chunks.push(bytes.subarray(offset));
  return Buffer.concat(chunks).toString("utf8");
}

function canonicalType(value) {
  return value.normalize("NFKC")
    .replace(/\s+/g, " ")
    .replace(/\s*([*&<>,()[\]])\s*/g, "$1")
    .trim();
}

function parameterTypes(declarator) {
  const functionNode = functionDeclarator(declarator);
  const parameters = functionNode ? field(functionNode, "parameters") : null;
  if (!parameters) return [];
  const values = children(parameters)
    .filter((child) => child.type === "parameter_declaration" || child.type === "optional_parameter_declaration")
    .map((parameter) => {
      const ranges = [];
      const parameterDeclarator = field(parameter, "declarator");
      const identifier = declaratorIdentifier(parameterDeclarator);
      if (identifier) ranges.push(identifier);
      const defaultValue = field(parameter, "default_value");
      if (defaultValue) {
        const prefix = Buffer.from(parameter.text, "utf8").subarray(0, defaultValue.startIndex - parameter.startIndex);
        const equalsAt = prefix.lastIndexOf("=".charCodeAt(0));
        ranges.push({
          startIndex: equalsAt >= 0 ? parameter.startIndex + equalsAt : defaultValue.startIndex,
          endIndex: defaultValue.endIndex,
        });
      }
      return canonicalType(removeUtf8Ranges(parameter, ranges));
    })
    .filter(Boolean);
  return values.length === 1 && values[0] === "void" ? [] : values;
}

function functionQualifiers(declarator) {
  const functionNode = functionDeclarator(declarator);
  if (!functionNode) return "";
  const values = children(functionNode)
    .filter((child) => child.type === "type_qualifier" || child.type === "ref_qualifier")
    .map((child) => canonicalType(child.text))
    .filter((value) => value === "const" || value === "volatile" || value === "&" || value === "&&");
  return values.length > 0 ? ` ${values.join(" ").replace(/\s+([&])/g, "$1")}` : "";
}

function declaratorName(declarator) {
  if (!declarator) return "";
  const name = field(declarator, "declarator");
  if (name && name !== declarator) return declaratorName(name);
  if (["identifier", "field_identifier", "operator_name", "destructor_name", "qualified_identifier"].includes(declarator.type)) {
    return cleanName(declarator.text);
  }
  for (const child of children(declarator)) {
    const candidate = declaratorName(child);
    if (candidate) return candidate;
  }
  return "";
}

function contextName(node) {
  return cleanName(field(node, "name")?.text ?? "");
}

function callArguments(node) {
  const argumentsNode = field(node, "arguments");
  return argumentsNode
    ? children(argumentsNode).filter((child) => child.type !== "comment").map((child) => child.text.trim())
    : [];
}

function scopeFor(node, kind, label, branch) {
  return {
    id: `${kind}_${node.startIndex}_${node.endIndex}`,
    kind,
    label: label.replace(/\s+/g, " ").trim().slice(0, 120),
    branch,
  };
}

function conditionLabel(node) {
  return (field(node, "condition")?.text ?? field(node, "value")?.text ?? node.text.split("{")[0])
    .replace(/\s+/g, " ").trim().slice(0, 120);
}

function collectCalls(node) {
  const calls = [];
  let order = 0;
  function visit(current, controlPath = []) {
    if (current !== node && current.type === "function_definition") return;
    if (current.type === "call_expression") {
      const expression = cleanName(field(current, "function")?.text ?? "");
      if (expression) {
        calls.push({
          expression,
          name: lastName(expression),
          argumentCount: countArguments(current),
          line: current.startPosition.row + 1,
          endLine: current.endPosition.row + 1,
          order: ++order,
          arguments: callArguments(current),
          controlPath,
        });
      }
      for (const child of children(current)) visit(child, controlPath);
      return;
    }
    if (current.type === "if_statement") {
      const condition = field(current, "condition");
      if (condition) visit(condition, controlPath);
      const label = conditionLabel(current);
      const consequence = field(current, "consequence");
      const alternative = field(current, "alternative");
      if (consequence) visit(consequence, [...controlPath, scopeFor(current, "alt", label, "then")]);
      if (alternative) visit(alternative, [...controlPath, scopeFor(current, "alt", label, "else")]);
      return;
    }
    if (["for_statement", "for_range_loop", "while_statement", "do_statement"].includes(current.type)) {
      const body = field(current, "body");
      for (const child of children(current)) {
        const isBody = body && child.startIndex === body.startIndex && child.endIndex === body.endIndex;
        visit(child, isBody
          ? [...controlPath, scopeFor(current, "loop", conditionLabel(current), "body")]
          : controlPath);
      }
      return;
    }
    for (const child of children(current)) visit(child, controlPath);
  }
  visit(node);
  return calls;
}

function compactStatement(node, fallback) {
  const value = node.text.replace(/\s+/g, " ").trim().replace(/[;{]\s*$/, "");
  return (value || fallback).slice(0, 140);
}

function buildControlFlow(functionNode, calls) {
  const nodes = [];
  const edges = [];
  let nextId = 0;
  const addNode = (kind, label, syntaxNode, callOrder = null) => {
    const item = {
      id: `c${++nextId}`,
      kind,
      label: label.slice(0, 160),
      startLine: syntaxNode?.startPosition.row + 1 || functionNode.startPosition.row + 1,
      endLine: syntaxNode?.endPosition.row + 1 || functionNode.endPosition.row + 1,
      callOrder,
    };
    nodes.push(item);
    return item;
  };
  const connect = (incoming, target) => {
    for (const source of incoming) {
      edges.push({ sourceId: source.id, targetId: target.id, type: source.type ?? "control", label: source.label ?? "" });
    }
  };
  const bodyNode = field(functionNode, "body");
  const entry = addNode("entry", "시작", {
    startPosition: functionNode.startPosition,
    endPosition: bodyNode?.startPosition ?? functionNode.startPosition,
  });
  const exit = addNode("exit", "종료", {
    startPosition: { row: functionNode.endPosition.row },
    endPosition: functionNode.endPosition,
  });

  function processSequence(statements, incoming, loopNode = null) {
    let pending = incoming;
    for (const statement of statements) {
      const halted = pending.filter((item) => item.breakLoop);
      const active = pending.filter((item) => !item.breakLoop);
      pending = [...halted, ...processStatement(statement, active, loopNode)];
    }
    return pending;
  }

  function directCalls(statement) {
    return calls.filter((call) => {
      const line = call.line;
      return line >= statement.startPosition.row + 1 && line <= statement.endPosition.row + 1;
    });
  }

  function processStatement(statement, incoming, loopNode) {
    if (!statement || statement.type === "comment") return incoming;
    if (statement.type === "compound_statement") return processSequence(children(statement), incoming, loopNode);
    if (statement.type === "if_statement") {
      const decision = addNode("condition", conditionLabel(statement), field(statement, "condition") ?? statement);
      connect(incoming, decision);
      const consequence = field(statement, "consequence");
      const alternative = field(statement, "alternative");
      const yes = consequence ? processStatement(consequence, [{ id: decision.id, label: "예" }], loopNode) : [{ id: decision.id, label: "예" }];
      const no = alternative ? processStatement(alternative, [{ id: decision.id, label: "아니오" }], loopNode) : [{ id: decision.id, label: "아니오" }];
      return [...yes, ...no];
    }
    if (["for_statement", "for_range_loop", "while_statement", "do_statement"].includes(statement.type)) {
      const body = field(statement, "body");
      const loopHeader = statement.type === "do_statement"
        ? (field(statement, "condition") ?? statement)
        : { startPosition: statement.startPosition, endPosition: body?.startPosition ?? statement.endPosition };
      const loop = addNode("loop", conditionLabel(statement), loopHeader);
      connect(incoming, loop);
      const bodyExit = body ? processStatement(body, [{ id: loop.id, label: "반복" }], loop) : [];
      for (const source of bodyExit.filter((item) => !item.breakLoop)) {
        edges.push({ sourceId: source.id, targetId: loop.id, type: "loopBack", label: "다음 반복" });
      }
      return [{ id: loop.id, label: "종료" }, ...bodyExit.filter((item) => item.breakLoop).map((item) => ({ id: item.id, label: "break" }))];
    }
    if (statement.type === "return_statement") {
      let pending = incoming;
      for (const call of directCalls(statement)) {
        const callNode = addNode("call", `${call.expression}(${(call.arguments ?? []).join(", ")})`, {
          startPosition: { row: call.line - 1 },
          endPosition: { row: (call.endLine ?? call.line) - 1 },
        }, call.order);
        connect(pending, callNode);
        pending = [{ id: callNode.id }];
      }
      const value = statement.text.replace(/^\s*return\s*/, "").replace(/;\s*$/, "").trim();
      const returned = addNode("return", value ? `return ${value}` : "return", statement);
      connect(pending, returned);
      edges.push({ sourceId: returned.id, targetId: exit.id, type: "return", label: "리턴" });
      return [];
    }
    if (statement.type === "continue_statement") {
      const continued = addNode("continue", "continue", statement);
      connect(incoming, continued);
      if (loopNode) edges.push({ sourceId: continued.id, targetId: loopNode.id, type: "loopBack", label: "다음 반복" });
      return [];
    }
    if (statement.type === "break_statement") {
      const broken = addNode("break", "break", statement);
      connect(incoming, broken);
      return [{ id: broken.id, breakLoop: true }];
    }

    const statementCalls = directCalls(statement);
    if (statementCalls.length > 0) {
      let pending = incoming;
      for (const call of statementCalls) {
        const callNode = addNode("call", `${call.expression}(${(call.arguments ?? []).join(", ")})`, {
          startPosition: { row: call.line - 1 },
          endPosition: { row: (call.endLine ?? call.line) - 1 },
        }, call.order);
        connect(pending, callNode);
        pending = [{ id: callNode.id }];
      }
      return pending;
    }
    if (["declaration", "expression_statement", "throw_statement"].includes(statement.type)) {
      const operation = addNode("operation", compactStatement(statement, "처리"), statement);
      connect(incoming, operation);
      return [{ id: operation.id }];
    }
    return processSequence(children(statement), incoming, loopNode);
  }

  const pending = bodyNode ? processStatement(bodyNode, [{ id: entry.id }], null) : [{ id: entry.id }];
  connect(pending, exit);
  return { nodes, edges };
}

function collectStringBindings(content) {
  const values = new Map();
  const references = [];
  const add = (name, value) => {
    const key = name.replace(/^this->/, "").trim();
    const list = values.get(key) ?? [];
    if (!list.includes(value)) list.push(value);
    values.set(key, list);
  };
  for (const match of content.matchAll(/^\s*#\s*define\s+([A-Za-z_]\w*)\s+"([^"]+)"/gm)) add(match[1], match[2]);
  for (const match of content.matchAll(/(?:^|[;{}])\s*(?:[\w:<>*&]+\s+)+([A-Za-z_]\w*)\s*=\s*"([^"]+)"/gm)) add(match[1], match[2]);
  for (const match of content.matchAll(/(?:this->)?([A-Za-z_]\w*)\s*=\s*"([^"]+)"\s*;/g)) add(match[1], match[2]);
  for (const match of content.matchAll(/(?:^|[;{}])\s*(?:[\w:<>*&]+\s+)+([A-Za-z_]\w*)\s*=\s*([A-Za-z_]\w*)\s*;/gm)) references.push([match[1], match[2]]);
  for (let pass = 0; pass < references.length; pass += 1) {
    for (const [name, source] of references) for (const value of values.get(source) ?? []) add(name, value);
  }
  return values;
}

function collectIncludes(root) {
  const includes = [];
  function visit(node) {
    if (node.type === "preproc_include") {
      const match = /[<\"]([^>\"]+)[>\"]/.exec(node.text);
      if (match) includes.push(match[1].replaceAll("\\", "/"));
    }
    for (const child of children(node)) visit(child);
  }
  visit(root);
  return [...new Set(includes)];
}

export async function parseCppFile(filepath, content, projectPath = null) {
  const parser = await getParser();
  const tree = parser.parse(content);
  const symbols = [];
  const definitions = [];

  function visit(node, namespaceParts, ownerParts) {
    if (node.type === "namespace_definition") {
      const name = contextName(node);
      const nextNamespace = name ? [...namespaceParts, name] : namespaceParts;
      for (const child of children(node)) visit(child, nextNamespace, ownerParts);
      return;
    }

    if (["class_specifier", "struct_specifier", "union_specifier"].includes(node.type)) {
      const name = contextName(node);
      if (name) {
        const qualifiedName = [...namespaceParts, ...ownerParts, name].join("::");
        const bases = field(node, "base_class_clause")
          ? children(field(node, "base_class_clause")).map((child) => cleanName(child.text)).filter(Boolean)
          : [];
        symbols.push({
          semanticKey: `type:${qualifiedName}`,
          qualifiedName,
          simpleName: name,
          kind: node.type.startsWith("class") ? "class" : "type",
          parameterCount: null,
          signature: node.text.split(/\r?\n/, 1)[0].slice(0, 240),
          filePath: filepath,
          projectPath,
          startLine: node.startPosition.row + 1,
          endLine: node.endPosition.row + 1,
          contentFingerprint: fingerprint(node.text),
          calls: [],
          bases,
        });
        const body = field(node, "body");
        if (body) {
          for (const child of children(body)) visit(child, namespaceParts, [...ownerParts, name]);
        }
      }
      return;
    }

    if (node.type === "function_definition") {
      const declarator = field(node, "declarator");
      const declared = declaratorName(declarator);
      if (declared) {
        const alreadyQualified = declared.includes("::");
        const qualifiedName = alreadyQualified
          ? [...namespaceParts, declared].filter(Boolean).join("::")
          : [...namespaceParts, ...ownerParts, declared].join("::");
        const parameters = parameterTypes(declarator);
        const parameterCount = parameters.length;
        const qualifiers = functionQualifiers(declarator);
        const calls = collectCalls(node);
        const control = buildControlFlow(node, calls);
        const symbol = {
          semanticKey: `function:${qualifiedName}(${parameters.join(",")})${qualifiers}`,
          qualifiedName,
          simpleName: lastName(declared),
          kind: ownerParts.length > 0 || alreadyQualified ? "method" : "function",
          parameterCount,
          signature: node.text.split("{")[0].replace(/\s+/g, " ").trim().slice(0, 240),
          filePath: filepath,
          projectPath,
          startLine: node.startPosition.row + 1,
          endLine: node.endPosition.row + 1,
          contentFingerprint: fingerprint(node.text),
          calls,
          bases: [],
          controlNodes: control.nodes,
          controlEdges: control.edges,
        };
        symbols.push(symbol);
        definitions.push(symbol.semanticKey);
      }
      return;
    }

    for (const child of children(node)) visit(child, namespaceParts, ownerParts);
  }

  const includes = collectIncludes(tree.rootNode);
  visit(tree.rootNode, [], []);
  const diagnostics = [];
  if (tree.rootNode.hasError) diagnostics.push(`C++ parser recovered from syntax errors in ${filepath}.`);
  tree.delete();
  return { filepath, projectPath, includes, symbols, definitions, diagnostics, stringBindings: collectStringBindings(content) };
}

function ownerName(qualifiedName) {
  const parts = qualifiedName.split("::");
  return parts.length > 1 ? parts.slice(0, -1).join("::") : "";
}

function normalizeExpression(value) {
  let result = String(value ?? "").trim();
  while (result.startsWith("(") && result.endsWith(")")) result = result.slice(1, -1).trim();
  return result.replace(/^this->/, "");
}

function resolveStringValue(expression, bindings, aliases = []) {
  const normalized = normalizeExpression(expression);
  const literal = /^"([^"\\]*(?:\\.[^"\\]*)*)"$/.exec(normalized);
  if (literal) return { value: literal[1].replace(/\\"/g, '"'), confidence: "Exact" };
  const alias = aliases.find((item) => normalizeExpression(item.expression) === normalized);
  if (alias?.targetType) return { value: alias.targetType, confidence: "Inferred" };
  const values = bindings.get(normalized) ?? bindings.get(lastName(normalized)) ?? [];
  return values.length === 1 ? { value: values[0], confidence: "Inferred" } : null;
}

function ruleMatches(rule, call) {
  const apiName = cleanName(rule.apiName ?? "");
  if (!rule.enabled || !apiName) return false;
  return apiName.includes("::")
    ? call.expression.replaceAll(".", "::").replaceAll("->", "::") === apiName
    : call.name === apiName;
}

export function resolveCppCalls(files, indirectCallRules = []) {
  const symbols = files.flatMap((file) => file.symbols);
  const byQualified = new Map();
  const bySimple = new Map();
  for (const symbol of symbols) {
    const qualifiedKey = `${symbol.qualifiedName}/${symbol.parameterCount ?? "type"}`;
    const qualifiedValues = byQualified.get(qualifiedKey) ?? [];
    qualifiedValues.push(symbol);
    byQualified.set(qualifiedKey, qualifiedValues);
    const key = `${symbol.simpleName}/${symbol.parameterCount ?? "type"}`;
    const values = bySimple.get(key) ?? [];
    values.push(symbol);
    bySimple.set(key, values);
  }

  const bindings = new Map();
  for (const file of files) {
    for (const [name, fileValues] of file.stringBindings ?? []) {
      const values = bindings.get(name) ?? [];
      for (const value of fileValues) if (!values.includes(value)) values.push(value);
      bindings.set(name, values);
    }
  }

  const edges = [];
  const excludedCalls = [];
  let excludedCallCount = 0;
  let ambiguousCallCount = 0;
  const resolvedControlCalls = new Map();
  const exclude = (source, call, reason, candidates = []) => {
    excludedCallCount += 1;
    if (reason === "multipleTargets") ambiguousCallCount += 1;
    if (excludedCalls.length < 500) {
      excludedCalls.push({
        filePath: source.filePath,
        line: call.line,
        sourceSemanticKey: source.semanticKey,
        expression: call.expression,
        reason,
        candidateTargets: candidates.slice(0, 10),
      });
    }
  };
  const rememberControlTarget = (source, call, target, isIndirect, viaApi) => {
    resolvedControlCalls.set(`${source.semanticKey}/${call.order}`, {
      targetSemanticKey: target.semanticKey,
      isIndirect,
      viaApi,
    });
  };

  for (const source of symbols.filter((symbol) => symbol.calls.length > 0)) {
    for (const call of source.calls) {
      const indirectRule = indirectCallRules.find((rule) => ruleMatches(rule, call));
      if (indirectRule) {
        const typeIndex = Number(indirectRule.targetTypeArgumentIndex);
        const methodIndex = indirectRule.targetMethodArgumentIndex === null || indirectRule.targetMethodArgumentIndex === undefined
          ? null
          : Number(indirectRule.targetMethodArgumentIndex);
        const typeValue = resolveStringValue(call.arguments?.[typeIndex], bindings, indirectRule.aliases ?? []);
        if (!typeValue) {
          exclude(source, call, "indirectTypeUnresolved");
          continue;
        }
        const typeSymbols = symbols.filter((symbol) => ["class", "type"].includes(symbol.kind));
        const exactTypes = typeSymbols.filter((symbol) => symbol.qualifiedName === typeValue.value);
        const typeCandidates = exactTypes.length > 0
          ? exactTypes
          : typeSymbols.filter((symbol) => symbol.simpleName === lastName(typeValue.value));
        if (typeCandidates.length !== 1) {
          exclude(source, call, typeCandidates.length > 1 ? "multipleTargets" : "indirectTypeNotFound",
            typeCandidates.map((candidate) => candidate.qualifiedName));
          continue;
        }
        let target = typeCandidates[0];
        let methodLabel = "";
        if (methodIndex !== null) {
          const methodValue = resolveStringValue(call.arguments?.[methodIndex], bindings);
          methodLabel = methodValue?.value ?? normalizeExpression(call.arguments?.[methodIndex] ?? "");
          if (methodValue) {
            const methods = symbols.filter((symbol) => ["method", "function"].includes(symbol.kind)
              && ownerName(symbol.qualifiedName) === target.qualifiedName
              && symbol.simpleName === methodValue.value);
            if (methods.length === 1) target = methods[0];
          }
        }
        const edge = {
          sourceSemanticKey: source.semanticKey,
          targetSemanticKey: target.semanticKey,
          type: "calls",
          label: methodLabel ? `${indirectRule.apiName}: ${methodLabel}` : indirectRule.apiName,
          confidence: typeValue.confidence,
          filePath: source.filePath,
          line: call.line,
          endLine: call.endLine,
          sequenceIndex: call.order,
          isIndirect: true,
          viaApi: indirectRule.apiName,
          controlPath: call.controlPath ?? [],
        };
        edges.push(edge);
        rememberControlTarget(source, call, target, true, indirectRule.apiName);
        continue;
      }

      const exactName = call.expression.replaceAll(".", "::").replaceAll("->", "::");
      const ownerCandidate = [ownerName(source.qualifiedName), call.name].filter(Boolean).join("::");
      const qualifiedCandidates = [
        ...(byQualified.get(`${exactName}/${call.argumentCount}`) ?? []),
        ...(exactName === ownerCandidate ? [] : (byQualified.get(`${ownerCandidate}/${call.argumentCount}`) ?? [])),
      ].filter((candidate, index, all) => all.indexOf(candidate) === index);
      let target = qualifiedCandidates.length === 1 ? qualifiedCandidates[0] : null;
      let confidence = "Exact";
      if (qualifiedCandidates.length > 1) {
        exclude(source, call, "multipleTargets", qualifiedCandidates.map((candidate) => candidate.qualifiedName));
      } else if (!target) {
        const candidates = bySimple.get(`${call.name}/${call.argumentCount}`) ?? [];
        if (candidates.length === 1) {
          [target] = candidates;
          confidence = "Inferred";
        } else if (candidates.length > 1) {
          exclude(source, call, "multipleTargets", candidates.map((candidate) => candidate.qualifiedName));
        }
      }
      if (!target || target.semanticKey === source.semanticKey) continue;
      edges.push({
        sourceSemanticKey: source.semanticKey,
        targetSemanticKey: target.semanticKey,
        type: "calls",
        label: call.expression,
        confidence,
        filePath: source.filePath,
        line: call.line,
        endLine: call.endLine,
        sequenceIndex: call.order,
        isIndirect: false,
        viaApi: null,
        controlPath: call.controlPath ?? [],
      });
      rememberControlTarget(source, call, target, false, null);
    }
  }

  for (const symbol of symbols) {
    symbol.controlNodes = (symbol.controlNodes ?? []).map((node) => ({
      ...node,
      ...(node.callOrder ? resolvedControlCalls.get(`${symbol.semanticKey}/${node.callOrder}`) : null),
    }));
  }

  return {
    files,
    symbols,
    edges: edges.filter((edge, index, all) => all.findIndex((candidate) =>
      candidate.sourceSemanticKey === edge.sourceSemanticKey
      && candidate.targetSemanticKey === edge.targetSemanticKey
      && candidate.type === edge.type
      && candidate.line === edge.line
      && candidate.sequenceIndex === edge.sequenceIndex) === index),
    diagnostics: files.flatMap((file) => file.diagnostics),
    ambiguousCallCount,
    excludedCalls,
    excludedCallCount,
    excludedCallsTruncated: excludedCallCount > excludedCalls.length,
  };
}
