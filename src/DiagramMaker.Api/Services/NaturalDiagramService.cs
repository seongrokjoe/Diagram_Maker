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
    public const string GeneratorVersion = "natural-v7";
    private readonly LlmOptions _options = options.Value;

    public NaturalDiagramRequest ValidateRequest(NaturalDiagramRequest request) => NormalizeRequest(request);

    internal static string RunPrompt(NaturalDiagramRun run) => run.Answers is not { Count: > 0 } ? run.Request.Prompt :
        run.Request.Prompt + "\n\n사용자 확인 답변:\n" + string.Join("\n", run.Answers.Select(answer =>
            (run.Questions?.FirstOrDefault(q => q.Id == answer.QuestionId)?.Text ?? answer.QuestionId) + ": " + answer.Text));

    public async Task<NaturalRequirements?> PrepareRunRequirementsAsync(NaturalDiagramRun run, CancellationToken ct)
    {
        if (run.Requirements is not null) return run.Requirements;
        var sourceId = run.SourceDiagramId ?? run.Request.ParentDiagramId;
        if (sourceId is { } id && await store.GetNaturalDiagramAsync(id, ct) is { } source &&
            source.OwnerUserId == run.OwnerUserId && source.Request.Prompt == run.Request.Prompt &&
            source.GeneratorVersion == GeneratorVersion && run.Answers is not { Count: > 0 }) return source.Requirements;
        var prompt = RunPrompt(run);
        var result = await llm.ExtractNaturalRequirementsAsync(prompt, run.Request.EnableThinking, ct);
        return result is null ? null : NaturalRequirementEvidence.Attach(prompt, result);
    }

    public async Task<NaturalDiagramRecord> GenerateAsync(NaturalDiagramRequest request, string ownerUserId, CancellationToken cancellationToken)
    {
        using var execution = SemanticExecution.Current is null ? new SemanticExecution(_options, null, cancellationToken) : null;
        cancellationToken = execution?.Token ?? cancellationToken;
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
        using var execution = SemanticExecution.Current is null ? new SemanticExecution(_options, null, cancellationToken) : null;
        cancellationToken = execution?.Token ?? cancellationToken;
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

    public async Task<NaturalDiagramRecord> ExecuteRunAsync(
        NaturalDiagramRun run,
        Func<NaturalGenerationProgress, CancellationToken, Task> reportProgress,
        CancellationToken cancellationToken)
    {
        var request = NormalizeRequest(run.Request) with { Prompt = RunPrompt(run) };
        NaturalDiagramRecord? parent = null;
        var sourceId = run.SourceDiagramId ?? request.ParentDiagramId ?? run.ResultDiagramId;
        if (sourceId is not null)
        {
            parent = await store.GetNaturalDiagramAsync(sourceId.Value, cancellationToken)
                ?? throw new ArgumentException("The source natural diagram does not exist.");
            if (!string.IsNullOrEmpty(parent.OwnerUserId) && !parent.OwnerUserId.Equals(run.OwnerUserId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException();
            request = request with { ParentDiagramId = parent.Id, ForceRegenerate = false };
        }
        var checkpointDiagram = run.Views?.SelectMany(view => view.Pages ?? [])
            .Select(page => page.Diagram ?? page.LastSuccessfulDiagram).FirstOrDefault(diagram => diagram is not null);
        var continuing = checkpointDiagram is not null;
        if (checkpointDiagram is not null)
        {
            parent = parent is null
                ? new NaturalDiagramRecord(Guid.NewGuid(), request, checkpointDiagram, run.CreatedAt, run.OwnerUserId,
                    ParentDiagramId: request.ParentDiagramId, GeneratorVersion: GeneratorVersion,
                    Views: run.Views, Requirements: run.Requirements)
                : parent with { Request = request, Diagram = checkpointDiagram, Views = run.Views, Requirements = run.Requirements };
        }
        HashSet<string>? pageIds;
        if (continuing)
        {
            var completed = run.Views!.SelectMany(view => view.Pages ?? [])
                .Where(page => page.State == "Completed" && page.Diagram is not null)
                .Select(page => page.Id).ToHashSet(StringComparer.Ordinal);
            var scenarios = run.Requirements is null
                ? [new NaturalScenario("scenario-1", request.Prompt, [], [])]
                : NaturalRequirementEvidence.EffectiveScenarios(run.Requirements);
            var selectedViews = run.RegenerateViewIds?.ToHashSet(StringComparer.Ordinal);
            var selectedPages = run.RegeneratePageIds?.ToHashSet(StringComparer.Ordinal);
            pageIds = request.EffectiveViews().SelectMany(view => scenarios.Select(scenario => view.Id + "-" + scenario.Id))
                .Where(pageId => (selectedPages is { Count: > 0 } ? selectedPages.Contains(pageId) :
                    selectedViews is not { Count: > 0 } || selectedViews.Any(viewId => pageId.StartsWith(viewId + "-", StringComparison.Ordinal))) &&
                    !completed.Contains(pageId)).ToHashSet(StringComparer.Ordinal);
        }
        else pageIds = run.RegeneratePageIds?.ToHashSet(StringComparer.Ordinal);
        var viewIds = continuing ? [] : run.RegenerateViewIds?.ToHashSet(StringComparer.Ordinal)
            ?? (parent is null || pageIds is not { Count: > 0 }
                ? request.EffectiveViews().Select(view => view.Id).ToHashSet(StringComparer.Ordinal)
                : []);
        var record = await BuildRevisionAsync(request, parent, viewIds, run.OwnerUserId, cancellationToken,
            pageIds, reportProgress, run.Requirements);
        record = record with { Request = run.Request };
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
        CancellationToken cancellationToken,
        IReadOnlySet<string>? regeneratePageIds = null,
        Func<NaturalGenerationProgress, CancellationToken, Task>? reportProgress = null,
        NaturalRequirements? savedRequirements = null)
    {
        var previous = EffectiveResults(parent).ToDictionary(static view => view.ViewId, StringComparer.Ordinal);
        var results = new List<NaturalDiagramViewResult>();
        Exception? firstFailure = null;
        var selections = request.EffectiveViews();
        var totalUnits = selections.Count;
        var completedUnits = 0;
        var requirements = savedRequirements ?? (parent?.Request.Prompt == request.Prompt && parent.GeneratorVersion == GeneratorVersion
            ? parent.Requirements : null);
        if (requirements is not null && requirements.Scenarios is not { Count: > 0 })
            requirements = NaturalRequirementEvidence.Attach(request.Prompt, requirements);
        if (requirements is not null)
            totalUnits = selections.Count * NaturalRequirementEvidence.EffectiveScenarios(requirements).Count;
        var requirementsAttempted = requirements is not null;
        Exception? requirementsFailure = null;
        foreach (var view in selections)
        {
            var pageRegenerationForView = regeneratePageIds?.Any(pageId => pageId.StartsWith(view.Id + "-", StringComparison.Ordinal)) == true;
            if (!regenerateViewIds.Contains(view.Id) && !pageRegenerationForView && previous.TryGetValue(view.Id, out var unchanged) && unchanged.Selection == view)
            {
                results.Add(unchanged with { Reused = true });
                completedUnits += Math.Max(1, unchanged.Pages?.Count ?? 0);
                if (reportProgress is not null)
                    await reportProgress(new(requirements, results.ToArray(), completedUnits, Math.Max(totalUnits, completedUnits),
                        "변경 없는 결과 재사용"), cancellationToken);
                continue;
            }
            previous.TryGetValue(view.Id, out var prior);
            try
            {
                if (!requirementsAttempted)
                {
                    requirementsAttempted = true;
                    try
                    {
                        requirements = await llm.ExtractNaturalRequirementsAsync(request.Prompt, request.EnableThinking, cancellationToken);
                        if (requirements is not null) requirements = NaturalRequirementEvidence.Attach(request.Prompt, requirements);
                    }
                    catch (LlmClientException error) { requirementsFailure = error; throw; }
                }
                if (requirementsFailure is not null) throw requirementsFailure;
                IReadOnlyList<NaturalScenario> scenarios = requirements is null
                    ? [new("scenario-1", request.Prompt.Length > 80 ? request.Prompt[..80] : request.Prompt, [], [])]
                    : NaturalRequirementEvidence.EffectiveScenarios(requirements);
                totalUnits = selections.Count * scenarios.Count;
                if (reportProgress is not null)
                    await reportProgress(new(requirements, results.ToArray(), completedUnits, totalUnits,
                        "요구사항 저장 완료 · 시나리오 생성 중"), cancellationToken);
                var pages = new List<NaturalDiagramPageResult>();
                Exception? viewFailure = null;
                foreach (var scenario in scenarios)
                {
                    var pageId = view.Id + "-" + scenario.Id;
                    var priorParts = prior?.Pages?.Where(page => page.ScenarioId == scenario.Id).ToArray() ?? [];
                    if (priorParts.Length > 1 && regeneratePageIds is { Count: > 0 } && !regeneratePageIds.Contains(pageId))
                    {
                        foreach (var part in priorParts)
                        {
                            if (!regeneratePageIds.Contains(part.Id)) { pages.Add(part with { Reused = true }); continue; }
                            try
                            {
                                var subset = requirements is null ? null : NaturalRequirementEvidence.ForScenario(requirements,
                                    scenario with { RequirementIds = part.DesignQuality?.ReviewedRequirementIds ?? scenario.RequirementIds });
                                pages.AddRange(await GenerateBoundedPagesAsync(request, view, scenario with { Title = part.Title }, part.Id,
                                    (part.Diagram?.Version ?? 0) + 1, subset, cancellationToken));
                            }
                            catch (LlmClientException error)
                            {
                                viewFailure ??= error;
                                pages.Add(part with { State = "Failed", ErrorCode = error.Code, ErrorMessage = error.Message,
                                    LastSuccessfulDiagram = part.Diagram ?? part.LastSuccessfulDiagram });
                            }
                        }
                        completedUnits++;
                        if (reportProgress is not null) await reportProgress(new(requirements,
                            results.Append(ProgressView(view, pages, viewFailure, scenarios.Count)).ToArray(),
                            completedUnits, totalUnits, "선택한 상세 페이지 저장"), cancellationToken);
                        continue;
                    }
                    var priorPage = prior?.Pages?.FirstOrDefault(page => page.ScenarioId == scenario.Id);
                    if (priorPage is null && scenarios.Count == 1 && prior?.Diagram is not null)
                        priorPage = new(view.Id + "-scenario-1", scenario.Id, scenario.Title, prior.Diagram,
                            prior.State, prior.ErrorCode, prior.ErrorMessage, prior.LastSuccessfulDiagram, prior.Reused, prior.DesignQuality);
                    if (regeneratePageIds is { Count: > 0 } && !regeneratePageIds.Contains(pageId) && priorPage is not null)
                    {
                        pages.Add(priorPage with { Reused = true });
                        completedUnits++;
                        if (reportProgress is not null)
                            await reportProgress(new(requirements,
                                results.Append(ProgressView(view, pages, viewFailure, scenarios.Count)).ToArray(),
                                completedUnits, totalUnits, "선택하지 않은 시나리오 결과 재사용"), cancellationToken);
                        continue;
                    }
                    try
                    {
                        var scenarioRequirements = requirements is null ? null : NaturalRequirementEvidence.ForScenario(requirements, scenario);
                        var generatedPages = await GenerateBoundedPagesAsync(request, view, scenario, pageId,
                            (priorPage?.Diagram?.Version ?? 0) + 1, scenarioRequirements, cancellationToken);
                        pages.AddRange(generatedPages);
                    }
                    catch (Exception exception) when (exception is LlmClientException or InvalidOperationException or DiagramValidationException)
                    {
                        firstFailure ??= exception;
                        viewFailure ??= exception;
                        var fallback = priorPage?.Diagram ?? priorPage?.LastSuccessfulDiagram;
                        pages.Add(new(pageId, scenario.Id, scenario.Title, fallback, "Failed",
                            exception is LlmClientException llmException ? llmException.Code : "DIAGRAM_GENERATION_FAILED",
                            exception.Message, fallback, DesignQuality: priorPage?.DesignQuality));
                    }
                    completedUnits++;
                    if (reportProgress is not null)
                        await reportProgress(new(requirements,
                            results.Append(ProgressView(view, pages, viewFailure, scenarios.Count)).ToArray(),
                            completedUnits, totalUnits, "시나리오별 다이어그램 저장 중"), cancellationToken);
                }
                var successful = pages.Where(page => page.Diagram is not null).ToArray();
                var state = pages.All(page => page.State == "Completed") ? "Completed" :
                    pages.All(page => page.State == "Failed") ? "Failed" : "Partial";
                var primary = successful.FirstOrDefault()?.Diagram;
                results.Add(new NaturalDiagramViewResult(view.Id, view, primary, state,
                    viewFailure is LlmClientException llmError ? llmError.Code : viewFailure is null ? null : "DIAGRAM_GENERATION_FAILED",
                    viewFailure?.Message, primary, DesignQuality: successful.FirstOrDefault()?.DesignQuality, Pages: pages));
            }
            catch (Exception exception) when (exception is LlmClientException or InvalidOperationException or DiagramValidationException)
            {
                firstFailure ??= exception;
                var fallback = prior?.Diagram ?? prior?.LastSuccessfulDiagram;
                results.Add(new NaturalDiagramViewResult(view.Id, view, fallback, "Failed",
                    exception is LlmClientException llmException ? llmException.Code : "DIAGRAM_GENERATION_FAILED",
                    exception.Message, fallback, DesignQuality: prior?.DesignQuality));
                completedUnits++;
                if (reportProgress is not null)
                    await reportProgress(new(requirements, results.ToArray(), completedUnits, totalUnits,
                        "다이어그램 생성 실패 진단 저장"), cancellationToken);
            }
        }
        if (requirements is not null && results.Any(result => result.State == "Completed" && !result.Reused))
        {
            try
            {
                if (!await llm.ReviewNaturalSetAsync(request.Prompt, requirements, results, request.EnableThinking, cancellationToken))
                    throw new LlmClientException("NATURAL_CROSS_VIEW_REVIEW", "형식 간 누락 또는 모순 검토를 통과하지 못했습니다. 검증된 개별 페이지를 보존했습니다.");
            }
            catch (LlmClientException error)
            {
                for (var i = 0; i < results.Count; i++)
                    if (results[i].State == "Completed") results[i] = results[i] with { State = "Partial", ErrorCode = error.Code, ErrorMessage = error.Message };
            }
        }
        if (reportProgress is not null)
            await reportProgress(new(requirements, results.ToArray(), Math.Max(completedUnits, totalUnits), totalUnits,
                "자연어 다이어그램 생성 마무리"), cancellationToken);
        var primaryArtifact = results.Select(static result => result.Diagram).FirstOrDefault(static diagram => diagram is not null);
        if (primaryArtifact is null) throw firstFailure ?? new InvalidOperationException("No diagram view could be generated.");
        var now = DateTimeOffset.UtcNow;
        var recordId = Guid.NewGuid();
        var rootId = parent?.RootDiagramId ?? parent?.Id ?? recordId;
        return new NaturalDiagramRecord(recordId, request with { ForceRegenerate = false }, primaryArtifact, now,
            ownerUserId, rootId, parent?.Id, "generated", GeneratorVersion, false, results, (parent?.Revision ?? 0) + 1, requirements);
    }

    private static NaturalDiagramViewResult ProgressView(DiagramViewSelection view,
        IReadOnlyList<NaturalDiagramPageResult> pages, Exception? failure, int expectedPages)
    {
        var successful = pages.Where(page => page.Diagram is not null).ToArray();
        var state = pages.Count < expectedPages ? "Generating" : pages.All(page => page.State == "Completed") ? "Completed" :
            pages.All(page => page.State == "Failed") ? "Failed" : "Partial";
        return new(view.Id, view, successful.FirstOrDefault()?.Diagram, state,
            failure is LlmClientException llmError ? llmError.Code : failure is null ? null : "DIAGRAM_GENERATION_FAILED",
            failure?.Message, successful.FirstOrDefault()?.Diagram,
            DesignQuality: successful.FirstOrDefault()?.DesignQuality, Pages: pages.ToArray());
    }

    private async Task<IReadOnlyList<NaturalDiagramPageResult>> GenerateBoundedPagesAsync(
        NaturalDiagramRequest request, DiagramViewSelection view, NaturalScenario scenario, string pageId, int version,
        NaturalRequirements? requirements, CancellationToken ct, int depth = 0)
    {
        try
        {
            var page = await SemanticExecution.RunAsync("natural-page",
                System.Text.Json.JsonSerializer.Serialize(new { request, view, scenario, pageId, version, requirements, GeneratorVersion }),
                async () =>
                {
                    var generated = await GenerateViewAsync(request, view, version, requirements, ct);
                    return new NaturalDiagramPageResult(pageId, scenario.Id, scenario.Title, generated.Artifact, DesignQuality: generated.Quality);
                }, value => value.State == "Completed");
            return [page!];
        }
        catch (LlmClientException error) when (error.Code is "LLM_RESPONSE_TRUNCATED" or "LLM_INPUT_LIMIT" or
            "LLM_CONTEXT_LIMIT" or "LLM_INPUT_CHARACTERS" or "LLM_OUTPUT_BUDGET" or "NATURAL_DESIGN_LIMIT")
        {
            if (requirements is null || depth >= 5) throw;
            var shared = requirements.Requirements.Where(r => r.Kind is "entity" or "interlock").ToArray();
            var targets = requirements.Requirements.Except(shared).ToArray();
            if (targets.Length < 2) throw;
            var pages = new List<NaturalDiagramPageResult>();
            foreach (var (part, index) in new[] { targets.Take(targets.Length / 2), targets.Skip(targets.Length / 2) }.Select((part, index) => (part, index)))
            {
                var selected = shared.Concat(part).ToArray();
                var rangeIds = selected.SelectMany(NaturalRequirementEvidence.RangeIds).ToHashSet();
                var ranges = (requirements.SourceRanges ?? []).Where(r => rangeIds.Contains(r.Id)).ToArray();
                var subset = requirements with { Requirements = selected, SourceRanges = ranges };
                var title = scenario.Title + $" · 상세 {index + 1}";
                // Every selected source span and all common constraints are retained; do not truncate strings.
                var scopedRequest = request with { Prompt = string.Join("\n", ranges.Select(r => r.Text)) };
                pages.AddRange(await GenerateBoundedPagesAsync(scopedRequest, view, scenario with { Title = title },
                    pageId + $"-part-{index + 1}", version, subset, ct, depth + 1));
            }
            return pages;
        }
    }

    private async Task<(DiagramArtifact Artifact, NaturalDesignQuality? Quality)> GenerateViewAsync(
        NaturalDiagramRequest request,
        DiagramViewSelection view,
        int version,
        NaturalRequirements? requirements,
        CancellationToken cancellationToken)
    {
        _ = environment; // Constructor retained for existing integrations.
        var preset = presets.Resolve(view.DiagramType, view.PresetId);
        NaturalDesignedDiagram? generated = null;
        if (llm.IsEnabled)
            generated = await llm.GenerateDesignedNaturalAsync(request.Prompt, view.DiagramType, request.EnableThinking, preset, view.Overrides, requirements, cancellationToken);
        if (generated is null) throw new LlmClientException("LLM_DISABLED", "The internal LLM is unavailable; generation requires the approved internal server.");
        var ir = ApplyPreset(generated.Diagram, preset, view.Overrides);
        return (new DiagramArtifact(Guid.NewGuid(), ir.Type, version, ir, compiler.Compile(ir), DateTimeOffset.UtcNow), generated.Quality);
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
        // Whitespace and Unicode form determine exact source ranges and paragraph scenarios.
        var normalizedPrompt = request.Prompt;
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
        return ir with { Direction = direction };
    }

}
