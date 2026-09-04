using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Services;

public sealed partial class DiagramRevisionService(
    IAppStore store,
    DiagramValidator validator,
    MermaidCompiler compiler)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RevisionGates = new();

    public async Task<DiagramRevisionRecord> SaveAsync(
        DiagramArtifact source,
        SaveDiagramEditRequest request,
        string ownerUserId,
        string sourceKind,
        Guid sourceId,
        string? groupId,
        string viewId,
        CancellationToken cancellationToken)
    {
        if (request.RootArtifactId != source.Id)
            throw new ArgumentException("RootArtifactId must match the generated diagram artifact.");
        ValidateDocument(request.Document);
        var gate = RevisionGates.GetOrAdd(request.RootArtifactId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (currentVersion, parentRevisionId, ir) = await BuildAsync(source, request, ownerUserId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var artifact = new DiagramArtifact(Guid.NewGuid(), ir.Type, currentVersion + 1, ir, compiler.Compile(ir), now);
            var record = new DiagramRevisionRecord(
                Guid.NewGuid(), request.RootArtifactId, source.Id, parentRevisionId, ownerUserId,
                sourceKind, sourceId, groupId, viewId, artifact.Version, artifact, now);
            await store.SaveDiagramRevisionAsync(record, cancellationToken);
            return record;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiagramEditPreviewResponse> PreviewAsync(
        DiagramArtifact source,
        SaveDiagramEditRequest request,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        if (request.RootArtifactId != source.Id)
            throw new ArgumentException("RootArtifactId must match the generated diagram artifact.");
        ValidateDocument(request.Document);
        var gate = RevisionGates.GetOrAdd(request.RootArtifactId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (currentVersion, _, ir) = await BuildAsync(source, request, ownerUserId, cancellationToken);
            return new DiagramEditPreviewResponse(currentVersion, ir, compiler.Compile(ir));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(int CurrentVersion, Guid? ParentRevisionId, DiagramIr Ir)> BuildAsync(
        DiagramArtifact source,
        SaveDiagramEditRequest request,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        var revisions = await store.ListDiagramRevisionsAsync(request.RootArtifactId, ownerUserId, cancellationToken);
        var latest = revisions.LastOrDefault();
        var currentVersion = latest?.Version ?? source.Version;
        if (request.ExpectedVersion != currentVersion)
            throw new DiagramRevisionConflictException(currentVersion);
        if (latest?.Id != request.ParentRevisionId || latest is null && request.ParentRevisionId is not null)
            throw new DiagramRevisionConflictException(currentVersion);

        var ir = ApplyDocument((latest?.Diagram ?? source).Ir, request.Document);
        validator.Validate(ir);
        return (currentVersion, latest?.Id, ir);
    }

    private static DiagramIr ApplyDocument(DiagramIr basis, DiagramEditDocument document)
    {
        var existingNodes = basis.Nodes.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        var nodes = document.Nodes.Select(node => existingNodes.TryGetValue(node.Id, out var existing)
            ? existing with { Label = node.Label.Trim() }
            : new DiagramNode(node.Id.Trim(), node.Label.Trim(), NodeKind(basis.Type), null,
                "unchanged", Confidence.Inferred, [])).ToArray();
        var existingEdges = basis.Edges.ToDictionary(static edge => edge.Id, StringComparer.Ordinal);
        var edges = document.Edges.Select((edge, index) => existingEdges.TryGetValue(edge.Id, out var existing)
            ? existing with
            {
                SourceId = edge.SourceId.Trim(),
                TargetId = edge.TargetId.Trim(),
                Label = edge.Label.Trim(),
                Type = string.IsNullOrWhiteSpace(edge.Type) ? existing.Type : edge.Type.Trim(),
                SequenceIndex = basis.Type.Equals("sequence", StringComparison.OrdinalIgnoreCase) ? index + 1 : existing.SequenceIndex
            }
            : new DiagramEdge(edge.Id.Trim(), edge.SourceId.Trim(), edge.TargetId.Trim(),
                string.IsNullOrWhiteSpace(edge.Type) ? DefaultEdgeType(basis.Type) : edge.Type.Trim(), edge.Label.Trim(),
                "unchanged", Confidence.Inferred, [],
                basis.Type.Equals("sequence", StringComparison.OrdinalIgnoreCase) ? index + 1 : null)).ToArray();
        return basis with
        {
            Title = document.Title.Trim(),
            Direction = document.Direction,
            Nodes = nodes,
            Edges = edges,
            Notes = basis.Notes.Concat(["구조 편집기에서 수정됨."]).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static void ValidateDocument(DiagramEditDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Title) || document.Title.Trim().Length > 200)
            throw new ArgumentException("The diagram title must contain 1-200 characters.");
        if (document.Direction is not null && document.Direction is not ("LR" or "TB"))
            throw new ArgumentException("Direction must be LR or TB.");
        if (document.Nodes is null || document.Nodes.Count is < 1 or > 100)
            throw new ArgumentException("A diagram must contain 1-100 nodes.");
        if (document.Edges is null || document.Edges.Count > 200)
            throw new ArgumentException("A diagram may contain at most 200 edges.");
        if (document.Nodes.Any(static node => string.IsNullOrWhiteSpace(node.Id) || !SafeId().IsMatch(node.Id) || string.IsNullOrWhiteSpace(node.Label) || node.Label.Trim().Length > 240))
            throw new ArgumentException("Node IDs and labels must be valid and no longer than 240 characters.");
        if (document.Edges.Any(static edge => string.IsNullOrWhiteSpace(edge.Id) || !SafeId().IsMatch(edge.Id) || string.IsNullOrWhiteSpace(edge.SourceId) || string.IsNullOrWhiteSpace(edge.TargetId) || edge.Label is null || edge.Label.Length > 240))
            throw new ArgumentException("Edge IDs, endpoints, and labels must be valid and no longer than 240 characters.");
        if (document.Nodes.Select(static node => node.Id).Distinct(StringComparer.Ordinal).Count() != document.Nodes.Count)
            throw new ArgumentException("Node IDs must be unique.");
        if (document.Edges.Select(static edge => edge.Id).Distinct(StringComparer.Ordinal).Count() != document.Edges.Count)
            throw new ArgumentException("Edge IDs must be unique.");
        var nodeIds = document.Nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (document.Edges.Any(edge => !nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId)))
            throw new ArgumentException("Every edge must reference an existing node.");
    }

    private static string NodeKind(string type) => type switch
    {
        "sequence" => "participant",
        "class" => "class",
        "state" => "state",
        _ => "component"
    };

    private static string DefaultEdgeType(string type) => type switch
    {
        "sequence" => "message",
        "class" => "uses",
        "state" => "transition",
        _ => "flow"
    };

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,120}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}

public sealed class DiagramRevisionConflictException(int currentVersion) : Exception("The diagram revision has changed. Reload it and try again.")
{
    public int CurrentVersion { get; } = currentVersion;
}
