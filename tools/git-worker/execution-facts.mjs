// Syntax-owned execution regions. Offsets are UTF-16, just like the editor.
// These facts describe observed syntax; they never imply a resolved call target.
export const isCppCast = node => node?.type === "call_expression" &&
  node.childForFieldName("function")?.type === "template_function" &&
  /^(?:static_cast|dynamic_cast|reinterpret_cast|const_cast)\s*</.test(node.childForFieldName("function")?.text ?? "");

export function executionFacts(declaration) {
  const field = (n, key) => n?.childForFieldName(key);
  const children = n => n?.namedChildren.filter(c => c.type !== "comment") ?? [];
  const fact = (n, kind, expression = n.text, extra = {}) => ({
    id: `${kind}_${n.startIndex}_${n.endIndex}`, kind, expression,
    startOffset: n.startIndex, endOffset: n.endIndex,
    children: [], alternative: [], evaluation: [], ...extra,
  });
  const unwrap = n => ["condition_clause", "parenthesized_expression"].includes(n?.type) ? unwrap(children(n)[0]) : n;
  function expression(n) {
    if (!n || ["lambda_expression", "function_definition"].includes(n.type)) return [];
    const left = field(n, "left"), right = field(n, "right"), op = field(n, "operator")?.text;
    if (n.type === "binary_expression" && ["&&", "||"].includes(op))
      return [...expression(left), fact(n, "branch", left.text, {
        children: op === "&&" ? expression(right) : [], alternative: op === "||" ? expression(right) : [],
      })];
    if (n.type === "conditional_expression") return [...expression(field(n, "condition")), fact(n, "branch", field(n, "condition").text, {
      children: expression(field(n, "consequence")), alternative: expression(field(n, "alternative")),
    })];
    if (n.type === "call_expression") {
      if (isCppCast(n)) return children(field(n, "arguments")).flatMap(expression);
      const args = children(field(n, "arguments"));
      const evaluations = args.map(expression);
      const active = evaluations.map((events, index) => ({ events, node: args[index] })).filter(a => a.events.length);
      const before = expression(field(n, "function"));
      if (active.length > 1) before.push(fact(n, "unordered", "C++ 인자 사이의 평가 순서 미확정", {
        children: active.map(({ events, node }) => fact(node, "region", node.text, { children: events })),
      }));
      else before.push(...active.flatMap(a => a.events));
      return [...before, fact(n, "call", n.text, { value: n.parent?.type === "expression_statement" ? "discarded" : "unknown" }),
        ...args.filter(a => a.type === "identifier").map(a => fact(a, "invalidate", a.text, { variable: a.text, value: "escaped" }))];
    }
    if (n.type === "assignment_expression") return [...expression(left), ...expression(right), fact(n, "assign", n.text, {
      variable: left?.type === "identifier" ? left.text : null, value: op === "=" ? right?.text : null,
    })];
    if (n.type === "update_expression") return [fact(n, "invalidate", n.text)];
    return children(n).flatMap(expression);
  }
  function statement(n, loop = null) {
    if (!n) return [];
    if (n.type === "compound_statement") {
      const result = [];
      for (const child of children(n)) {
        result.push(...statement(child, loop));
        if (["return_statement", "throw_statement", "break_statement", "continue_statement"].includes(child.type)) break;
      }
      return result;
    }
    if (n.type === "else_clause") return statement(children(n)[0], loop);
    if (n.type === "if_statement") {
      const condition = unwrap(field(n, "condition"));
      return [fact(condition, "branch", condition.text, { evaluation: expression(condition),
        id: `branch_if_${n.startIndex}_${n.endIndex}`,
        children: statement(field(n, "consequence"), loop), alternative: statement(field(n, "alternative"), loop) })];
    }
    if (["return_statement", "throw_statement"].includes(n.type)) {
      const value = children(n)[0];
      return [...expression(value), fact(n, n.type === "return_statement" ? "return" : "throw", n.text,
        { value: value?.text ?? "void", terminationTarget: "function" })];
    }
    if (["break_statement", "continue_statement"].includes(n.type))
      return [fact(n, n.type === "break_statement" ? "break" : "continue", n.text, { terminationTarget: loop ?? "unknown" })];
    if (["while_statement", "do_statement", "for_statement"].includes(n.type)) {
      const id = `loop_${n.startIndex}_${n.endIndex}`;
      const condition = unwrap(field(n, "condition"));
      return [...statement(field(n, "initializer"), loop), fact(n, "loop", condition?.text ?? "true", {
        id, value: n.type === "do_statement" ? "post-test" : "pre-test", evaluation: expression(condition),
        children: statement(field(n, "body"), id), alternative: expression(field(n, "update")),
      })];
    }
    if (n.type === "declaration") return children(n).flatMap(d => d.type === "init_declarator"
      ? [...expression(field(d, "value")), fact(d, "declare", d.text, {
        variable: field(d, "declarator")?.type === "identifier" ? field(d, "declarator").text : null,
        value: field(d, "value")?.text ?? null,
      })] : []);
    if (["switch_statement", "try_statement", "for_range_loop", "goto_statement"].includes(n.type))
      return [fact(n, "unsupported", `${n.type}: 제어 경로 미확인. 아래 관측 호출의 순서·실행 조건은 원문을 확인하세요.`, { children: expression(n) })];
    return expression(n);
  }
  return declaration?.type === "function_definition" ? statement(field(declaration, "body")) : [];
}
