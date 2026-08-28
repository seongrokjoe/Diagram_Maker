using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Services;

public sealed class NaturalDiagramService(
    IInternalLlmClient llm,
    MermaidCompiler compiler,
    IAppStore store,
    NaturalDiagramSessionCache cache,
    IOptions<LlmOptions> options,
    IWebHostEnvironment environment)
{
    public const string GeneratorVersion = "natural-v2";
    private readonly LlmOptions _options = options.Value;

    public async Task<NaturalDiagramRecord> GenerateAsync(NaturalDiagramRequest request, string ownerUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 10_000)
        {
            throw new ArgumentException("Prompt must contain between 1 and 10,000 characters.");
        }

        var resolvedType = NaturalDiagramTypeResolver.Resolve(request.DiagramType, request.Prompt);
        var normalizedRequest = request with { DiagramType = resolvedType };
        var cacheKey = CreateCacheKey(normalizedRequest, ownerUserId);
        if (!request.ForceRegenerate && cache.TryGet(cacheKey, out var cachedId))
        {
            var cached = await store.GetNaturalDiagramAsync(cachedId, cancellationToken);
            if (cached is not null) return cached with { Reused = true };
        }

        var gate = cache.GetGate(cacheKey);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!request.ForceRegenerate && cache.TryGet(cacheKey, out cachedId))
            {
                var cached = await store.GetNaturalDiagramAsync(cachedId, cancellationToken);
                if (cached is not null) return cached with { Reused = true };
            }

            DiagramIr? ir = null;
            if (llm.IsEnabled)
                ir = await llm.GenerateNaturalDiagramAsync(normalizedRequest.Prompt, resolvedType, normalizedRequest.EnableThinking, cancellationToken);
            if (ir is null && _options.AllowDevelopmentStub && environment.IsDevelopment()) ir = CreateDeterministicDiagram(normalizedRequest);
            if (ir is null) throw new InvalidOperationException("The internal LLM is unavailable and no external fallback is permitted.");

            NaturalDiagramRecord? parent = null;
            if (normalizedRequest.ParentDiagramId is { } parentId)
            {
                parent = await store.GetNaturalDiagramAsync(parentId, cancellationToken)
                         ?? throw new ArgumentException("Parent diagram does not exist.");
                if (!string.IsNullOrEmpty(parent.OwnerUserId) && !parent.OwnerUserId.Equals(ownerUserId, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
            }
            var now = DateTimeOffset.UtcNow;
            var diagramId = Guid.NewGuid();
            var version = (parent?.Diagram.Version ?? 0) + 1;
            var artifact = new DiagramArtifact(diagramId, ir.Type, version, ir, compiler.Compile(ir), now);
            var rootId = parent?.RootDiagramId ?? parent?.Id ?? diagramId;
            var record = new NaturalDiagramRecord(diagramId, normalizedRequest with { ForceRegenerate = false }, artifact, now,
                ownerUserId, rootId, parent?.Id, "generated", GeneratorVersion, false);
            await store.SaveNaturalDiagramAsync(record, cancellationToken);
            cache.Set(cacheKey, record.Id);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    private string CreateCacheKey(NaturalDiagramRequest request, string ownerUserId)
    {
        var normalizedPrompt = string.Join(' ', request.Prompt.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var value = $"{ownerUserId}\n{normalizedPrompt}\n{request.DiagramType}\n{request.EnableThinking}\n{request.ParentDiagramId}\n{_options.Model}\n{GeneratorVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static DiagramIr CreateDeterministicDiagram(NaturalDiagramRequest request)
    {
        var type = ResolveType(request.DiagramType, request.Prompt);
        var normalized = request.Prompt.Replace("=>", "->", StringComparison.Ordinal).Replace("→", "->", StringComparison.Ordinal);
        var labels = normalized.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => Regex.Replace(value, @"\s+", " ").Trim(' ', '.', ','))
            .Where(static value => value.Length > 0)
            .Take(12)
            .ToArray();
        if (labels.Length < 2)
        {
            labels = ["사용자", ShortLabel(request.Prompt), "결과"];
        }

        var nodes = labels.Select((label, index) => new DiagramNode(
            $"n{index + 1}", ShortLabel(label), index == 0 ? "actor" : "component", null,
            "unchanged", Confidence.Inferred, [])).ToArray();
        var edges = Enumerable.Range(0, nodes.Length - 1).Select(index => new DiagramEdge(
            $"e{index + 1}", nodes[index].Id, nodes[index + 1].Id, "flow",
            type == "sequence" ? "요청" : string.Empty, "unchanged", Confidence.Inferred, [], index + 1)).ToArray();
        return new DiagramIr(type, ShortLabel(request.Prompt), nodes, edges,
            ["Development deterministic mode: configure the internal LLM for semantic generation."], []);
    }

    private static string ResolveType(string requested, string prompt)
    {
        if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return requested.ToLowerInvariant();
        }

        if (prompt.Contains("시퀀스", StringComparison.OrdinalIgnoreCase) || prompt.Contains("sequence", StringComparison.OrdinalIgnoreCase)) return "sequence";
        if (prompt.Contains("클래스", StringComparison.OrdinalIgnoreCase) || prompt.Contains("class", StringComparison.OrdinalIgnoreCase)) return "class";
        if (prompt.Contains("상태", StringComparison.OrdinalIgnoreCase) || prompt.Contains("state", StringComparison.OrdinalIgnoreCase)) return "state";
        return "flowchart";
    }

    private static string ShortLabel(string value)
    {
        var label = Regex.Replace(value, @"\s+", " ").Trim();
        return label.Length <= 80 ? label : label[..77] + "...";
    }
}
