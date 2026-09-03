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

function collectCalls(node) {
  const calls = [];
  let order = 0;
  function visit(current) {
    if (current !== node && current.type === "function_definition") return;
    if (current.type === "call_expression") {
      const expression = cleanName(field(current, "function")?.text ?? "");
      if (expression) {
        calls.push({
          expression,
          name: lastName(expression),
          argumentCount: countArguments(current),
          line: current.startPosition.row + 1,
          order: ++order,
        });
      }
    }
    for (const child of children(current)) visit(child);
  }
  visit(node);
  return calls;
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
          calls: collectCalls(node),
          bases: [],
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
  return { filepath, projectPath, includes, symbols, definitions, diagnostics };
}

function ownerName(qualifiedName) {
  const parts = qualifiedName.split("::");
  return parts.length > 1 ? parts.slice(0, -1).join("::") : "";
}

export function resolveCppCalls(files) {
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

  const edges = [];
  let ambiguousCallCount = 0;
  for (const source of symbols.filter((symbol) => symbol.calls.length > 0)) {
    for (const call of source.calls) {
      const exactName = call.expression.replaceAll(".", "::").replaceAll("->", "::");
      const ownerCandidate = [ownerName(source.qualifiedName), call.name].filter(Boolean).join("::");
      const qualifiedCandidates = [
        ...(byQualified.get(`${exactName}/${call.argumentCount}`) ?? []),
        ...(exactName === ownerCandidate ? [] : (byQualified.get(`${ownerCandidate}/${call.argumentCount}`) ?? [])),
      ].filter((candidate, index, all) => all.indexOf(candidate) === index);
      let target = qualifiedCandidates.length === 1 ? qualifiedCandidates[0] : null;
      let confidence = "Exact";
      if (qualifiedCandidates.length > 1) {
        ambiguousCallCount += 1;
      } else if (!target) {
        const candidates = bySimple.get(`${call.name}/${call.argumentCount}`) ?? [];
        if (candidates.length === 1) {
          [target] = candidates;
          confidence = "Inferred";
        } else if (candidates.length > 1) {
          ambiguousCallCount += 1;
        }
      }
      if (!target || target.semanticKey === source.semanticKey) continue;
      edges.push({
        sourceSemanticKey: source.semanticKey,
        targetSemanticKey: target.semanticKey,
        type: "calls",
        label: "calls",
        confidence,
        filePath: source.filePath,
        line: call.line,
        sequenceIndex: call.order,
      });
    }
  }

  return {
    files,
    symbols,
    edges: edges.filter((edge, index, all) => all.findIndex((candidate) =>
      candidate.sourceSemanticKey === edge.sourceSemanticKey
      && candidate.targetSemanticKey === edge.targetSemanticKey
      && candidate.type === edge.type) === index),
    diagnostics: files.flatMap((file) => file.diagnostics),
    ambiguousCallCount,
  };
}
