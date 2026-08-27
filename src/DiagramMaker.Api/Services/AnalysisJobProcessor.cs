using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed class AnalysisJobProcessor(
    IAppStore store,
    IGitWorkerClient git,
    SourceGraphAnalyzer analyzer,
    IInternalLlmClient llm,
    MermaidCompiler compiler,
    ILogger<AnalysisJobProcessor> logger)
{
    public async Task ProcessAsync(AnalysisJob leasedJob, CancellationToken cancellationToken)
    {
        try
        {
            var repository = await store.GetRepositoryAsync(leasedJob.Request.RepositoryId, cancellationToken)
                             ?? throw new InvalidOperationException("Repository is no longer registered.");

            var comparison = await git.CompareAsync(repository, leasedJob.Request, cancellationToken);
            var job = await UpdateAsync(leasedJob with
            {
                State = AnalysisState.Indexing,
                BaseSha = comparison.BaseSha,
                TargetSha = comparison.TargetSha,
                Progress = 25,
                StageMessage = "Extracting changed symbols"
            }, cancellationToken);

            var graph = analyzer.Analyze(repository.Id, comparison);
            job = await UpdateAsync(job with
            {
                State = AnalysisState.Graphing,
                Progress = 55,
                StageMessage = "Building versioned symbol graph"
            }, cancellationToken);

            var deterministic = BuildDeterministicNarrative(graph, comparison.Files);
            var narrative = deterministic;
            var warnings = deterministic.Warnings.ToList();
            var llmSucceeded = false;
            if (job.Request.IncludeLlmSummary && llm.IsEnabled)
            {
                job = await UpdateAsync(job with
                {
                    State = AnalysisState.Summarizing,
                    Progress = 70,
                    StageMessage = "Requesting evidence-bound internal LLM review"
                }, cancellationToken);
                try
                {
                    var generated = await llm.GenerateReviewAsync(graph, comparison.Files, cancellationToken);
                    if (generated is not null)
                    {
                        narrative = generated;
                        llmSucceeded = true;
                    }
                    else
                    {
                        warnings.Add("Internal LLM returned no valid evidence-bound result.");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Internal LLM review failed for analysis {AnalysisId}; deterministic results remain available.", job.Id);
                    warnings.Add("Internal LLM review failed; deterministic analysis is shown.");
                }
            }
            else if (job.Request.IncludeLlmSummary)
            {
                warnings.Add("Internal LLM is disabled; deterministic analysis is shown.");
            }

            narrative = narrative with { Warnings = narrative.Warnings.Concat(warnings).Distinct(StringComparer.Ordinal).ToArray() };
            job = await UpdateAsync(job with
            {
                State = AnalysisState.Rendering,
                Progress = 85,
                StageMessage = "Compiling safe Mermaid diagram"
            }, cancellationToken);

            var diagramIr = BuildDiagram(repository.Name, graph, comparison);
            var artifact = new DiagramArtifact(Guid.NewGuid(), diagramIr.Type, 1, diagramIr, compiler.Compile(diagramIr), DateTimeOffset.UtcNow);
            var safeFiles = comparison.Files.Select(static file => file with { BeforeContent = null, AfterContent = null }).ToArray();
            var result = new AnalysisResult(safeFiles, graph, narrative, [artifact]);
            var finalState = job.Request.IncludeLlmSummary && !llmSucceeded ? AnalysisState.Partial : AnalysisState.Completed;
            await UpdateAsync(job with
            {
                State = finalState,
                Progress = 100,
                StageMessage = finalState == AnalysisState.Completed ? "Analysis completed" : "Static analysis completed with degraded AI summary",
                Result = result,
                LeaseUntil = null
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analysis {AnalysisId} failed at stage {State}.", leasedJob.Id, leasedJob.State);
            await store.SaveAnalysisAsync(leasedJob with
            {
                State = AnalysisState.Failed,
                Progress = 100,
                StageMessage = "Analysis failed",
                ErrorCode = MapErrorCode(exception),
                ErrorMessage = SafeMessage(exception),
                LeaseUntil = null,
                UpdatedAt = DateTimeOffset.UtcNow
            }, CancellationToken.None);
        }
    }

    private async Task<AnalysisJob> UpdateAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        var updated = job with { UpdatedAt = DateTimeOffset.UtcNow };
        await store.SaveAnalysisAsync(updated, cancellationToken);
        return updated;
    }

    private static ReviewNarrative BuildDeterministicNarrative(VersionedGraph graph, IReadOnlyList<ChangedFile> files)
    {
        var additions = graph.Changes.Count(static change => change.Type == SymbolChangeKind.AddSymbol);
        var removals = graph.Changes.Count(static change => change.Type == SymbolChangeKind.RemoveSymbol);
        var modifications = graph.Changes.Count - additions - removals;
        var risks = new List<RiskItem>();
        foreach (var change in graph.Changes.Where(static change => change.Type is SymbolChangeKind.RemoveSymbol or SymbolChangeKind.ChangeSignature))
        {
            risks.Add(new RiskItem(
                change.Type == SymbolChangeKind.RemoveSymbol ? "high" : "medium",
                change.Type == SymbolChangeKind.RemoveSymbol ? "A symbol was removed; verify all external callers." : "A symbol signature changed; verify compatibility.",
                change.EvidenceIds));
        }

        return new ReviewNarrative(
            $"{files.Count} files changed; {additions} symbols added, {removals} removed, and {modifications} modified.",
            "Deterministic summary derived from immutable Git revisions and syntax analysis.",
            risks,
            []);
    }

    private static DiagramIr BuildDiagram(string repositoryName, VersionedGraph graph, GitComparison comparison)
    {
        var changeByIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in graph.Changes)
        {
            var before = graph.Versions.FirstOrDefault(version => version.Id == change.BeforeSymbolVersionId);
            var after = graph.Versions.FirstOrDefault(version => version.Id == change.AfterSymbolVersionId);
            var identityId = after?.IdentityId ?? before?.IdentityId;
            if (identityId is null) continue;
            changeByIdentity[identityId] = change.Type switch
            {
                SymbolChangeKind.AddSymbol => "added",
                SymbolChangeKind.RemoveSymbol => "deleted",
                _ => "modified"
            };
        }

        var selectedIdentities = graph.Identities
            .OrderBy(identity => changeByIdentity.ContainsKey(identity.Id) ? 0 : 1)
            .ThenBy(static identity => identity.SemanticKey, StringComparer.Ordinal)
            .Take(DiagramValidator.MaximumNodes)
            .ToArray();
        var selectedIdentityIds = selectedIdentities.Select(static identity => identity.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = selectedIdentities.Select(identity =>
        {
            var version = graph.Versions.FirstOrDefault(candidate => candidate.IdentityId == identity.Id && candidate.RevisionSha == comparison.TargetSha)
                          ?? graph.Versions.First(candidate => candidate.IdentityId == identity.Id);
            var evidence = graph.Evidence.Where(item => item.RevisionSha == version.RevisionSha && item.FilePath == version.FilePath &&
                                                        item.StartLine == version.StartLine).Select(static item => item.Id).ToArray();
            return new DiagramNode(identity.Id, version.QualifiedName, identity.Kind, Path.GetDirectoryName(version.FilePath),
                changeByIdentity.GetValueOrDefault(identity.Id, "unchanged"), evidence.Length == 0 ? Confidence.Inferred : Confidence.Exact, evidence);
        }).ToArray();
        var edges = graph.Edges
            .Where(edge => selectedIdentityIds.Contains(edge.FromIdentityId) && selectedIdentityIds.Contains(edge.ToIdentityId))
            .Take(DiagramValidator.MaximumEdges)
            .Select(edge => new DiagramEdge(
                edge.Id, edge.FromIdentityId, edge.ToIdentityId, edge.Type, edge.Label, "unchanged",
                edge.Confidence, edge.EvidenceIds, edge.SequenceIndex))
            .ToArray();
        var notes = new List<string> { "Exact and inferred relations are distinguished in the evidence panel." };
        if (graph.Identities.Count > nodes.Length || graph.Edges.Count > edges.Length)
        {
            notes.Add($"The preview is limited to {nodes.Length} nodes and {edges.Length} edges; the complete graph remains available in the analysis result.");
        }
        return new DiagramIr(
            "dependency",
            $"{repositoryName}: {comparison.BaseSha[..8]} → {comparison.TargetSha[..8]}",
            nodes,
            edges,
            notes,
            [comparison.BaseSha, comparison.TargetSha]);
    }

    private static string MapErrorCode(Exception exception) => exception switch
    {
        DirectoryNotFoundException => "REPOSITORY_NOT_FOUND",
        TimeoutException => "ANALYSIS_TIMEOUT",
        DiagramValidationException => "DIAGRAM_INVALID",
        _ => "ANALYSIS_FAILED"
    };

    private static string SafeMessage(Exception exception) => exception switch
    {
        DirectoryNotFoundException => "The registered repository is unavailable.",
        OperationCanceledException => "The analysis timed out or was cancelled.",
        DiagramValidationException => exception.Message,
        _ => "The analysis failed. Consult the internal server log using the analysis ID."
    };
}
