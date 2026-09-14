import path from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import { Language, Parser } from "web-tree-sitter";
import { executionFacts, isCppCast } from "./execution-facts.mjs";

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

export async function getParser() {
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

function removeSourceRanges(node, ranges) {
  // web-tree-sitter's JS indices and strings both use UTF-16 code units.
  const source = node.text;
  const normalized = ranges
    .map(({ startIndex, endIndex }) => ({
      start: Math.max(0, startIndex - node.startIndex),
      end: Math.min(source.length, endIndex - node.startIndex),
    }))
    .filter((range) => range.end > range.start)
    .sort((left, right) => left.start - right.start);
  const chunks = [];
  let offset = 0;
  for (const range of normalized) {
    if (range.start > offset) chunks.push(source.slice(offset, range.start));
    offset = Math.max(offset, range.end);
  }
  if (offset < source.length) chunks.push(source.slice(offset));
  return chunks.join("");
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
        const prefix = parameter.text.slice(0, defaultValue.startIndex - parameter.startIndex);
        const equalsAt = prefix.lastIndexOf("=");
        ranges.push({
          startIndex: equalsAt >= 0 ? parameter.startIndex + equalsAt : defaultValue.startIndex,
          endIndex: defaultValue.endIndex,
        });
      }
      return canonicalType(removeSourceRanges(parameter, ranges));
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

// Resolve only lexical type evidence. Never infer an object's type from its name.
function expressionType(expression, useSite, depth = 0, localOnly = false) {
  if (!expression || depth > 12) return null;
  if (expression.type === "parenthesized_expression") return expressionType(expression.namedChild(0), useSite, depth + 1);
  if (isCppCast(expression)) return field(field(expression, "function"), "arguments")?.namedChild(0)?.text ?? null;
  if (expression.type === "cast_expression") return field(expression, "type")?.text ?? null;
  if (expression.type === "number_literal") return /^\d+$/.test(expression.text) ? "int" : null;
  if (expression.type === "true" || expression.type === "false") return "bool";
  if (expression.type === "string_literal") return "const char*";
  if (expression.type !== "identifier") return null;
  for (let scope = useSite.parent; scope; scope = scope.parent) {
    const declarations = children(scope).filter(n => n.endIndex <= useSite.startIndex &&
      ["declaration", "parameter_declaration", "optional_parameter_declaration", "field_declaration"].includes(n.type));
    if (scope.type === "function_definition") {
      const parameters = field(functionDeclarator(field(scope, "declarator")), "parameters");
      if (parameters) declarations.push(...children(parameters));
    }
    for (const declaration of declarations.reverse()) {
      for (const declarator of children(declaration).filter(n => !["type_identifier", "primitive_type", "type_qualifier"].includes(n.type))) {
        if (declaratorIdentifier(declarator)?.text !== expression.text) continue;
        const type = field(declaration, "type")?.text;
        if (type === "auto") return expressionType(field(declarator, "value"), declaration, depth + 1);
        return type ? canonicalType(type + (/pointer_declarator/.test(declarator.toString()) ? "*" : "")) : null;
      }
    }
    if (localOnly && scope.type === "function_definition") break;
  }
  return null;
}

function receiverType(expression, call) {
  let type = expressionType(expression, call);
  if (!type) return null;
  type = type.replace(/\b(const|volatile|class|struct)\b|[*&]/g, "").trim();
  // Follow visible using/typedef aliases, bounded to avoid cycles.
  for (let depth = 0; depth < 12; depth++) {
    let alias = null;
    for (let scope = call.parent; scope && !alias; scope = scope.parent) {
      alias = children(scope).filter(n => n.endIndex <= call.startIndex &&
        (n.type === "alias_declaration" && field(n, "name")?.text === type ||
         n.type === "type_definition" && field(n, "declarator")?.text === type)).at(-1);
    }
    if (!alias) return type;
    const next = field(alias, "type")?.text.replace(/\b(const|volatile)\b|[*&]/g, "").trim();
    if (!next || next === type) return null;
    type = next;
  }
  return null;
}

function collectCalls(node) {
  const calls = [];
  let order = 0;
  function visit(current, controlPath = [], parentOrder = null) {
    if (current !== node && current.type === "function_definition") return;
    if (current.type === "call_expression") {
      if (isCppCast(current)) {
        for (const child of children(field(current, "arguments"))) visit(child, controlPath, parentOrder);
        return;
      }
      const expression = cleanName(field(current, "function")?.text ?? "");
      const functionNode = field(current, "function");
      const receiver = functionNode?.type === "field_expression" ? field(functionNode, "argument") : null;
      const currentOrder = current.startIndex;
      // Nested calls must complete before their enclosing call. Argument-to-argument
      // order is not guaranteed by C++; retain that uncertainty in the control scope.
      const argumentsNode = field(current, "arguments");
      const argumentCalls = argumentsNode ? children(argumentsNode).filter(child => /\(/.test(child.text)) : [];
      for (const child of children(current)) visit(child, argumentCalls.length > 1
        ? [...controlPath, scopeFor(current, "unordered", "인자 평가 순서 미확정", "arguments")]
        : controlPath, currentOrder);
      if (expression) {
        calls.push({
          expression,
          name: lastName(receiver ? field(functionNode, "field")?.text ?? expression : expression),
          receiver: receiver?.text ?? null,
          receiverType: receiver ? receiverType(receiver, current) : null,
          calleeIsLocal: functionNode?.type === "identifier" && !!expressionType(functionNode, current, 0, true),
          argumentTypes: children(field(current, "arguments")).filter(n => n.type !== "comment").map(n => expressionType(n, current)),
          argumentCount: countArguments(current),
          line: current.startPosition.row + 1,
          endLine: current.endPosition.row + 1,
          order: ++order,
          startOffset: current.startIndex,
          endOffset: current.endIndex,
          parentOrder,
          arguments: callArguments(current),
          controlPath,
        });
      }
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
    if (current.type === "switch_statement") {
      const condition = field(current, "condition");
      if (condition) visit(condition, controlPath);
      const body = field(current, "body");
      const clauses = body ? children(body).filter(child => child.type === "case_statement") : [];
      const hasFallthrough = clauses.slice(0, -1).some(clause => !/\b(break|return|throw)\b[^{}]*;\s*$/.test(clause.text));
      for (const clause of clauses) {
        const value = field(clause, "value");
        const label = value ? `case ${value.text}` : "default";
        // Fallthrough is not a mutually exclusive alternative. Keep case-local
        // facts and defer execution paths to the control-flow diagram explicitly.
        const scope = scopeFor(current, hasFallthrough ? "unordered" : "alt",
          hasFallthrough ? `switch ${conditionLabel(current)}: case별 호출 목록, fallthrough 실행 경로는 흐름도 참조` : `switch ${conditionLabel(current)}`, label);
        for (const child of children(clause)) if (!value || child.startIndex !== value.startIndex) visit(child, [...controlPath, scope]);
      }
      return;
    }
    for (const child of children(current)) visit(child, controlPath);
  }
  visit(node);
  return calls;
}

function compactStatement(node, fallback) {
  const value = node.text.replace(/\s+/g, " ").trim();
  return (value || fallback).slice(0, 1000);
}

function compactSignature(node) {
  return node.text.split("{")[0].replace(/\s+/g, " ").replace(/;\s*$/, "").trim().slice(0, 240);
}

function memberFact(node, accessibility, declarator = field(node, "declarator")) {
  const functionNode = functionDeclarator(declarator);
  const declared = declaratorName(declarator);
  if (!declared) return null;
  const type = canonicalType(field(node, "type")?.text ?? "");
  const parameters = functionNode ? parameterTypes(declarator) : [];
  return {
    name: lastName(declared),
    kind: functionNode || node.type === "function_definition" ? "method" : "field",
    accessibility,
    signature: functionNode || node.type === "function_definition"
      ? compactSignature(node)
      : `${type} ${declarator.text}`.trim().replace(/;\s*$/, "").slice(0, 240),
    declaredType: type || null,
    isStatic: /\bstatic\b/.test(node.text.split(/[;{]/, 1)[0]),
    startLine: node.startPosition.row + 1,
    endLine: node.endPosition.row + 1,
    referencedTypes: [...new Set([type, ...parameters].filter(Boolean))],
  };
}

function memberFacts(node, accessibility) {
  if (node.type === "function_definition") {
    const member = memberFact(node, accessibility);
    return member ? [member] : [];
  }
  const typeNode = field(node, "type");
  const declarators = children(node).filter((child) => child !== typeNode &&
    !["storage_class_specifier", "type_qualifier", "attribute_specifier", "attribute_declaration", "comment"].includes(child.type) &&
    (functionDeclarator(child) || declaratorName(child)));
  const candidates = declarators.length > 0 ? declarators : [field(node, "declarator")].filter(Boolean);
  return candidates.map((declarator) => memberFact(node, accessibility, declarator)).filter(Boolean);
}

function collectClassMembers(typeNode) {
  const body = field(typeNode, "body");
  if (!body) return [];
  let accessibility = typeNode.type === "class_specifier" ? "private" : "public";
  const members = [];
  for (const child of children(body)) {
    if (child.type === "access_specifier") {
      accessibility = child.text.replace(":", "").trim().toLowerCase();
      continue;
    }
    if (child.type === "function_definition" || child.type === "field_declaration" || child.type === "declaration") {
      members.push(...memberFacts(child, accessibility));
    }
  }
  return members;
}

function buildControlFlow(functionNode, calls) {
  const nodes = [];
  const edges = [];
  let nextId = 0;
  const addNode = (kind, label, syntaxNode, callOrder = null) => {
    const item = {
      id: `c${++nextId}`,
      kind,
      label: label.slice(0, 1000),
      startLine: syntaxNode?.startPosition.row + 1 || functionNode.startPosition.row + 1,
      endLine: syntaxNode?.endPosition.row + 1 || functionNode.endPosition.row + 1,
      startOffset: syntaxNode?.startIndex ?? functionNode.startIndex,
      endOffset: syntaxNode?.endIndex ?? functionNode.endIndex,
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
    startIndex: functionNode.startIndex, endIndex: bodyNode?.startIndex ?? functionNode.startIndex,
    startPosition: functionNode.startPosition,
    endPosition: bodyNode?.startPosition ?? functionNode.startPosition,
  });
  const exit = addNode("exit", "종료", {
    startIndex: Math.max(functionNode.startIndex, functionNode.endIndex - 1), endIndex: functionNode.endIndex,
    startPosition: { row: functionNode.endPosition.row },
    endPosition: functionNode.endPosition,
  });

  function processSequence(statements, incoming, loopNode = null, switchNode = null) {
    let pending = incoming;
    for (let index = 0; index < statements.length; index++) {
      const statement = statements[index];
      const halted = pending.filter((item) => item.breakLoop || item.breakSwitch);
      const active = pending.filter((item) => !item.breakLoop && !item.breakSwitch);
      if (active.length === 0) break;
      if (isSimpleAssignment(statement)) {
        const grouped = [statement];
        while (index + 1 < statements.length && isSimpleAssignment(statements[index + 1]) &&
          statements[index + 1].startPosition.row <= grouped.at(-1).endPosition.row + 1) {
          grouped.push(statements[++index]);
        }
        const label = grouped.map((item) => compactStatement(item, "처리")).join("\n").slice(0, 1000);
        const operation = addNode("operation", label, {
          startIndex: grouped[0].startIndex, endIndex: grouped.at(-1).endIndex,
          startPosition: grouped[0].startPosition,
          endPosition: grouped.at(-1).endPosition,
        });
        connect(active, operation);
        pending = [...halted, { id: operation.id }];
      } else {
        pending = [...halted, ...processStatement(statement, active, loopNode, switchNode)];
      }
    }
    return pending;
  }

  function directCalls(statement) {
    return calls.filter((call) => {
      return call.startOffset >= statement.startIndex && call.endOffset <= statement.endIndex;
    });
  }

  function topLevelCalls(statement) {
    return directCalls(statement).filter((call) => call.parentOrder === null);
  }

  function isSimpleAssignment(statement) {
    if (!statement || topLevelCalls(statement).length > 0) return false;
    if (statement.type === "declaration") return statement.text.includes("=");
    if (statement.type !== "expression_statement") return false;
    return children(statement).some((child) => child.type === "assignment_expression");
  }

  function processStatement(statement, incoming, loopNode, switchNode = null) {
    if (!statement || statement.type === "comment") return incoming;
    if (incoming.length === 0) return [];
    if (statement.type === "compound_statement") return processSequence(children(statement), incoming, loopNode, switchNode);
    if (statement.type === "if_statement") {
      const decision = addNode("condition", conditionLabel(statement), field(statement, "condition") ?? statement);
      connect(incoming, decision);
      const consequence = field(statement, "consequence");
      const alternative = field(statement, "alternative");
      const yes = consequence ? processStatement(consequence, [{ id: decision.id, label: "예" }], loopNode, switchNode) : [{ id: decision.id, label: "예" }];
      const no = alternative ? processStatement(alternative, [{ id: decision.id, label: "아니오" }], loopNode, switchNode) : [{ id: decision.id, label: "아니오" }];
      return [...yes, ...no];
    }
    if (statement.type === "switch_statement") {
      const condition = field(statement, "condition") ?? statement;
      const decision = addNode("condition", `switch ${conditionLabel(statement)}`, condition);
      connect(incoming, decision);
      const body = field(statement, "body");
      const clauses = body ? children(body).filter((item) => item.type === "case_statement") : [];
      if (clauses.length === 0) return [{ id: decision.id }];
      const exits = [];
      let fallthrough = [];
      for (const clause of clauses) {
        const value = field(clause, "value");
        const label = value ? `case ${compactStatement(value, "값")}` : "default";
        const statements = children(clause).filter((item) => !value || item.startIndex !== value.startIndex || item.endIndex !== value.endIndex);
        const caseNode = addNode("case", label, value ?? clause);
        connect([{ id: decision.id, label }, ...fallthrough.map(item => ({ ...item, label: "fallthrough" }))], caseNode);
        const branch = processSequence(statements, [{ id: caseNode.id }], loopNode, decision);
        exits.push(...branch.filter(item => item.breakSwitch).map(item => ({ id: item.id })));
        fallthrough = branch.filter(item => !item.breakSwitch);
      }
      if (!clauses.some(clause => !field(clause, "value"))) exits.push({ id: decision.id, label: "일치 없음" });
      return [...exits, ...fallthrough];
    }
    if (["for_statement", "for_range_loop", "while_statement", "do_statement"].includes(statement.type)) {
      const body = field(statement, "body");
      const loopHeader = statement.type === "do_statement"
        ? (field(statement, "condition") ?? statement)
        : { startPosition: statement.startPosition, endPosition: body?.startPosition ?? statement.endPosition,
          startIndex: statement.startIndex, endIndex: body?.startIndex ?? statement.endIndex };
      const loop = addNode("loop", conditionLabel(statement), loopHeader);
      const postTest = statement.type === "do_statement";
      const bodyEntry = postTest ? addNode("operation", "반복 처리 시작", body ?? statement) : null;
      connect(incoming, bodyEntry ?? loop);
      if (bodyEntry) connect([{ id: loop.id, label: "반복" }], bodyEntry);
      const bodyExit = body ? processStatement(body, [{ id: bodyEntry?.id ?? loop.id, label: postTest ? "" : "반복" }], loop, null) : [];
      for (const source of bodyExit.filter((item) => !item.breakLoop)) {
        edges.push({ sourceId: source.id, targetId: loop.id, type: "loopBack", label: "다음 반복" });
      }
      return [{ id: loop.id, label: "종료" }, ...bodyExit.filter((item) => item.breakLoop).map((item) => ({ id: item.id, label: "break" }))];
    }
    if (["return_statement", "throw_statement"].includes(statement.type)) {
      const [call] = topLevelCalls(statement);
      const returned = addNode("return", compactStatement(statement, "return"), statement, call?.order ?? null);
      connect(incoming, returned);
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
      return [{ id: broken.id, breakLoop: Boolean(loopNode && !switchNode), breakSwitch: Boolean(switchNode) }];
    }

    const statementCalls = topLevelCalls(statement);
    if (statementCalls.length > 0) {
      const callNode = addNode("call", compactStatement(statement, "호출"), statement, statementCalls[0].order);
      connect(incoming, callNode);
      return [{ id: callNode.id }];
    }
    if (["declaration", "expression_statement", "throw_statement"].includes(statement.type)) {
      const operation = addNode("operation", compactStatement(statement, "처리"), statement);
      connect(incoming, operation);
      return [{ id: operation.id }];
    }
    return processSequence(children(statement), incoming, loopNode, switchNode);
  }

  const pending = bodyNode ? processStatement(bodyNode, [{ id: entry.id }], null, null) : [{ id: entry.id }];
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
          startOffset: node.startIndex,
          endOffset: node.endIndex,
          filePath: filepath,
          projectPath,
          startLine: node.startPosition.row + 1,
          endLine: node.endPosition.row + 1,
          contentFingerprint: fingerprint(node.text),
          calls: [],
          bases,
          members: collectClassMembers(node),
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
          parameterTypes: parameters,
          signature: node.text.split("{")[0].replace(/\s+/g, " ").trim().slice(0, 240),
          startOffset: node.startIndex,
          endOffset: node.endIndex,
          filePath: filepath,
          projectPath,
          startLine: node.startPosition.row + 1,
          endLine: node.endPosition.row + 1,
          contentFingerprint: fingerprint(node.text),
          calls,
          bases: [],
          execution: executionFacts(node),
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
  for (const symbol of symbols.filter(symbol => symbol.kind === "method")) {
    const owners = symbols.filter(owner => ["class", "type"].includes(owner.kind) && owner.qualifiedName === ownerName(symbol.qualifiedName));
    const distinct = [...new Map(owners.map(owner => [owner.semanticKey, owner])).values()];
    if (distinct.length === 1) {
      symbol.ownerSemanticKey = distinct[0].semanticKey;
      symbol.ownerKind = distinct[0].kind;
    }
  }
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

      let typedOwner = call.receiver === "this" ? ownerName(source.qualifiedName) : call.receiverType;
      if (typedOwner && call.receiver !== "this") {
        const scopes = ownerName(source.qualifiedName).split("::");
        const owners = [];
        for (let count = scopes.length; count > 0; count--) owners.push([...scopes.slice(0, count), typedOwner].join("::"));
        owners.push(typedOwner);
        typedOwner = owners.find(owner => byQualified.has(`${owner}::${call.name}/${call.argumentCount}`)) ?? typedOwner;
      }
      const exactName = call.receiver ? (typedOwner ? `${typedOwner}::${call.name}` : "") : call.expression;
      const ownerCandidate = [ownerName(source.qualifiedName), call.name].filter(Boolean).join("::");
      let qualifiedCandidates = [
        ...(byQualified.get(`${exactName}/${call.argumentCount}`) ?? []),
        ...(call.receiver || call.expression.includes("::") || exactName === ownerCandidate ? [] : (byQualified.get(`${ownerCandidate}/${call.argumentCount}`) ?? [])),
      ].filter((candidate, index, all) => all.indexOf(candidate) === index && (!candidate.fileLocal || candidate.filePath === source.filePath));
      if (qualifiedCandidates.length > 1 && call.argumentTypes?.every(Boolean)) {
        const typed = qualifiedCandidates.filter(candidate => candidate.parameterTypes?.every((type, i) => canonicalType(type) === canonicalType(call.argumentTypes[i])));
        if (typed.length === 1) qualifiedCandidates = typed;
      }
      const candidates = (typedOwner ? qualifiedCandidates : bySimple.get(`${call.name}/${call.argumentCount}`) ?? [])
        .filter(candidate => !candidate.fileLocal || candidate.filePath === source.filePath);
      call.candidateSemanticKeys = candidates.map(candidate => candidate.semanticKey);
      if (call.calleeIsLocal) {
        call.resolutionReason = "localCallable";
        exclude(source, call, "localCallable", candidates.map(candidate => candidate.qualifiedName));
        continue;
      }
      const virtual = typedOwner && symbols.some(symbol => symbol.qualifiedName === typedOwner &&
        symbol.members?.some(member => member.name === call.name && /\bvirtual\b|\boverride\b/.test(member.signature ?? "")));
      call.resolutionReason = virtual ? "virtualDispatch" : call.receiver && !typedOwner ? "receiverTypeUnknown" :
        qualifiedCandidates.length > 1 ? "overloadAmbiguous" : candidates.length > 1 ? "multipleTargets" : "targetNotProven";
      let target = qualifiedCandidates.length === 1 ? qualifiedCandidates[0] : null;
      if (virtual) target = null;
      let confidence = "Exact";
      if (qualifiedCandidates.length > 1) {
        exclude(source, call, "multipleTargets", qualifiedCandidates.map((candidate) => candidate.qualifiedName));
      } else if (!target && !call.receiver) {
        if (candidates.length === 1) {
          [target] = candidates;
          confidence = "Inferred";
        } else if (candidates.length > 1) {
          exclude(source, call, "multipleTargets", candidates.map((candidate) => candidate.qualifiedName));
        }
      }
      if (!target) continue;
      call.resolvedSemanticKey = target.semanticKey;
      call.resolutionConfidence = confidence;
      call.resolutionReason = confidence === "Exact" ? "exactScopeAndType" : "nameOnly";
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
