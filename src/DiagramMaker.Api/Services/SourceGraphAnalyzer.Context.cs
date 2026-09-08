using DiagramMaker.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiagramMaker.Services;

public sealed partial class SourceGraphAnalyzer
{
    private static CodeContext DescribeCode(SyntaxNode syntax, SyntaxNode declaration,
        string revision, string blob, string path, InvocationExpressionSyntax? call = null)
    {
        // Pick the outer action, not a constructor or call inside its arguments.
        var invocation = call ?? syntax.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(value => !value.Ancestors().TakeWhile(parent => parent != syntax)
                .Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));
        var statement = syntax.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault() ?? syntax;
        var variable = statement.DescendantNodesAndSelf().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(value => invocation is null || value.Span.Contains(invocation.Span));
        var assignment = (invocation ?? syntax).AncestorsAndSelf().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(value => statement.Span.Contains(value.Span));
        var creation = (variable?.Initializer?.Value ?? assignment?.Right ?? syntax) as BaseObjectCreationExpressionSyntax;
        var member = invocation?.Expression as MemberAccessExpressionSyntax;
        var target = member?.Name.Identifier.ValueText ?? (invocation?.Expression as SimpleNameSyntax)?.Identifier.ValueText;
        var receiver = member?.Expression.ToString();
        var purpose = target is null ? "prepare" : receiver?.Split('.').Last() is "Assert" or "CollectionAssert" ? "assertion" :
            (receiver is "Path" or "System.IO.Path" && target == "Combine") ||
            (receiver is "Directory" or "System.IO.Directory" && target == "CreateDirectory") ||
            target == "Add" && invocation!.ArgumentList.Arguments.Any(value => value.Expression is BaseObjectCreationExpressionSyntax)
                ? "prepare" : "call";
        var span = syntax.SyntaxTree.GetLineSpan((call ?? syntax).Span);
        return new CodeContext(statement.ToString(), target, receiver,
            invocation?.ArgumentList.Arguments.Select(value => value.ToString()).ToArray() ?? [],
            variable?.Identifier.ValueText ?? assignment?.Left.ToString(),
            creation is ObjectCreationExpressionSyntax typed ? typed.Type.ToString() : creation is not null ? "new" : null,
            statement.DescendantNodesAndSelf().OfType<InitializerExpressionSyntax>().Select(value => value.ToString()).ToArray(),
            CSharpControlPath(call ?? syntax, declaration), new SourceSpan(revision, blob, path,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, (call ?? syntax).SpanStart, (call ?? syntax).Span.End), purpose,
            DefinitionsFor(statement, declaration, revision, blob, path));
    }

    private static IReadOnlyList<CodeDefinition> DefinitionsFor(SyntaxNode statement, SyntaxNode declaration,
        string revision, string blob, string path)
    {
        var identifiers = statement.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(value => value.Identifier.ValueText).ToHashSet();
        var owner = declaration.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var declarations = declaration.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(value => value.Span.End < statement.SpanStart &&
                !value.Ancestors().TakeWhile(parent => parent != declaration).Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) &&
                value.Ancestors().OfType<BlockSyntax>().FirstOrDefault()?.Span.Contains(statement.Span) == true)
            .Concat(owner?.Members.OfType<FieldDeclarationSyntax>().SelectMany(field => field.Declaration.Variables) ?? [])
            .ToArray();
        var result = new Dictionary<int, CodeDefinition>();
        for (var pass = 0; pass <= declarations.Length; pass++)
        {
            var added = false;
            foreach (var value in declarations.Where(value => identifiers.Contains(value.Identifier.ValueText) && !result.ContainsKey(value.SpanStart)))
            {
                var definition = value.Ancestors().FirstOrDefault(parent => parent is LocalDeclarationStatementSyntax or FieldDeclarationSyntax) ?? value;
                var span = definition.SyntaxTree.GetLineSpan(definition.Span);
                result[value.SpanStart] = new CodeDefinition(value.Identifier.ValueText, definition.ToString(),
                    new SourceSpan(revision, blob, path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
                        definition.SpanStart, definition.Span.End));
                identifiers.UnionWith(definition.DescendantNodes().OfType<IdentifierNameSyntax>().Select(name => name.Identifier.ValueText));
                added = true;
            }
            if (!added) break;
        }
        return result.Values.OrderBy(value => value.Span.StartOffset).ToArray();
    }

    // Only definite transfers count. A return inside a nested loop/lambda or one
    // branch of a nested conditional does not prove that the enclosing block exits.
    private static bool DefinitelyTransfers(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax => true,
        BlockSyntax block => block.Statements.Any(DefinitelyTransfers),
        IfStatementSyntax conditional => conditional.Else is not null &&
            DefinitelyTransfers(conditional.Statement) && DefinitelyTransfers(conditional.Else.Statement),
        _ => false
    };

    private static IEnumerable<StatementSyntax> PrecedingStatements(SyntaxNode node, BlockSyntax block) =>
        block.Statements.TakeWhile(statement => !statement.Span.Contains(node.Span));

    private static bool IsUnreachable(SyntaxNode node, SyntaxNode declaration) => node.Ancestors()
        .TakeWhile(parent => parent != declaration).OfType<BlockSyntax>()
        .Any(block => PrecedingStatements(node, block).Any(DefinitelyTransfers));
}
