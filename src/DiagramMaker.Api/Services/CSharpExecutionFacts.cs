using DiagramMaker.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiagramMaker.Services;

public static class CSharpExecutionFacts
{
    public static IReadOnlyList<ExecutionFact> Build(BaseMethodDeclarationSyntax method)
    {
        ExecutionFact Fact(SyntaxNode n, string kind, string? expression = null) => new(
            $"{kind}_{n.SpanStart}_{n.Span.End}", kind, expression ?? n.ToString(), n.SpanStart, n.Span.End, [], [], []);
        IReadOnlyList<ExecutionFact> Expression(SyntaxNode? n)
        {
            if (n is null or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) return [];
            if (n is BinaryExpressionSyntax binary && (binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression)))
                return [.. Expression(binary.Left), Fact(binary, "branch", binary.Left.ToString()) with
                { Children = binary.IsKind(SyntaxKind.LogicalAndExpression) ? Expression(binary.Right) : [],
                    Alternative = binary.IsKind(SyntaxKind.LogicalOrExpression) ? Expression(binary.Right) : [] }];
            if (n is ConditionalExpressionSyntax conditional)
                return [.. Expression(conditional.Condition), Fact(n, "branch", conditional.Condition.ToString()) with
                { Children = Expression(conditional.WhenTrue), Alternative = Expression(conditional.WhenFalse) }];
            if (n is InvocationExpressionSyntax invocation)
                return [.. Expression(invocation.Expression), .. invocation.ArgumentList.Arguments.SelectMany(a => Expression(a.Expression)),
                    Fact(n, "call") with { Value = n.Parent is ExpressionStatementSyntax ? "discarded" : "unknown" }];
            if (n is AssignmentExpressionSyntax assignment)
                return [.. Expression(assignment.Left), .. Expression(assignment.Right), Fact(n, "assign") with
                { Variable = assignment.Left is IdentifierNameSyntax ? assignment.Left.ToString() : null,
                    Value = assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? assignment.Right.ToString() : null }];
            if (n is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax &&
                (n.IsKind(SyntaxKind.PreIncrementExpression) || n.IsKind(SyntaxKind.PreDecrementExpression) ||
                 n.IsKind(SyntaxKind.PostIncrementExpression) || n.IsKind(SyntaxKind.PostDecrementExpression)))
                return [.. n.ChildNodes().SelectMany(Expression), Fact(n, "invalidate")];
            return n.ChildNodes().SelectMany(Expression).ToArray();
        }
        IReadOnlyList<ExecutionFact> Declaration(VariableDeclarationSyntax declaration) => declaration.Variables.SelectMany(v =>
            Expression(v.Initializer?.Value).Concat(v.Initializer?.Value is RefExpressionSyntax reference
                ? [Fact(reference, "invalidate") with { Variable = reference.Expression.ToString(), Value = "escaped" }] : [])
            .Append(Fact(v, "declare") with
            { Variable = v.Identifier.ValueText, Value = v.Initializer?.Value.ToString() })).ToArray();
        IReadOnlyList<ExecutionFact> Statement(StatementSyntax? n, string? loop = null)
        {
            if (n is null) return [];
            if (n is BlockSyntax block)
            {
                var result = new List<ExecutionFact>();
                foreach (var statement in block.Statements)
                {
                    result.AddRange(Statement(statement, loop));
                    if (statement is ReturnStatementSyntax or ThrowStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax) break;
                }
                return result;
            }
            if (n is IfStatementSyntax conditional) return [Fact(conditional.Condition, "branch") with
            { Id = $"branch_if_{conditional.SpanStart}_{conditional.Span.End}",
                Evaluation = Expression(conditional.Condition), Children = Statement(conditional.Statement, loop), Alternative = Statement(conditional.Else?.Statement, loop) }];
            if (n is ReturnStatementSyntax returned) return [.. Expression(returned.Expression), Fact(n, "return") with
            { Value = returned.Expression?.ToString() ?? "void", TerminationTarget = "function" }];
            if (n is ThrowStatementSyntax thrown) return [.. Expression(thrown.Expression), Fact(n, "throw") with
            { Value = thrown.Expression?.ToString(), TerminationTarget = "function" }];
            if (n is BreakStatementSyntax or ContinueStatementSyntax) return [Fact(n, n is BreakStatementSyntax ? "break" : "continue") with
            { TerminationTarget = loop ?? "unknown" }];
            if (n is LocalDeclarationStatementSyntax local) return Declaration(local.Declaration);
            if (n is ForEachStatementSyntax each)
            {
                var region = Fact(n, "loop", $"{each.Identifier.ValueText} in {each.Expression}");
                return [.. Expression(each.Expression), region with { Value = "foreach", Children = Statement(each.Statement, region.Id) }];
            }
            if (n is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax)
            {
                var region = Fact(n, "loop");
                var condition = n switch { WhileStatementSyntax w => w.Condition, DoStatementSyntax d => d.Condition, ForStatementSyntax f => f.Condition, _ => null };
                var body = n switch { WhileStatementSyntax w => w.Statement, DoStatementSyntax d => d.Statement, ForStatementSyntax f => f.Statement, _ => null };
                var initial = n is ForStatementSyntax f1 ? (f1.Declaration is null ? [] : Declaration(f1.Declaration)).Concat(f1.Initializers.SelectMany(Expression)) : [];
                return [.. initial, region with { Expression = condition?.ToString() ?? "true", Value = n is DoStatementSyntax ? "post-test" : "pre-test",
                    Evaluation = Expression(condition), Children = Statement(body, region.Id),
                    Alternative = n is ForStatementSyntax f2 ? f2.Incrementors.SelectMany(Expression).ToArray() : [] }];
            }
            if (n is SwitchStatementSyntax or TryStatementSyntax or GotoStatementSyntax or UsingStatementSyntax or LockStatementSyntax)
                return [Fact(n, "unsupported", $"{n.Kind()}: 제어 경로 미확인. 아래 관측 호출의 순서·실행 조건은 원문을 확인하세요.") with
                { Children = Expression(n) }];
            return Expression(n);
        }
        return method.Body is not null ? Statement(method.Body) : method.ExpressionBody is { } arrow
            ? [.. Expression(arrow.Expression), Fact(arrow.Expression, "return") with { Value = arrow.Expression.ToString(), TerminationTarget = "function" }] : [];
    }
}
