using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiagramMaker.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DiagramMaker.Services;

public sealed partial class SourceGraphAnalyzer
{
    public const string IndexVersion = "source-graph-v8";

    private sealed record ParsedCall(string Name, int StartLine, int EndLine, EvidenceRef Evidence,
        int Order = 0, IReadOnlyList<ControlScope>? ControlPath = null, string? TargetKey = null, bool BindingAttempted = false,
        CodeContext? Context = null);
    private sealed record ControlEndpoint(string Id, string Label = "", bool Break = false);

    private sealed record ParsedSymbol(
        SymbolIdentity Identity,
        SymbolVersion Version,
        EvidenceRef Evidence,
        string Body,
        IReadOnlyList<string> CalledNames,
        IReadOnlyList<string> BaseTypeNames,
        IReadOnlyList<ParsedCall>? CallSites = null,
        MethodControlFlow? ControlFlow = null);

    public VersionedGraph Analyze(Guid repositoryId, GitComparison comparison) => Analyze(repositoryId, comparison, null);

    public VersionedGraph Analyze(Guid repositoryId, GitComparison comparison, CppSourceIndex? cppIndex)
    {
        var before = new List<ParsedSymbol>();
        var after = new List<ParsedSymbol>();
        var hasCppIndex = cppIndex is { TargetSymbols.Count: > 0 };
        var semanticModels = BuildSemanticModels(comparison);

        foreach (var file in comparison.Files)
        {
            var beforePath = file.PreviousPath ?? file.Path;
            if (file.BeforeContent is not null && file.BeforeBlobOid is not null && !(hasCppIndex && IsCppPath(beforePath)))
            {
                before.AddRange(Parse(repositoryId, comparison.BaseSha, file.BeforeBlobOid, beforePath, file.BeforeContent,
                    semanticModels.GetValueOrDefault((comparison.BaseSha, beforePath))));
            }

            if (file.AfterContent is not null && file.AfterBlobOid is not null && !(hasCppIndex && IsCppPath(file.Path)))
            {
                after.AddRange(Parse(repositoryId, comparison.TargetSha, file.AfterBlobOid, file.Path, file.AfterContent,
                    semanticModels.GetValueOrDefault((comparison.TargetSha, file.Path))));
            }
        }

        foreach (var file in comparison.ContextFiles ?? [])
        {
            if (hasCppIndex && IsCppPath(file.Path)) continue;
            var parsed = Parse(repositoryId, file.RevisionSha, file.BlobOid, file.Path, file.Content,
                semanticModels.GetValueOrDefault((file.RevisionSha, file.Path)));
            if (file.RevisionSha == comparison.BaseSha) before.AddRange(parsed);
            if (file.RevisionSha == comparison.TargetSha) after.AddRange(parsed);
        }

        var beforeByIdentity = before.GroupBy(static symbol => symbol.Identity.Id).ToDictionary(static group => group.Key, static group => group.First());
        var afterByIdentity = after.GroupBy(static symbol => symbol.Identity.Id).ToDictionary(static group => group.Key, static group => group.First());
        var changes = BuildChanges(beforeByIdentity, afterByIdentity).ToList();
        var edges = BuildEdges(after).Concat(BuildEdges(before)).ToList();
        var allSymbols = before.Concat(after).ToList();
        var controlFlows = allSymbols.Select(static symbol => symbol.ControlFlow)
            .Where(static flow => flow is not null).Cast<MethodControlFlow>().ToList();

        if (hasCppIndex)
        {
            var cppBefore = cppIndex!.BeforeChangedSymbols
                .Select(fact => CreateCppParsedSymbol(repositoryId, comparison.BaseSha, fact, comparison))
                .ToArray();
            var cppTarget = cppIndex.TargetSymbols
                .Select(fact => CreateCppParsedSymbol(repositoryId, comparison.TargetSha, fact, comparison))
                .ToArray();
            var changedTargetPaths = comparison.Files.Select(static file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changedCppTarget = cppTarget.Where(symbol => changedTargetPaths.Contains(symbol.Version.FilePath)).ToArray();
            var cppBeforeByIdentity = cppBefore.GroupBy(static symbol => symbol.Identity.Id).ToDictionary(static group => group.Key, static group => group.First());
            var cppTargetByIdentity = changedCppTarget.GroupBy(static symbol => symbol.Identity.Id).ToDictionary(static group => group.Key, static group => group.First());
            changes.AddRange(BuildChanges(cppBeforeByIdentity, cppTargetByIdentity));
            var cppSymbols = cppTarget.Concat(cppBefore).ToArray();
            edges.AddRange(BuildCppEdges(repositoryId, cppIndex.TargetEdges, cppIndex.TargetSymbols, cppSymbols,
                comparison.TargetSha, includeInheritance: true));
            edges.AddRange(BuildCppEdges(repositoryId, cppIndex.BaseEdges ?? [], [], cppSymbols,
                comparison.BaseSha, includeInheritance: false));
            controlFlows.AddRange(BuildCppControlFlows(cppIndex.TargetSymbols, cppSymbols, comparison.TargetSha));
            controlFlows.AddRange(BuildCppControlFlows(cppIndex.BeforeChangedSymbols, cppSymbols, comparison.BaseSha));
            allSymbols.AddRange(cppBefore);
            allSymbols.AddRange(cppTarget);
        }

        edges.AddRange(BuildMemberEdges(allSymbols));

        return new VersionedGraph(
            allSymbols.Select(static symbol => symbol.Identity).DistinctBy(static identity => identity.Id).ToArray(),
            allSymbols.Select(static symbol => symbol.Version).DistinctBy(static version => version.Id).ToArray(),
            edges.DistinctBy(static edge => edge.Id).ToArray(),
            allSymbols.Select(static symbol => symbol.Evidence)
                .Concat(allSymbols.SelectMany(static symbol => symbol.CallSites ?? []).Select(static call => call.Evidence))
                .Concat(controlFlows.SelectMany(flow => flow.Nodes).Where(node => node.Context is not null).Select(node =>
                    new EvidenceRef(node.EvidenceIds[0], node.Context!.Span.RevisionSha, node.Context.Span.BlobOid,
                        node.Context.Span.FilePath, node.Context.Span.StartLine, node.Context.Span.EndLine,
                        "RoslynStatement", Confidence.Exact, node.Context.Span.StartOffset, node.Context.Span.EndOffset)))
                .DistinctBy(static evidence => evidence.Id).ToArray(),
            changes.DistinctBy(static change => change.Id).ToArray(),
            controlFlows);
    }

    private static bool IsCppPath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp";

    private static Dictionary<(string Revision, string Path), SemanticModel> BuildSemanticModels(GitComparison comparison)
    {
        var snapshots = (comparison.ContextFiles ?? []).Concat(comparison.Files.SelectMany(file =>
            new[] {
                file.BeforeContent is not null ? new RepositoryFileSnapshot(file.PreviousPath ?? file.Path, comparison.BaseSha, file.BeforeBlobOid ?? "", file.BeforeContent) : null,
                file.AfterContent is not null ? new RepositoryFileSnapshot(file.Path, comparison.TargetSha, file.AfterBlobOid ?? "", file.AfterContent) : null
            }.OfType<RepositoryFileSnapshot>()));
        var result = new Dictionary<(string, string), SemanticModel>();
        foreach (var revision in snapshots.Where(file => Path.GetExtension(file.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .GroupBy(file => file.RevisionSha))
        {
            var trees = revision.DistinctBy(file => file.Path).Select(file => CSharpSyntaxTree.ParseText(file.Content, path: file.Path)).ToArray();
            var compilation = CSharpCompilation.Create("DiagramEvidence", trees,
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            foreach (var tree in trees) result[(revision.Key, tree.FilePath)] = compilation.GetSemanticModel(tree);
        }
        return result;
    }

    private static string MethodSymbolKey(IMethodSymbol method)
    {
        method = (method.ReducedFrom ?? method).OriginalDefinition;
        var kind = method.MethodKind == MethodKind.Constructor ? "ctor" : "method";
        return $"{kind}:{method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}";
    }

    private static string MethodKey(BaseMethodDeclarationSyntax declaration, SemanticModel? model) =>
        model?.GetDeclaredSymbol(declaration) is IMethodSymbol method ? MethodSymbolKey(method) :
        $"method:{declaration.SyntaxTree.FilePath}:{DeclarationSignature(declaration)}";

    private static IReadOnlyList<ControlScope> CSharpControlPath(SyntaxNode node, SyntaxNode declaration)
    {
        var scopes = new List<ControlScope>();
        foreach (var ancestor in node.Ancestors().TakeWhile(item => item != declaration).Reverse())
        {
            var id = StableIds.Create(ancestor.SyntaxTree.FilePath, ancestor.SpanStart, ancestor.Span.End);
            if (ancestor is BlockSyntax block)
            {
                foreach (var guard in PrecedingStatements(node, block).OfType<IfStatementSyntax>())
                {
                    var thenExits = DefinitelyTransfers(guard.Statement);
                    var elseExits = guard.Else is not null && DefinitelyTransfers(guard.Else.Statement);
                    if (thenExits == elseExits) continue;
                    scopes.Add(new ControlScope(StableIds.Create(guard.SyntaxTree.FilePath, guard.SpanStart, guard.Span.End),
                        "alt", CompactCode(guard.Condition.ToString()), thenExits ? "else" : "then"));
                }
            }
            if (ancestor is IfStatementSyntax conditional && !conditional.Condition.Span.Contains(node.Span))
                scopes.Add(new ControlScope(id, "alt", CompactCode(conditional.Condition.ToString()),
                    conditional.Else?.Span.Contains(node.Span) == true ? "else" : "then"));
            else if (ancestor is SwitchSectionSyntax section)
                scopes.Add(new ControlScope(StableIds.Create(section.Parent!.SpanStart, "switch"), "alt",
                    CompactCode(((SwitchStatementSyntax)section.Parent).Expression.ToString()),
                    string.Join(", ", section.Labels.Select(label => label.ToString().TrimEnd(':')))));
            else if (ancestor is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax)
                scopes.Add(new ControlScope(id, "loop", ancestor switch
                {
                    ForStatementSyntax item => item.Condition?.ToString() ?? "반복",
                    ForEachStatementSyntax item => $"{item.Identifier} in {item.Expression}",
                    WhileStatementSyntax item => item.Condition.ToString(),
                    DoStatementSyntax item => $"후행 조건: {item.Condition}",
                    _ => "반복"
                }, "body"));
        }
        return scopes;
    }

    private static ParsedSymbol CreateCppParsedSymbol(
        Guid repositoryId,
        string revisionSha,
        CppSymbolFact fact,
        GitComparison comparison)
    {
        var identityId = StableIds.Create(repositoryId, "cpp", fact.Kind, fact.SemanticKey);
        var versionId = StableIds.Create(identityId, revisionSha, fact.ContentFingerprint);
        var blobOid = FindBlobOid(comparison, revisionSha, fact.FilePath) ?? StableIds.Create(revisionSha, fact.FilePath);
        var evidenceId = StableIds.Create(revisionSha, blobOid, fact.FilePath, fact.StartLine, fact.EndLine, "cpp-tree-sitter");
        return new ParsedSymbol(
            new SymbolIdentity(identityId, repositoryId, "cpp", fact.Kind, fact.SemanticKey),
            new SymbolVersion(versionId, identityId, revisionSha, fact.QualifiedName, fact.Signature, fact.FilePath,
                fact.StartLine, fact.EndLine, fact.ContentFingerprint, fact.Members,
                fact.OwnerSemanticKey is null ? null : StableIds.Create(repositoryId, "cpp", fact.OwnerKind ?? "class", fact.OwnerSemanticKey)),
            new EvidenceRef(evidenceId, revisionSha, blobOid, fact.FilePath, fact.StartLine, fact.EndLine,
                "TreeSitterCpp", Confidence.Exact),
            fact.ContentFingerprint,
            [],
            fact.Bases,
            fact.Calls.Select(call => new ParsedCall(call.Name, call.Line, call.EndLine ?? call.Line,
                new EvidenceRef(StableIds.Create(revisionSha, blobOid, fact.FilePath, fact.SemanticKey, "cpp-call", call.Order,
                    call.StartOffset, call.EndOffset), revisionSha, blobOid, fact.FilePath, call.Line, call.EndLine ?? call.Line,
                    "TreeSitterCppCall", Confidence.Exact, call.StartOffset, call.EndOffset), call.Order, call.ControlPath)).ToArray());
    }

    private static string? FindBlobOid(GitComparison comparison, string revisionSha, string filePath)
    {
        var changed = comparison.Files.FirstOrDefault(file =>
            revisionSha == comparison.TargetSha
                ? file.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase)
                : (file.PreviousPath ?? file.Path).Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (changed is not null) return revisionSha == comparison.TargetSha ? changed.AfterBlobOid : changed.BeforeBlobOid;
        return comparison.ContextFiles?.FirstOrDefault(file =>
            file.RevisionSha == revisionSha && file.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase))?.BlobOid;
    }

    private static IReadOnlyList<GraphEdge> BuildCppEdges(
        Guid repositoryId,
        IReadOnlyList<CppEdgeFact> edgeFacts,
        IReadOnlyList<CppSymbolFact> typeSourceFacts,
        IReadOnlyList<ParsedSymbol> symbols,
        string revisionSha,
        bool includeInheritance)
    {
        var identityBySemanticKey = symbols
            .GroupBy(static symbol => symbol.Identity.SemanticKey)
            .ToDictionary(static group => group.Key, static group => group.First().Identity.Id, StringComparer.Ordinal);
        var evidenceByIdentity = symbols
            .GroupBy(static symbol => symbol.Identity.Id, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.OrderByDescending(symbol => symbol.Version.RevisionSha == revisionSha)
                    .Select(static symbol => symbol.Evidence.Id).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        foreach (var edge in edgeFacts)
        {
            if (!identityBySemanticKey.TryGetValue(edge.SourceSemanticKey, out var sourceId) ||
                !identityBySemanticKey.TryGetValue(edge.TargetSemanticKey, out var targetId)) continue;
            var callEvidence = symbols.Where(symbol => symbol.Identity.Id == sourceId && symbol.Version.RevisionSha == revisionSha)
                .SelectMany(symbol => symbol.CallSites ?? []).FirstOrDefault(call => call.Order == edge.SequenceIndex && call.StartLine == edge.Line)?.Evidence;
            edges.Add(new GraphEdge(
                StableIds.Create(repositoryId, revisionSha, sourceId, targetId, edge.Type, edge.Line, edge.SequenceIndex, edge.ViaApi),
                sourceId,
                targetId,
                edge.Type,
                edge.Label,
                edge.Confidence,
                callEvidence is not null ? [callEvidence.Id] : evidenceByIdentity.TryGetValue(sourceId, out var evidenceIds) ? evidenceIds : [],
                edge.SequenceIndex,
                edge.IsIndirect,
                edge.ViaApi,
                edge.ControlPath,
                revisionSha,
                edge.FilePath,
                edge.Line,
                edge.EndLine ?? edge.Line));
        }

        if (!includeInheritance) return edges;
        var typeFacts = typeSourceFacts.Where(static fact => fact.Kind is "class" or "type").ToArray();
        foreach (var source in typeFacts)
        {
            foreach (var baseName in source.Bases)
            {
                var matches = typeFacts.Where(candidate =>
                    candidate.QualifiedName.Equals(baseName, StringComparison.Ordinal) ||
                    candidate.SimpleName.Equals(baseName.Split("::").Last(), StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1 || !identityBySemanticKey.TryGetValue(source.SemanticKey, out var sourceId) ||
                    !identityBySemanticKey.TryGetValue(matches[0].SemanticKey, out var targetId)) continue;
                edges.Add(new GraphEdge(
                    StableIds.Create(repositoryId, revisionSha, sourceId, targetId, "inherits"), sourceId, targetId, "inherits", "inherits",
                    Confidence.Inferred, evidenceByIdentity.TryGetValue(sourceId, out var evidenceIds) ? evidenceIds : [],
                    RevisionSha: revisionSha, FilePath: source.FilePath, StartLine: source.StartLine, EndLine: source.EndLine));
            }
        }
        return edges;
    }

    private static IReadOnlyList<MethodControlFlow> BuildCppControlFlows(
        IReadOnlyList<CppSymbolFact> facts,
        IReadOnlyList<ParsedSymbol> symbols,
        string revisionSha)
    {
        var identityBySemanticKey = symbols
            .GroupBy(static symbol => symbol.Identity.SemanticKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Identity.Id, StringComparer.Ordinal);
        var evidenceByIdentity = symbols
            .GroupBy(static symbol => symbol.Identity.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Select(static value => value.Evidence.Id).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var result = new List<MethodControlFlow>();
        foreach (var fact in facts.Where(static value => value.ControlNodes is { Count: > 0 }))
        {
            if (!identityBySemanticKey.TryGetValue(fact.SemanticKey, out var identityId)) continue;
            var controlNodes = fact.ControlNodes!;
            var localIds = controlNodes.ToDictionary(
                static node => node.Id,
                node => StableIds.Create(identityId, revisionSha, fact.FilePath, node.Id),
                StringComparer.Ordinal);
            var evidence = evidenceByIdentity.GetValueOrDefault(identityId, []);
            var nodes = controlNodes.Select(node => new ControlFlowNode(
                localIds[node.Id], node.Kind, node.Label, node.StartLine, node.EndLine, evidence,
                node.TargetSemanticKey is not null && identityBySemanticKey.TryGetValue(node.TargetSemanticKey, out var targetId) ? targetId : null,
                node.IsIndirect, node.ViaApi)).ToArray();
            var edges = (fact.ControlEdges ?? []).Where(edge => localIds.ContainsKey(edge.SourceId) && localIds.ContainsKey(edge.TargetId))
                .Select(edge => new ControlFlowEdge(localIds[edge.SourceId], localIds[edge.TargetId], edge.Type, edge.Label)).ToArray();
            result.Add(new MethodControlFlow(identityId, nodes, edges, revisionSha, fact.FilePath));
        }
        return result;
    }

    private static IReadOnlyList<ParsedSymbol> Parse(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content,
        SemanticModel? semanticModel = null)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => ParseCSharp(repositoryId, revisionSha, blobOid, path, content, semanticModel),
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" => ParseCpp(repositoryId, revisionSha, blobOid, path, content),
            _ => [CreateFileSymbol(repositoryId, revisionSha, blobOid, path, content)]
        };
    }

    private static IReadOnlyList<ParsedSymbol> ParseCSharp(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content,
        SemanticModel? semanticModel = null)
    {
        var tree = semanticModel?.SyntaxTree ?? CSharpSyntaxTree.ParseText(content, path: path);
        var root = tree.GetRoot();
        var symbols = new List<ParsedSymbol>();

        foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var name = declaration.Identifier.ValueText;
            var qualifiedName = GetQualifiedName(declaration, name);
            var bases = declaration switch
            {
                TypeDeclarationSyntax type => type.BaseList?.Types.Select(static item => item.Type.ToString()).ToArray() ?? [],
                _ => []
            };
            symbols.Add(CreateSymbol(
                repositoryId, revisionSha, blobOid, path, content, "csharp",
                declaration.Kind().ToString().Replace("Declaration", string.Empty, StringComparison.Ordinal),
                $"type:{qualifiedName}", qualifiedName, declaration.ToString(), declaration.Span,
                [], bases, tree, members: ExtractCSharpMembers(declaration, tree),
                signature: DeclarationSignature(declaration)));
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.ValueText;
            var qualifiedName = GetQualifiedName(method, name);
            var semanticKey = MethodKey(method, semanticModel);
            var callSites = ParseCSharpCalls(method, tree, revisionSha, blobOid, path, semanticModel);
            var calls = callSites.Select(static call => call.Name).Distinct(StringComparer.Ordinal).ToArray();
            var parsed = CreateSymbol(
                repositoryId, revisionSha, blobOid, path, content, "csharp", "method",
                semanticKey, qualifiedName, method.ToString(), method.Span, calls, [], tree, callSites,
                signature: DeclarationSignature(method));
            symbols.Add(parsed with
            {
                ControlFlow = BuildCSharpControlFlow(parsed.Identity.Id, revisionSha, blobOid, path, method, parsed.Evidence.Id)
            });
        }

        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            var name = constructor.Identifier.ValueText;
            var qualifiedName = GetQualifiedName(constructor, name);
            var semanticKey = MethodKey(constructor, semanticModel);
            var callSites = ParseCSharpCalls(constructor, tree, revisionSha, blobOid, path, semanticModel);
            var calls = callSites.Select(static call => call.Name).Distinct(StringComparer.Ordinal).ToArray();
            var parsed = CreateSymbol(
                repositoryId, revisionSha, blobOid, path, content, "csharp", "constructor",
                semanticKey, qualifiedName, constructor.ToString(), constructor.Span, calls, [], tree, callSites,
                signature: DeclarationSignature(constructor));
            symbols.Add(parsed with
            {
                ControlFlow = BuildCSharpControlFlow(parsed.Identity.Id, revisionSha, blobOid, path, constructor, parsed.Evidence.Id)
            });
        }

        return symbols.Count == 0 ? [CreateFileSymbol(repositoryId, revisionSha, blobOid, path, content)] : symbols;
    }

    private static IReadOnlyList<ParsedSymbol> ParseCpp(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content)
    {
        var symbols = new List<ParsedSymbol>();
        foreach (Match match in CppTypeRegex().Matches(content))
        {
            var name = match.Groups[2].Value;
            var bases = match.Groups[3].Success
                ? match.Groups[3].Value.Split(',').Select(static value => value.Trim().Split(' ').Last()).ToArray()
                : [];
            symbols.Add(CreateCppSymbol(repositoryId, revisionSha, blobOid, path, content, "type", $"type:{name}", name, match, [], bases));
        }

        foreach (Match match in CppFunctionRegex().Matches(content))
        {
            var name = match.Groups[1].Value;
            if (CppControlKeywords.Contains(name))
            {
                continue;
            }

            var parameterCount = string.IsNullOrWhiteSpace(match.Groups[2].Value)
                ? 0
                : match.Groups[2].Value.Split(',').Length;
            var end = FindClosingBrace(content, match.Index + match.Length - 1);
            var body = content[match.Index..Math.Min(end + 1, content.Length)];
            var calls = CppCallRegex().Matches(body).Select(static candidate => candidate.Groups[1].Value)
                .Where(nameCandidate => !CppControlKeywords.Contains(nameCandidate))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            symbols.Add(CreateCppSymbol(
                repositoryId, revisionSha, blobOid, path, content, "function",
                $"function:{name}/{parameterCount}", name, match, calls, [], body));
        }

        return symbols.Count == 0 ? [CreateFileSymbol(repositoryId, revisionSha, blobOid, path, content)] : symbols;
    }

    private static IReadOnlyList<ParsedCall> ParseCSharpCalls(
        SyntaxNode declaration, SyntaxTree tree, string revisionSha, string blobOid, string path, SemanticModel? model) =>
        declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => !IsUnreachable(invocation, declaration))
            .Where(invocation => !invocation.Ancestors().TakeWhile(parent => parent != declaration)
                .Any(parent => parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OrderBy(invocation => invocation.Span.End).ThenByDescending(invocation => invocation.SpanStart)
            .Select((invocation, index) =>
            {
                var name = invocation.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => invocation.Expression.ToString().Split('.').Last()
                };
                var span = tree.GetLineSpan(invocation.Span);
                var startLine = span.StartLinePosition.Line + 1;
                var endLine = span.EndLinePosition.Line + 1;
                var evidence = new EvidenceRef(
                    StableIds.Create(revisionSha, blobOid, path, invocation.SpanStart, invocation.Span.End, "csharp-call"),
                    revisionSha, blobOid, path, startLine, endLine, "RoslynInvocation", Confidence.Exact,
                    invocation.SpanStart, invocation.Span.End);
                var target = model?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                return new ParsedCall(name, startLine, endLine, evidence, index + 1,
                    CSharpControlPath(invocation, declaration), target is null ? null : MethodSymbolKey(target), model is not null,
                    DescribeCode(invocation, declaration, revisionSha, blobOid, path, invocation));
            })
            .Where(static call => !string.IsNullOrWhiteSpace(call.Name))
            .ToArray();

    private static ParsedSymbol CreateSymbol(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content,
        string language,
        string kind,
        string semanticKey,
        string qualifiedName,
        string body,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        IReadOnlyList<string> calls,
        IReadOnlyList<string> bases,
        SyntaxTree tree,
        IReadOnlyList<ParsedCall>? callSites = null,
        IReadOnlyList<ClassMemberFact>? members = null,
        string? signature = null)
    {
        var lines = tree.GetLineSpan(span);
        var parsed = CreateParsedSymbol(repositoryId, revisionSha, blobOid, path, language, kind, semanticKey,
            qualifiedName, signature ?? FirstLine(body), body, lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1,
            calls, bases, Confidence.Exact, callSites, members);
        var declaration = tree.GetRoot().FindNode(span, getInnermostNodeForTie: true);
        var owner = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return parsed with
        {
            Evidence = parsed.Evidence with { StartOffset = span.Start, EndOffset = span.End },
            Version = parsed.Version with { OwnerIdentityId = owner is null ? null : StableIds.Create(repositoryId, language,
                owner.Kind().ToString().Replace("Declaration", "", StringComparison.Ordinal),
                $"type:{GetQualifiedName(owner, owner.Identifier.ValueText)}") }
        };
    }

    private static ParsedSymbol CreateCppSymbol(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content,
        string kind,
        string semanticKey,
        string qualifiedName,
        Match match,
        IReadOnlyList<string> calls,
        IReadOnlyList<string> bases,
        string? body = null)
    {
        var value = body ?? match.Value;
        var startLine = CountLines(content.AsSpan(0, match.Index)) + 1;
        var endLine = startLine + CountLines(value.AsSpan());
        return CreateParsedSymbol(repositoryId, revisionSha, blobOid, path, "cpp", kind, semanticKey,
            qualifiedName, FirstLine(match.Value), value, startLine, endLine, calls, bases, Confidence.Inferred);
    }

    private static ParsedSymbol CreateFileSymbol(Guid repositoryId, string revisionSha, string blobOid, string path, string content) =>
        CreateParsedSymbol(repositoryId, revisionSha, blobOid, path, "text", "file", $"file:{path}", path,
            path, content, 1, CountLines(content.AsSpan()) + 1, [], [], Confidence.Inferred);

    private static ParsedSymbol CreateParsedSymbol(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string language,
        string kind,
        string semanticKey,
        string qualifiedName,
        string signature,
        string body,
        int startLine,
        int endLine,
        IReadOnlyList<string> calls,
        IReadOnlyList<string> bases,
        Confidence confidence,
        IReadOnlyList<ParsedCall>? callSites = null,
        IReadOnlyList<ClassMemberFact>? members = null)
    {
        var identityId = StableIds.Create(repositoryId, language, kind, semanticKey);
        var versionId = StableIds.Create(identityId, revisionSha, Hash(body));
        var evidenceId = StableIds.Create(revisionSha, blobOid, path, startLine, endLine, language);
        return new ParsedSymbol(
            new SymbolIdentity(identityId, repositoryId, language, kind, semanticKey),
            new SymbolVersion(versionId, identityId, revisionSha, qualifiedName, signature, path, startLine, endLine, Hash(body), members),
            new EvidenceRef(evidenceId, revisionSha, blobOid, path, startLine, endLine, language == "csharp" ? "RoslynSyntax" : "FallbackParser", confidence),
            body,
            calls,
            bases,
            callSites);
    }

    private static IReadOnlyList<ClassMemberFact> ExtractCSharpMembers(
        BaseTypeDeclarationSyntax declaration,
        SyntaxTree tree)
    {
        if (declaration is not TypeDeclarationSyntax type) return [];
        var defaultAccess = declaration is InterfaceDeclarationSyntax ? "public" : "private";
        var members = new List<ClassMemberFact>();
        foreach (var member in type.Members)
        {
            var access = Accessibility(member.Modifiers, defaultAccess);
            var isStatic = member.Modifiers.Any(SyntaxKind.StaticKeyword);
            var lines = tree.GetLineSpan(member.Span);
            var start = lines.StartLinePosition.Line + 1;
            var end = lines.EndLinePosition.Line + 1;
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        members.Add(new ClassMemberFact(variable.Identifier.ValueText, "field", access,
                            $"{field.Declaration.Type} {variable.Identifier.ValueText}", field.Declaration.Type.ToString(),
                            isStatic, start, end, [field.Declaration.Type.ToString()]));
                    }
                    break;
                case PropertyDeclarationSyntax property:
                    members.Add(new ClassMemberFact(property.Identifier.ValueText, "property", access,
                        $"{property.Type} {property.Identifier.ValueText}", property.Type.ToString(), isStatic,
                        start, end, [property.Type.ToString()]));
                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        members.Add(new ClassMemberFact(variable.Identifier.ValueText, "event", access,
                            $"event {eventField.Declaration.Type} {variable.Identifier.ValueText}", eventField.Declaration.Type.ToString(),
                            isStatic, start, end, [eventField.Declaration.Type.ToString()]));
                    }
                    break;
                case MethodDeclarationSyntax method:
                    members.Add(new ClassMemberFact(method.Identifier.ValueText, "method", access,
                        $"{method.ReturnType} {method.Identifier}{method.ParameterList}", method.ReturnType.ToString(), isStatic,
                        start, end, [method.ReturnType.ToString(), .. method.ParameterList.Parameters.Select(static item => item.Type?.ToString() ?? string.Empty).Where(static item => item.Length > 0)]));
                    break;
                case ConstructorDeclarationSyntax constructor:
                    members.Add(new ClassMemberFact(constructor.Identifier.ValueText, "constructor", access,
                        $"{constructor.Identifier}{constructor.ParameterList}", null, isStatic, start, end,
                        constructor.ParameterList.Parameters.Select(static item => item.Type?.ToString() ?? string.Empty).Where(static item => item.Length > 0).ToArray()));
                    break;
            }
        }
        return members;
    }

    private static string DeclarationSignature(SyntaxNode declaration)
    {
        var value = declaration.ToString();
        var bodyIndex = value.IndexOf('{');
        var expressionIndex = value.IndexOf("=>", StringComparison.Ordinal);
        var end = new[] { bodyIndex, expressionIndex }.Where(static index => index >= 0).DefaultIfEmpty(value.Length).Min();
        return CompactCode(value[..end].TrimEnd().TrimEnd(';'));
    }

    private static string Accessibility(SyntaxTokenList modifiers, string fallback)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword) && modifiers.Any(SyntaxKind.InternalKeyword)) return "protected internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && modifiers.Any(SyntaxKind.ProtectedKeyword)) return "private protected";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
        return fallback;
    }

    private static MethodControlFlow? BuildCSharpControlFlow(
        string identityId,
        string revisionSha,
        string blobOid,
        string path,
        BaseMethodDeclarationSyntax declaration,
        string evidenceId)
    {
        if (declaration.Body is null && declaration.ExpressionBody is null) return null;
        // Exception/disposal/non-local transfers must not become false fall-through paths.
        if (declaration.DescendantNodes().Any(node => node is TryStatementSyntax or GotoStatementSyntax or
            YieldStatementSyntax or UsingStatementSyntax or LockStatementSyntax or FixedStatementSyntax)) return null;
        var nodes = new List<ControlFlowNode>();
        var edges = new List<ControlFlowEdge>();
        var ordinal = 0;
        ControlFlowNode Add(string kind, string label, SyntaxNode syntax)
        {
            var span = syntax.SyntaxTree.GetLineSpan(syntax.Span);
            var node = new ControlFlowNode(
                StableIds.Create(identityId, revisionSha, path, "control", ++ordinal), kind,
                CompactCode(label), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
                [kind is "entry" or "exit" ? evidenceId : StableIds.Create(revisionSha, blobOid, path, syntax.SpanStart, syntax.Span.End, "statement")],
                Context: kind is "entry" or "exit" ? null : DescribeCode(syntax, declaration, revisionSha, blobOid, path));
            nodes.Add(node);
            return node;
        }
        void Connect(IEnumerable<ControlEndpoint> incoming, ControlFlowNode target)
        {
            foreach (var source in incoming) edges.Add(new ControlFlowEdge(source.Id, target.Id, "control", source.Label));
        }

        var entry = Add("entry", "시작", declaration);
        var exit = Add("exit", "종료", declaration);

        IReadOnlyList<ControlEndpoint> Sequence(IEnumerable<StatementSyntax> statements, IReadOnlyList<ControlEndpoint> incoming, ControlFlowNode? loop = null)
        {
            var pending = incoming.ToList();
            foreach (var statement in statements)
            {
                var halted = pending.Where(static item => item.Break).ToList();
                var active = pending.Where(static item => !item.Break).ToArray();
                if (active.Length == 0) break;
                pending = [.. halted, .. Statement(statement, active, loop)];
            }
            return pending;
        }

        IReadOnlyList<ControlEndpoint> Statement(StatementSyntax statement, IReadOnlyList<ControlEndpoint> incoming, ControlFlowNode? loop = null)
        {
            if (statement is BlockSyntax block) return Sequence(block.Statements, incoming, loop);
            if (statement is IfStatementSyntax conditional)
            {
                var decision = Add("condition", conditional.Condition.ToString(), conditional.Condition);
                Connect(incoming, decision);
                var yes = Statement(conditional.Statement, [new ControlEndpoint(decision.Id, "예")], loop);
                var no = conditional.Else is null
                    ? [new ControlEndpoint(decision.Id, "아니오")]
                    : Statement(conditional.Else.Statement, [new ControlEndpoint(decision.Id, "아니오")], loop);
                return [.. yes, .. no];
            }
            if (statement is SwitchStatementSyntax switchStatement)
            {
                var decision = Add("condition", $"switch {switchStatement.Expression}", switchStatement.Expression);
                Connect(incoming, decision);
                var results = new List<ControlEndpoint>();
                foreach (var section in switchStatement.Sections)
                {
                    var label = string.Join(", ", section.Labels.Select(static value => value switch
                    {
                        CaseSwitchLabelSyntax item => $"case {item.Value}",
                        DefaultSwitchLabelSyntax => "default",
                        _ => value.ToString().TrimEnd(':')
                    }));
                    var caseNode = Add("case", label, section.Labels[0]);
                    edges.Add(new ControlFlowEdge(decision.Id, caseNode.Id, "control", label));
                    results.AddRange(Sequence(section.Statements, [new ControlEndpoint(caseNode.Id)], loop)
                        .Select(static endpoint => endpoint with { Break = false }));
                }
                if (!switchStatement.Sections.SelectMany(section => section.Labels).Any(label => label is DefaultSwitchLabelSyntax))
                    results.Add(new ControlEndpoint(decision.Id, "일치 없음"));
                return results;
            }
            if (statement is ForStatementSyntax forStatement)
            {
                var sources = incoming;
                foreach (var initializer in (forStatement.Declaration is null ? Array.Empty<SyntaxNode>() : [forStatement.Declaration])
                    .Concat(forStatement.Initializers))
                {
                    var initialized = Add("operation", initializer.ToString(), initializer);
                    Connect(sources, initialized);
                    sources = [new ControlEndpoint(initialized.Id)];
                }
                var loopNode = Add("loop", forStatement.Condition?.ToString() ?? "항상 참", forStatement.Condition ?? (SyntaxNode)forStatement);
                Connect(sources, loopNode);
                var increments = forStatement.Incrementors.Select(expression => Add("operation", expression.ToString(), expression)).ToArray();
                for (var index = 1; index < increments.Length; index++)
                    edges.Add(new ControlFlowEdge(increments[index - 1].Id, increments[index].Id, "control", ""));
                var repeatTarget = increments.FirstOrDefault() ?? loopNode;
                if (increments.Length > 0) edges.Add(new ControlFlowEdge(increments[^1].Id, loopNode.Id, "loopBack", "조건 재검사"));
                var ends = Statement(forStatement.Statement, [new ControlEndpoint(loopNode.Id, "반복")], repeatTarget);
                foreach (var endpoint in ends.Where(item => !item.Break))
                    edges.Add(new ControlFlowEdge(endpoint.Id, repeatTarget.Id, "loopBack", "다음 반복"));
                return (forStatement.Condition is null ? Array.Empty<ControlEndpoint>() : [new ControlEndpoint(loopNode.Id, "종료")])
                    .Concat(ends.Where(item => item.Break).Select(item => item with { Break = false, Label = "break" })).ToArray();
            }
            if (statement is ForEachStatementSyntax forEach)
                return Loop($"{forEach.Identifier} in {forEach.Expression}", forEach.Statement, forEach, incoming);
            if (statement is WhileStatementSyntax whileStatement)
                return Loop(whileStatement.Condition.ToString(), whileStatement.Statement, whileStatement, incoming);
            if (statement is DoStatementSyntax doStatement)
                return Loop(doStatement.Condition.ToString(), doStatement.Statement, doStatement.Condition, incoming, postTest: true);
            if (statement is ReturnStatementSyntax or ThrowStatementSyntax)
            {
                var returned = Add("return", statement.ToString(), statement);
                Connect(incoming, returned);
                edges.Add(new ControlFlowEdge(returned.Id, exit.Id, "return", "리턴"));
                return [];
            }
            if (statement is ContinueStatementSyntax)
            {
                var continued = Add("continue", "continue", statement);
                Connect(incoming, continued);
                if (loop is not null) edges.Add(new ControlFlowEdge(continued.Id, loop.Id, "loopBack", "다음 반복"));
                return [];
            }
            if (statement is BreakStatementSyntax)
            {
                var broken = Add("break", "break", statement);
                Connect(incoming, broken);
                return [new ControlEndpoint(broken.Id, Break: true)];
            }
            var kind = statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any() ? "call" : "operation";
            var operation = Add(kind, statement.ToString(), statement);
            Connect(incoming, operation);
            return [new ControlEndpoint(operation.Id)];

            IReadOnlyList<ControlEndpoint> Loop(string label, StatementSyntax body, SyntaxNode syntax, IReadOnlyList<ControlEndpoint> sources, bool postTest = false)
            {
                var loopNode = Add("loop", label, syntax);
                ControlFlowNode? bodyEntry = postTest ? Add("operation", "반복 처리 시작", body) : null;
                Connect(sources, bodyEntry ?? loopNode);
                if (bodyEntry is not null) edges.Add(new ControlFlowEdge(loopNode.Id, bodyEntry.Id, "control", "반복"));
                var bodyEnds = Statement(body, [new ControlEndpoint(bodyEntry?.Id ?? loopNode.Id, postTest ? "" : "반복")], loopNode);
                foreach (var endpoint in bodyEnds.Where(static item => !item.Break))
                    edges.Add(new ControlFlowEdge(endpoint.Id, loopNode.Id, "loopBack", "다음 반복"));
                return [new ControlEndpoint(loopNode.Id, "종료"), .. bodyEnds.Where(static item => item.Break).Select(static item => item with { Break = false, Label = "break" })];
            }
        }

        IReadOnlyList<ControlEndpoint> pending;
        if (declaration.Body is not null) pending = Sequence(declaration.Body.Statements, [new ControlEndpoint(entry.Id)]);
        else
        {
            var expression = Add("return", declaration.ExpressionBody!.Expression.ToString(), declaration.ExpressionBody.Expression);
            Connect([new ControlEndpoint(entry.Id)], expression);
            pending = [new ControlEndpoint(expression.Id)];
        }
        Connect(pending.Where(static item => !item.Break), exit);
        return new MethodControlFlow(identityId, nodes, edges, revisionSha, path);
    }

    private static string CompactCode(string value)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact[..Math.Min(1000, compact.Length)];
    }

    private static IReadOnlyList<SymbolChange> BuildChanges(
        IReadOnlyDictionary<string, ParsedSymbol> before,
        IReadOnlyDictionary<string, ParsedSymbol> after)
    {
        var changes = new List<SymbolChange>();
        foreach (var identityId in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(identityId, out var oldSymbol);
            after.TryGetValue(identityId, out var newSymbol);
            if (oldSymbol is null && newSymbol is not null)
            {
                changes.Add(new SymbolChange(StableIds.Create("change", identityId, "add"), SymbolChangeKind.AddSymbol,
                    null, newSymbol.Version.Id, Confidence.Exact, [newSymbol.Evidence.Id]));
            }
            else if (oldSymbol is not null && newSymbol is null)
            {
                changes.Add(new SymbolChange(StableIds.Create("change", identityId, "remove"), SymbolChangeKind.RemoveSymbol,
                    oldSymbol.Version.Id, null, Confidence.Exact, [oldSymbol.Evidence.Id]));
            }
            else if (oldSymbol is not null && newSymbol is not null && oldSymbol.Version.ContentFingerprint != newSymbol.Version.ContentFingerprint)
            {
                var kind = oldSymbol.Version.Signature.Equals(newSymbol.Version.Signature, StringComparison.Ordinal)
                    ? SymbolChangeKind.ModifyBody
                    : SymbolChangeKind.ChangeSignature;
                changes.Add(new SymbolChange(StableIds.Create("change", identityId, kind), kind,
                    oldSymbol.Version.Id, newSymbol.Version.Id, Confidence.Exact, [oldSymbol.Evidence.Id, newSymbol.Evidence.Id]));
            }
        }

        return changes;
    }

    private static IReadOnlyList<GraphEdge> BuildEdges(IReadOnlyList<ParsedSymbol> symbols)
    {
        var callable = symbols
            .Where(static symbol => symbol.Identity.Kind is "method" or "constructor" or "function")
            .GroupBy(static symbol => symbol.Version.QualifiedName.Split('.').Last())
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var types = symbols.Where(static symbol => symbol.Identity.Kind.Contains("type", StringComparison.OrdinalIgnoreCase) ||
                                                   symbol.Identity.Kind.Contains("class", StringComparison.OrdinalIgnoreCase) ||
                                                   symbol.Identity.Kind.Contains("interface", StringComparison.OrdinalIgnoreCase))
            .GroupBy(static symbol => symbol.Version.QualifiedName.Split('.').Last())
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var edges = new List<GraphEdge>();

        foreach (var source in symbols)
        {
            var callSites = source.CallSites ?? source.CalledNames
                .Select(name => new ParsedCall(name, source.Version.StartLine, source.Version.EndLine, source.Evidence))
                .ToArray();
            foreach (var call in callSites)
            {
                var targets = call.TargetKey is not null
                    ? symbols.Where(symbol => symbol.Identity.SemanticKey == call.TargetKey).DistinctBy(symbol => symbol.Identity.Id).ToArray()
                    : call.BindingAttempted ? [] : callable.GetValueOrDefault(call.Name, []);
                if (targets.Length != 1)
                {
                    continue;
                }

                var target = targets[0];
                edges.Add(new GraphEdge(
                    StableIds.Create(source.Version.RevisionSha, source.Identity.Id, target.Identity.Id, "calls", call.Evidence.Id, call.Order),
                    source.Identity.Id, target.Identity.Id, "calls", call.Name, call.TargetKey is null ? Confidence.Inferred : Confidence.Exact,
                    [call.Evidence.Id], SequenceIndex: call.Order, ControlPath: call.ControlPath,
                    RevisionSha: source.Version.RevisionSha, FilePath: source.Version.FilePath,
                    StartLine: call.StartLine, EndLine: call.EndLine, Context: call.Context));
            }

            foreach (var baseType in source.BaseTypeNames)
            {
                if (!types.TryGetValue(baseType.Split('.').Last(), out var targets) || targets.Length != 1)
                {
                    continue;
                }

                var target = targets[0];
                edges.Add(new GraphEdge(
                    StableIds.Create(source.Version.RevisionSha, source.Identity.Id, target.Identity.Id, "inherits"),
                    source.Identity.Id, target.Identity.Id,
                    target.Identity.Kind.Equals("interface", StringComparison.OrdinalIgnoreCase) ? "implements" : "inherits", baseType, Confidence.Inferred,
                    [source.Evidence.Id], RevisionSha: source.Version.RevisionSha, FilePath: source.Version.FilePath,
                    StartLine: source.Version.StartLine, EndLine: source.Version.EndLine));
            }
        }

        return edges.DistinctBy(static edge => edge.Id).ToArray();
    }

    private static IReadOnlyList<GraphEdge> BuildMemberEdges(IReadOnlyList<ParsedSymbol> symbols)
    {
        var typeSymbols = symbols.Where(static symbol => symbol.Version.Members is not null)
            .GroupBy(static symbol => symbol.Version.RevisionSha, StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        foreach (var revision in typeSymbols)
        {
            var candidates = revision.ToArray();
            foreach (var source in candidates)
            {
                foreach (var member in source.Version.Members ?? [])
                {
                    foreach (var reference in member.ReferencedTypes ?? [])
                    {
                        var simple = SimpleTypeName(reference);
                        if (simple.Length == 0 || PrimitiveTypeNames.Contains(simple)) continue;
                        var matches = candidates.Where(candidate => candidate.Identity.Id != source.Identity.Id &&
                            (candidate.Version.QualifiedName.Equals(reference, StringComparison.Ordinal) ||
                             SimpleTypeName(candidate.Version.QualifiedName).Equals(simple, StringComparison.Ordinal))).ToArray();
                        if (matches.Length != 1) continue;
                        var target = matches[0];
                        var edgeType = member.Kind is "field" or "property" or "event" ? "association" : "depends";
                        edges.Add(new GraphEdge(
                            StableIds.Create(source.Version.RevisionSha, source.Identity.Id, target.Identity.Id, edgeType, member.Name),
                            source.Identity.Id, target.Identity.Id, edgeType, member.Name, Confidence.Inferred,
                            [source.Evidence.Id], RevisionSha: source.Version.RevisionSha, FilePath: source.Version.FilePath,
                            StartLine: member.StartLine, EndLine: member.EndLine));
                    }
                }
            }
        }
        return edges.DistinctBy(static edge => edge.Id).ToArray();
    }

    private static string SimpleTypeName(string value)
    {
        var tokens = Regex.Matches(value, @"[A-Za-z_]\w*(?:(?:::|\.)[A-Za-z_]\w*)*")
            .Select(static match => match.Value).ToArray();
        if (tokens.Length == 0) return string.Empty;
        return tokens[^1].Split(["::", "."], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
    }

    private static string GetQualifiedName(SyntaxNode declaration, string name)
    {
        var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Select(static item => item.Name.ToString()).Reverse();
        var types = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .Select(static item => item.Identifier.ValueText).Reverse();
        return string.Join('.', namespaces.Concat(types).Append(name));
    }

    private static int FindClosingBrace(string content, int openingBrace)
    {
        var depth = 0;
        for (var index = openingBrace; index < content.Length; index++)
        {
            if (content[index] == '{') depth++;
            else if (content[index] == '}' && --depth == 0) return index;
        }
        return Math.Min(content.Length - 1, openingBrace);
    }

    private static int CountLines(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (character == '\n') count++;
        }
        return count;
    }

    private static string FirstLine(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')[0].Trim();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static readonly HashSet<string> CppControlKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "sizeof"
    };

    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "void", "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double",
        "decimal", "char", "string", "object", "size_t", "auto", "const", "unsigned", "signed"
    };

    [GeneratedRegex(@"\b(class|struct)\s+([A-Za-z_]\w*)(?:\s*:\s*([^\{]+))?\s*\{")]
    private static partial Regex CppTypeRegex();

    [GeneratedRegex(@"(?:[A-Za-z_]\w*(?:::\w+)*(?:\s*[<>&*]+)?\s+)+([A-Za-z_]\w*(?:::\w+)*)\s*\(([^;{}]*)\)\s*(?:const\s*)?\{")]
    private static partial Regex CppFunctionRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*\(")]
    private static partial Regex CppCallRegex();
}
