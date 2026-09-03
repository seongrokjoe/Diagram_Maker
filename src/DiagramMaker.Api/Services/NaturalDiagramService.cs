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
    DiagramPresetCatalog presets,
    IOptions<LlmOptions> options,
    IWebHostEnvironment environment)
{
    public const string GeneratorVersion = "natural-v4";
    private readonly LlmOptions _options = options.Value;

    public async Task<NaturalDiagramRecord> GenerateAsync(NaturalDiagramRequest request, string ownerUserId, CancellationToken cancellationToken)
    {
        var normalizedRequest = NormalizeRequest(request);
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

            NaturalDiagramRecord? parent = null;
            if (normalizedRequest.ParentDiagramId is { } parentId)
            {
                parent = await store.GetNaturalDiagramAsync(parentId, cancellationToken)
                         ?? throw new ArgumentException("Parent diagram does not exist.");
                if (!string.IsNullOrEmpty(parent.OwnerUserId) && !parent.OwnerUserId.Equals(ownerUserId, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
            }
            var requestedIds = normalizedRequest.EffectiveViews().Select(static view => view.Id).ToHashSet(StringComparer.Ordinal);
            var record = await BuildRevisionAsync(normalizedRequest, parent, requestedIds, ownerUserId, cancellationToken);
            await store.SaveNaturalDiagramAsync(record, cancellationToken);
            cache.Set(cacheKey, record.Id);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NaturalDiagramRecord> ReviseViewsAsync(
        NaturalDiagramRecord parent,
        IReadOnlyList<DiagramViewSelection> views,
        IReadOnlySet<string> regenerateViewIds,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(parent.OwnerUserId) && !parent.OwnerUserId.Equals(ownerUserId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException();
        var primary = views.FirstOrDefault() ?? throw new ArgumentException("At least one diagram view is required.");
        var request = NormalizeRequest(parent.Request with
        {
            ParentDiagramId = parent.Id,
            DiagramType = primary.DiagramType,
            PresetId = primary.PresetId,
            Style = primary.Overrides,
            Views = views,
            ForceRegenerate = false
        });
        var record = await BuildRevisionAsync(request, parent, regenerateViewIds, ownerUserId, cancellationToken);
        await store.SaveNaturalDiagramAsync(record, cancellationToken);
        return record;
    }

    private NaturalDiagramRequest NormalizeRequest(NaturalDiagramRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 10_000)
            throw new ArgumentException("Prompt must contain between 1 and 10,000 characters.");
        var views = request.EffectiveViews();
        if (views.Count is < 1 or > 4) throw new ArgumentException("One to four natural diagram views are required.");
        var normalized = views.Select(view =>
        {
            var resolvedType = NaturalDiagramTypeResolver.Resolve(view.DiagramType, request.Prompt);
            if (resolvedType is not ("flowchart" or "sequence" or "class" or "state"))
                throw new ArgumentException("Natural diagram views must be flowchart, sequence, class, or state.");
            if (!presets.Contains(resolvedType, view.PresetId))
                throw new ArgumentException($"Preset '{view.PresetId}' does not support {resolvedType}.");
            return DiagramViewSelectionService.Normalize(view with { DiagramType = resolvedType, PresetId = presets.Resolve(resolvedType, view.PresetId).Id });
        }).ToArray();
        if (normalized.Select(static view => view.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("Every diagram view must have a unique ID.");
        if (normalized.Select(static view => view.DiagramType).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new ArgumentException("A diagram type can only be selected once per request.");
        var primary = normalized[0];
        return request with
        {
            DiagramType = primary.DiagramType,
            PresetId = primary.PresetId,
            Style = primary.Overrides,
            Views = normalized
        };
    }

    private async Task<NaturalDiagramRecord> BuildRevisionAsync(
        NaturalDiagramRequest request,
        NaturalDiagramRecord? parent,
        IReadOnlySet<string> regenerateViewIds,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        var previous = EffectiveResults(parent).ToDictionary(static view => view.ViewId, StringComparer.Ordinal);
        var results = new List<NaturalDiagramViewResult>();
        Exception? firstFailure = null;
        foreach (var view in request.EffectiveViews())
        {
            if (!regenerateViewIds.Contains(view.Id) && previous.TryGetValue(view.Id, out var unchanged) && unchanged.Selection == view)
            {
                results.Add(unchanged with { Reused = true });
                continue;
            }
            previous.TryGetValue(view.Id, out var prior);
            try
            {
                var artifact = await GenerateViewAsync(request, view, (prior?.Diagram?.Version ?? 0) + 1, cancellationToken);
                results.Add(new NaturalDiagramViewResult(view.Id, view, artifact));
            }
            catch (Exception exception) when (exception is LlmClientException or InvalidOperationException or DiagramValidationException)
            {
                firstFailure ??= exception;
                var fallback = prior?.Diagram ?? prior?.LastSuccessfulDiagram;
                results.Add(new NaturalDiagramViewResult(view.Id, view, fallback, "Failed",
                    exception is LlmClientException llmException ? llmException.Code : "DIAGRAM_GENERATION_FAILED",
                    exception.Message, fallback));
            }
        }
        var primaryArtifact = results.Select(static result => result.Diagram).FirstOrDefault(static diagram => diagram is not null);
        if (primaryArtifact is null) throw firstFailure ?? new InvalidOperationException("No diagram view could be generated.");
        var now = DateTimeOffset.UtcNow;
        var recordId = Guid.NewGuid();
        var rootId = parent?.RootDiagramId ?? parent?.Id ?? recordId;
        return new NaturalDiagramRecord(recordId, request with { ForceRegenerate = false }, primaryArtifact, now,
            ownerUserId, rootId, parent?.Id, "generated", GeneratorVersion, false, results, (parent?.Revision ?? 0) + 1);
    }

    private async Task<DiagramArtifact> GenerateViewAsync(
        NaturalDiagramRequest request,
        DiagramViewSelection view,
        int version,
        CancellationToken cancellationToken)
    {
        var preset = presets.Resolve(view.DiagramType, view.PresetId);
        DiagramIr? ir = null;
        if (llm.IsEnabled)
            ir = await llm.GenerateNaturalDiagramAsync(request.Prompt, view.DiagramType, request.EnableThinking, preset, view.Overrides, cancellationToken);
        if (ir is null && _options.AllowDevelopmentStub && environment.IsDevelopment())
            ir = CreateDeterministicDiagram(request with { DiagramType = view.DiagramType, PresetId = view.PresetId, Style = view.Overrides });
        if (ir is null) throw new InvalidOperationException("The internal LLM is unavailable and no external fallback is permitted.");
        ir = ApplyPreset(ir, preset, view.Overrides);
        return new DiagramArtifact(Guid.NewGuid(), ir.Type, version, ir, compiler.Compile(ir), DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<NaturalDiagramViewResult> EffectiveResults(NaturalDiagramRecord? record)
    {
        if (record is null) return [];
        if (record.Views is { Count: > 0 }) return record.Views;
        var selection = record.Request.EffectiveViews()[0];
        return [new NaturalDiagramViewResult(selection.Id, selection, record.Diagram, Reused: record.Reused)];
    }

    private string CreateCacheKey(NaturalDiagramRequest request, string ownerUserId)
    {
        var normalizedPrompt = string.Join(' ', request.Prompt.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var views = string.Join('\n', request.EffectiveViews().Select(static view =>
            $"{view.Id}:{view.DiagramType}:{view.PresetId}:{view.Overrides?.Direction}:{view.Overrides?.DetailLevel}:{view.Overrides?.CallerDepth}:{view.Overrides?.CalleeDepth}:{view.Overrides?.RelationDepth}"));
        var value = $"{ownerUserId}\n{normalizedPrompt}\n{views}\n{request.EnableThinking}\n{request.ParentDiagramId}\n{_options.Model}\n{GeneratorVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static DiagramIr ApplyPreset(DiagramIr ir, DiagramPreset preset, DiagramStyleOverrides? style)
    {
        var direction = style?.Direction?.Equals("TB", StringComparison.OrdinalIgnoreCase) == true
            ? "TB"
            : style?.Direction?.Equals("LR", StringComparison.OrdinalIgnoreCase) == true
                ? "LR"
                : preset.Direction;
        var detail = style?.DetailLevel?.ToLowerInvariant() ?? preset.DetailLevel;
        var maximumNodes = detail switch
        {
            "compact" => Math.Min(preset.MaximumNodes, 20),
            "detailed" => Math.Max(preset.MaximumNodes, 40),
            _ => preset.MaximumNodes
        };
        var maximumEdges = detail switch
        {
            "compact" => Math.Min(preset.MaximumEdges, 30),
            "detailed" => Math.Max(preset.MaximumEdges, 60),
            _ => preset.MaximumEdges
        };
        var nodes = ir.Nodes.Take(maximumNodes).ToArray();
        var nodeIds = nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        var edges = ir.Edges
            .Where(edge => nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId))
            .Take(maximumEdges)
            .ToArray();
        return ir with { Direction = direction, Nodes = nodes, Edges = edges };
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
