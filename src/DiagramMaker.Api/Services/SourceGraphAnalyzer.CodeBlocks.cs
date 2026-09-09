using DiagramMaker.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DiagramMaker.Services;

public sealed partial class SourceGraphAnalyzer
{
    private sealed record SnippetTree(CodeBlockInput Block, SyntaxTree Tree, string Prefix, bool Fragment, string WrapperName);
    private sealed record SnippetSymbol(SnippetTree Input, ParsedSymbol Parsed, string Id, CodeBlockSourceMap Map,
        CodeBlockLocation Location, int? Arity);

    public CodeBlockGraph AnalyzeCSharpCodeBlocks(Guid workspaceId, IReadOnlyList<CodeBlockInput> blocks)
    {
        var inputs = blocks.Select(PrepareCSharpSnippet).ToArray();
        var compilation = CSharpCompilation.Create("CodeBlockEvidence", inputs.Select(i => i.Tree),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var parsed = new List<SnippetSymbol>();
        var warnings = new List<string>();
        foreach (var input in inputs)
        {
            if (input.Tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                warnings.Add($"{input.Block.Title}: 잘린 구문 또는 문법 오류가 있어 복구 가능한 부분만 분석합니다.");
            if (input.Fragment) warnings.Add($"{input.Block.Title}: 함수 내부 코드 조각입니다. 바깥 선언과 실행 문맥은 미확인입니다.");
            var map = new CodeBlockSourceMap(input.Block, input.Prefix.Length);
            // Reuse syntax/CFG extraction directly. These legacy parser identifiers are local snapshot keys;
            // no repository, GitComparison, before/after change or Git evidence is created for snippets.
            foreach (var symbol in ParseCSharp(workspaceId, map.ContentHash, map.ContentHash, input.Tree.FilePath,
                input.Tree.GetText().ToString(), compilation.GetSemanticModel(input.Tree)))
            {
                if (symbol.Identity.Kind == "file" || symbol.Version.QualifiedName == input.WrapperName) continue;
                var location = map.Location(symbol.Evidence.StartOffset ?? 0, symbol.Evidence.EndOffset ?? input.Block.Code.Length + input.Prefix.Length);
                var declaration = input.Tree.GetRoot().FindNode(TextSpan.FromBounds(symbol.Evidence.StartOffset ?? 0,
                    symbol.Evidence.EndOffset ?? input.Tree.Length), getInnermostNodeForTie: true);
                var arity = declaration is BaseMethodDeclarationSyntax method ? method.ParameterList.Parameters.Count : (int?)null;
                parsed.Add(new SnippetSymbol(input, symbol, StableIds.Create("code-symbol", input.Block.Id, map.ContentHash,
                    symbol.Identity.SemanticKey, location.StartOffset), map, location, arity));
            }
            if (parsed.All(p => p.Input != input) && !string.IsNullOrWhiteSpace(input.Block.Code))
                warnings.Add($"{input.Block.Title}: 분석할 선언이나 처리 구문을 찾지 못했습니다. 함수 또는 실제 처리 구문을 포함해 주세요.");
        }
        var evidence = new Dictionary<string, CodeBlockEvidence>();
        string AddEvidence(SnippetSymbol p, CodeBlockLocation location)
        {
            var item = p.Map.Evidence(location); evidence[item.Id] = item; return item.Id;
        }
        var symbols = new List<CodeBlockSymbol>();
        var transitions = new List<CodeBlockStateTransition>();
        foreach (var p in parsed)
        {
            var original = p.Parsed;
            var syntheticMethod = p.Input.Fragment && original.Version.QualifiedName.EndsWith(".__Fragment__", StringComparison.Ordinal);
            var name = syntheticMethod ? p.Input.Block.Title : original.Version.QualifiedName.Replace(p.Input.WrapperName + ".", "", StringComparison.Ordinal);
            var owner = parsed.FirstOrDefault(other => other.Input == p.Input && other.Parsed.Identity.Id == original.Version.OwnerIdentityId);
            var stepIds = (original.ControlFlow?.Nodes ?? []).ToDictionary(n => n.Id, n => StableIds.Create(p.Id, n.Id));
            var steps = (original.ControlFlow?.Nodes ?? []).Select(n =>
            {
                var loc = n.Context?.Span is { StartOffset: { } start, EndOffset: { } end } ? p.Map.Location(start, end) : p.Location;
                var context = n.Context;
                var label = n.Kind is "operation" or "call" && context is not null ? DiagramCodeLabels.Action(context) : n.Label;
                var scopes = NormalizeScopes(context?.ControlPath ?? [], p.Id);
                return new CodeBlockStep(stepIds[n.Id], n.Kind, label,
                    n.Kind is "condition" or "loop" ? p.Input.Block.Code[loc.StartOffset..loc.EndOffset] :
                        context?.Statement ?? p.Input.Block.Code[loc.StartOffset..loc.EndOffset], loc, [AddEvidence(p, loc)], scopes,
                    context?.Target, context?.Receiver, context?.Arguments, context?.AssignedTo,
                    context?.Definitions?.Select(d => d.Statement).ToArray(), context?.Purpose ?? "operation");
            }).ToArray();
            var calls = (original.CallSites ?? []).Select(call =>
            {
                var loc = p.Map.Location(call.Evidence.StartOffset!.Value, call.Evidence.EndOffset!.Value);
                AddEvidence(p, loc);
                var exact = call.TargetKey is null ? [] : parsed.Where(target => target.Parsed.Identity.SemanticKey == call.TargetKey).ToArray();
                var candidates = exact.Length > 0 ? exact : parsed.Where(target => target.Arity == (call.Context?.Arguments.Count ?? 0) &&
                    target.Parsed.Version.QualifiedName.Split('.').Last() == call.Name).ToArray();
                // A successful semantic binding is necessary for an automatic cross-block C# call.
                var targetId = exact.Length == 1 ? exact[0].Id : null;
                return new CodeBlockCall(StableIds.Create(p.Id, "call", loc.StartOffset, loc.EndOffset), p.Id, call.Name,
                    call.Context?.Arguments.Count ?? 0, loc, NormalizeScopes(call.ControlPath ?? [], p.Id), call.Order,
                    targetId, candidates.Select(t => t.Id).Distinct().ToArray(), call.Context?.Receiver,
                    p.Input.Block.Code[loc.StartOffset..loc.EndOffset]);
            }).ToArray();
            if (original.Identity.Kind is "method" or "constructor" && original.ControlFlow is null)
                warnings.Add($"{p.Input.Block.Title} / {name}: 본문이 없거나 지원하지 않는 제어 구문이 있어 Flow 일부를 제공할 수 없습니다.");
            var members = (original.Version.Members ?? []).Select(m => m with
            { StartLine = Math.Max(1, m.StartLine - p.Input.Prefix.Count(c => c == '\n')),
                EndLine = Math.Max(1, m.EndLine - p.Input.Prefix.Count(c => c == '\n')) }).ToArray();
            symbols.Add(new CodeBlockSymbol(p.Id, p.Input.Block.Id, name, syntheticMethod ? "fragment" : original.Identity.Kind.ToLowerInvariant(),
                syntheticMethod ? "코드 조각" : original.Version.Signature, p.Location, [AddEvidence(p, p.Location)], steps,
                (original.ControlFlow?.Edges ?? []).Select(e => e with { SourceId = stepIds[e.SourceId], TargetId = stepIds[e.TargetId] }).ToArray(),
                calls, members, original.BaseTypeNames, owner?.Id, syntheticMethod, p.Arity));
            if (!p.Input.Tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                transitions.AddRange(CSharpSnippetTransitions(p, compilation.GetSemanticModel(p.Input.Tree), loc => AddEvidence(p, loc)));
        }
        return new CodeBlockGraph(symbols, [], evidence.Values.ToArray(), transitions, warnings.Distinct().ToArray(), CodeBlockAnalyzer.AnalyzerVersion);
    }

    private static IReadOnlyList<ControlScope> NormalizeScopes(IReadOnlyList<ControlScope> scopes, string symbolId) =>
        scopes.Select(s => s with { Id = StableIds.Create(symbolId, s.Id) }).ToArray();

    private static SnippetTree PrepareCSharpSnippet(CodeBlockInput block)
    {
        var wrapper = "__CodeBlock_" + StableIds.Create(block.Id).Replace('-', '_');
        var raw = CSharpSyntaxTree.ParseText(block.Code, path: block.Id + ".cs");
        if (raw.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any()) return new(block, raw, "", false, wrapper);
        var member = SyntaxFactory.ParseMemberDeclaration(block.Code);
        var isMember = member is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax;
        var prefix = $"class {wrapper}\n{{\n" + (isMember ? "" : "void __Fragment__()\n{\n");
        var suffix = isMember ? "\n}" : "\n}\n}";
        return new(block, CSharpSyntaxTree.ParseText(prefix + block.Code + suffix, path: block.Id + ".cs"), prefix, !isMember, wrapper);
    }

    private static IEnumerable<CodeBlockStateTransition> CSharpSnippetTransitions(SnippetSymbol p, SemanticModel model, Func<CodeBlockLocation, string> evidence)
    {
        var start = p.Parsed.Evidence.StartOffset ?? 0;
        var end = p.Parsed.Evidence.EndOffset ?? p.Input.Tree.Length;
        var declaration = p.Input.Tree.GetRoot().FindNode(TextSpan.FromBounds(start, end), getInnermostNodeForTie: true);
        // Methods own their transitions; do not duplicate a method transition on its containing class.
        if (declaration is not BaseMethodDeclarationSyntax method) yield break;
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression) && !IsUnreachable(a, method)))
        {
            if (assignment.Ancestors().TakeWhile(a => a != method).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) continue;
            var variable = model.GetSymbolInfo(assignment.Left).Symbol;
            var value = model.GetSymbolInfo(assignment.Right).Symbol;
            var enumValue = value is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum };
            var stateName = new[] { "state", "status", "phase", "mode" }.Any(n => assignment.Left.ToString().Contains(n, StringComparison.OrdinalIgnoreCase));
            if (!enumValue && !(stateName && assignment.Right is LiteralExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax)) continue;
            bool Same(ExpressionSyntax expression) => variable is not null
                ? SymbolEqualityComparer.Default.Equals(variable, model.GetSymbolInfo(expression).Symbol)
                : expression.ToString() == assignment.Left.ToString();
            string? from = null;
            SyntaxNode? origin = null;
            foreach (var ancestor in assignment.Ancestors().TakeWhile(a => a != method))
            {
                if (ancestor is IfStatementSyntax conditional && conditional.Statement.Span.Contains(assignment.Span) &&
                    !conditional.Condition.DescendantNodesAndSelf().Any(n => n.IsKind(SyntaxKind.LogicalOrExpression)))
                {
                    var test = GuardEquality(conditional.Condition);
                    if (test is not null) { from = (Same(test.Left) ? test.Right : test.Left).ToString(); origin = test; break; }
                }
                if (ancestor is SwitchSectionSyntax section && section.Parent is SwitchStatementSyntax selection && Same(selection.Expression) &&
                    section.Labels.Count == 1 && section.Labels[0] is CaseSwitchLabelSyntax label)
                { from = label.Value.ToString(); origin = label; break; }
            }
            if (from is null || origin is null) continue;
            var path = CSharpControlPath(assignment, method);
            // Only writes after the selected state guard can invalidate it. Writes
            // in a different case/branch are not on this execution path.
            if (method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.SpanStart > origin.SpanStart && a.SpanStart < assignment.SpanStart &&
                Same(a.Left) && CSharpControlPath(a, method).All(scope => path.Any(p => p.Id == scope.Id && p.Branch == scope.Branch)))) continue;
            var loc = p.Map.Location(assignment.SpanStart, assignment.Span.End);
            var guards = assignment.Ancestors().OfType<IfStatementSyntax>().Where(i => method.Span.Contains(i.Span))
                .Reverse().Select(i => i.Statement.Span.Contains(assignment.Span) ? i.Condition.ToString() : $"!({i.Condition})");
            yield return new CodeBlockStateTransition(StableIds.Create(p.Id, "state", loc.StartOffset), p.Id, assignment.Left.ToString(),
                from, assignment.Right.ToString(), string.Join(" && ", guards),
                [evidence(loc), evidence(p.Map.Location(origin.SpanStart, origin.Span.End))]);

            BinaryExpressionSyntax? GuardEquality(ExpressionSyntax expression) => expression switch
            {
                ParenthesizedExpressionSyntax parenthesis => GuardEquality(parenthesis.Expression),
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) => GuardEquality(binary.Left) ?? GuardEquality(binary.Right),
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) && (Same(binary.Left) || Same(binary.Right)) => binary,
                _ => null
            };
        }
    }
}
