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
    private sealed record ParsedSymbol(
        SymbolIdentity Identity,
        SymbolVersion Version,
        EvidenceRef Evidence,
        string Body,
        IReadOnlyList<string> CalledNames,
        IReadOnlyList<string> BaseTypeNames);

    public VersionedGraph Analyze(Guid repositoryId, GitComparison comparison) => Analyze(repositoryId, comparison, null);

    public VersionedGraph Analyze(Guid repositoryId, GitComparison comparison, CppSourceIndex? cppIndex)
    {
        var before = new List<ParsedSymbol>();
        var after = new List<ParsedSymbol>();
        var hasCppIndex = cppIndex is { TargetSymbols.Count: > 0 };

        foreach (var file in comparison.Files)
        {
            var beforePath = file.PreviousPath ?? file.Path;
            if (file.BeforeContent is not null && file.BeforeBlobOid is not null && !(hasCppIndex && IsCppPath(beforePath)))
            {
                before.AddRange(Parse(repositoryId, comparison.BaseSha, file.BeforeBlobOid, beforePath, file.BeforeContent));
            }

            if (file.AfterContent is not null && file.AfterBlobOid is not null && !(hasCppIndex && IsCppPath(file.Path)))
            {
                after.AddRange(Parse(repositoryId, comparison.TargetSha, file.AfterBlobOid, file.Path, file.AfterContent));
            }
        }

        foreach (var file in comparison.ContextFiles ?? [])
        {
            if (hasCppIndex && IsCppPath(file.Path)) continue;
            var parsed = Parse(repositoryId, file.RevisionSha, file.BlobOid, file.Path, file.Content);
            if (file.RevisionSha == comparison.BaseSha) before.AddRange(parsed);
            if (file.RevisionSha == comparison.TargetSha) after.AddRange(parsed);
        }

        var beforeByIdentity = before.GroupBy(static symbol => symbol.Identity.Id).ToDictionary(static group => group.Key, static group => group.First());
        var afterByIdentity = after.GroupBy(static symbol => symbol.Identity.Id).ToDictionary(static group => group.Key, static group => group.First());
        var changes = BuildChanges(beforeByIdentity, afterByIdentity).ToList();
        var edges = BuildEdges(after).ToList();
        var allSymbols = before.Concat(after).ToList();

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
            edges.AddRange(BuildCppEdges(repositoryId, cppIndex, cppTarget));
            allSymbols.AddRange(cppBefore);
            allSymbols.AddRange(cppTarget);
        }

        return new VersionedGraph(
            allSymbols.Select(static symbol => symbol.Identity).DistinctBy(static identity => identity.Id).ToArray(),
            allSymbols.Select(static symbol => symbol.Version).DistinctBy(static version => version.Id).ToArray(),
            edges.DistinctBy(static edge => edge.Id).ToArray(),
            allSymbols.Select(static symbol => symbol.Evidence).DistinctBy(static evidence => evidence.Id).ToArray(),
            changes.DistinctBy(static change => change.Id).ToArray());
    }

    private static bool IsCppPath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp";

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
                fact.StartLine, fact.EndLine, fact.ContentFingerprint),
            new EvidenceRef(evidenceId, revisionSha, blobOid, fact.FilePath, fact.StartLine, fact.EndLine,
                "TreeSitterCpp", Confidence.Exact),
            fact.ContentFingerprint,
            [],
            fact.Bases);
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
        CppSourceIndex index,
        IReadOnlyList<ParsedSymbol> targetSymbols)
    {
        var identityBySemanticKey = targetSymbols
            .GroupBy(static symbol => symbol.Identity.SemanticKey)
            .ToDictionary(static group => group.Key, static group => group.First().Identity.Id, StringComparer.Ordinal);
        var evidenceByIdentity = targetSymbols.ToDictionary(static symbol => symbol.Identity.Id, static symbol => symbol.Evidence.Id, StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        foreach (var edge in index.TargetEdges)
        {
            if (!identityBySemanticKey.TryGetValue(edge.SourceSemanticKey, out var sourceId) ||
                !identityBySemanticKey.TryGetValue(edge.TargetSemanticKey, out var targetId)) continue;
            edges.Add(new GraphEdge(
                StableIds.Create(repositoryId, sourceId, targetId, edge.Type),
                sourceId,
                targetId,
                edge.Type,
                edge.Label,
                edge.Confidence,
                evidenceByIdentity.TryGetValue(sourceId, out var evidenceId) ? [evidenceId] : [],
                edge.SequenceIndex));
        }

        var typeFacts = index.TargetSymbols.Where(static fact => fact.Kind is "class" or "type").ToArray();
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
                    StableIds.Create(repositoryId, sourceId, targetId, "inherits"), sourceId, targetId, "inherits", "inherits",
                    Confidence.Inferred, evidenceByIdentity.TryGetValue(sourceId, out var evidenceId) ? [evidenceId] : []));
            }
        }
        return edges;
    }

    private static IReadOnlyList<ParsedSymbol> Parse(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => ParseCSharp(repositoryId, revisionSha, blobOid, path, content),
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" => ParseCpp(repositoryId, revisionSha, blobOid, path, content),
            _ => [CreateFileSymbol(repositoryId, revisionSha, blobOid, path, content)]
        };
    }

    private static IReadOnlyList<ParsedSymbol> ParseCSharp(
        Guid repositoryId,
        string revisionSha,
        string blobOid,
        string path,
        string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content, path: path);
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
                [], bases, tree));
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.ValueText;
            var qualifiedName = GetQualifiedName(method, name);
            var semanticKey = $"method:{qualifiedName}/{method.ParameterList.Parameters.Count}";
            var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(static invocation => invocation.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    _ => invocation.Expression.ToString()
                })
                .Where(static called => !string.IsNullOrWhiteSpace(called))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            symbols.Add(CreateSymbol(
                repositoryId, revisionSha, blobOid, path, content, "csharp", "method",
                semanticKey, qualifiedName, method.ToString(), method.Span, calls, [], tree));
        }

        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            var name = constructor.Identifier.ValueText;
            var qualifiedName = GetQualifiedName(constructor, name);
            var semanticKey = $"ctor:{qualifiedName}/{constructor.ParameterList.Parameters.Count}";
            var calls = constructor.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(static invocation => invocation.Expression.ToString().Split('.').Last())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            symbols.Add(CreateSymbol(
                repositoryId, revisionSha, blobOid, path, content, "csharp", "constructor",
                semanticKey, qualifiedName, constructor.ToString(), constructor.Span, calls, [], tree));
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
        SyntaxTree tree)
    {
        var lines = tree.GetLineSpan(span);
        return CreateParsedSymbol(repositoryId, revisionSha, blobOid, path, language, kind, semanticKey,
            qualifiedName, FirstLine(body), body, lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1, calls, bases, Confidence.Exact);
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
        Confidence confidence)
    {
        var identityId = StableIds.Create(repositoryId, language, kind, semanticKey);
        var versionId = StableIds.Create(identityId, revisionSha, Hash(body));
        var evidenceId = StableIds.Create(revisionSha, blobOid, path, startLine, endLine, language);
        return new ParsedSymbol(
            new SymbolIdentity(identityId, repositoryId, language, kind, semanticKey),
            new SymbolVersion(versionId, identityId, revisionSha, qualifiedName, signature, path, startLine, endLine, Hash(body)),
            new EvidenceRef(evidenceId, revisionSha, blobOid, path, startLine, endLine, language == "csharp" ? "RoslynSyntax" : "FallbackParser", confidence),
            body,
            calls,
            bases);
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
            foreach (var calledName in source.CalledNames)
            {
                if (!callable.TryGetValue(calledName, out var targets) || targets.Length != 1)
                {
                    continue;
                }

                var target = targets[0];
                edges.Add(new GraphEdge(
                    StableIds.Create(source.Identity.Id, target.Identity.Id, "calls"),
                    source.Identity.Id, target.Identity.Id, "calls", "calls", Confidence.Inferred,
                    [source.Evidence.Id]));
            }

            foreach (var baseType in source.BaseTypeNames)
            {
                if (!types.TryGetValue(baseType.Split('.').Last(), out var targets) || targets.Length != 1)
                {
                    continue;
                }

                var target = targets[0];
                edges.Add(new GraphEdge(
                    StableIds.Create(source.Identity.Id, target.Identity.Id, "inherits"),
                    source.Identity.Id, target.Identity.Id, "inherits", "inherits", Confidence.Inferred,
                    [source.Evidence.Id]));
            }
        }

        return edges.DistinctBy(static edge => edge.Id).ToArray();
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

    [GeneratedRegex(@"\b(class|struct)\s+([A-Za-z_]\w*)(?:\s*:\s*([^\{]+))?\s*\{")]
    private static partial Regex CppTypeRegex();

    [GeneratedRegex(@"(?:[A-Za-z_]\w*(?:::\w+)*(?:\s*[<>&*]+)?\s+)+([A-Za-z_]\w*(?:::\w+)*)\s*\(([^;{}]*)\)\s*(?:const\s*)?\{")]
    private static partial Regex CppFunctionRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*\(")]
    private static partial Regex CppCallRegex();
}
