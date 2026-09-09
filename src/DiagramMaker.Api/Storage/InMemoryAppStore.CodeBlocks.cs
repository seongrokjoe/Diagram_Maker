using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class InMemoryAppStore
{
    private readonly object _codeBlockGate = new();
    private readonly Dictionary<Guid, CodeBlockWorkspace> _codeWorkspaces = [];
    private readonly Dictionary<Guid, CodeBlockRun> _codeRuns = [];

    public Task<CodeBlockWorkspace?> GetCodeBlockWorkspaceAsync(Guid id, CancellationToken cancellationToken)
    { lock (_codeBlockGate) return Task.FromResult(_codeWorkspaces.GetValueOrDefault(id)); }

    public Task<IReadOnlyList<CodeBlockWorkspace>> ListCodeBlockWorkspacesAsync(string ownerUserId, int limit, CancellationToken cancellationToken)
    { lock (_codeBlockGate) return Task.FromResult<IReadOnlyList<CodeBlockWorkspace>>(_codeWorkspaces.Values
        .Where(w => w.OwnerUserId == ownerUserId).OrderByDescending(w => w.UpdatedAt).Take(limit).ToArray()); }

    public Task<bool> SaveCodeBlockWorkspaceAsync(CodeBlockWorkspace workspace, int expectedRevision, CancellationToken cancellationToken)
    {
        lock (_codeBlockGate)
        {
            var current = _codeWorkspaces.GetValueOrDefault(workspace.Id);
            if ((current?.Revision ?? 0) != expectedRevision || workspace.Revision != expectedRevision + 1 ||
                current is not null && current.OwnerUserId != workspace.OwnerUserId) return Task.FromResult(false);
            _codeWorkspaces[workspace.Id] = workspace;
            foreach (var run in _codeRuns.Values.Where(r => r.WorkspaceId == workspace.Id && !r.IsTerminal).ToArray())
                _codeRuns[run.Id] = CodeBlockStoreRules.Cancel(run);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteCodeBlockWorkspaceAsync(Guid id, string ownerUserId, int expectedRevision, CancellationToken cancellationToken)
    {
        lock (_codeBlockGate)
        {
            if (!_codeWorkspaces.TryGetValue(id, out var workspace) || workspace.OwnerUserId != ownerUserId ||
                workspace.Revision != expectedRevision) return Task.FromResult(false);
            var runIds = _codeRuns.Values.Where(r => r.WorkspaceId == id).Select(r => r.Id).ToHashSet();
            foreach (var runId in runIds) _codeRuns.Remove(runId);
            foreach (var revision in _diagramRevisions.Values.Where(r => r.SourceKind == "code-block" && runIds.Contains(r.SourceId)))
                _diagramRevisions.TryRemove(revision.Id, out _);
            _codeWorkspaces.Remove(id);
            return Task.FromResult(true);
        }
    }

    public Task<CodeBlockRun?> GetCodeBlockRunAsync(Guid id, CancellationToken cancellationToken)
    { lock (_codeBlockGate) return Task.FromResult(_codeRuns.GetValueOrDefault(id)); }

    public Task<IReadOnlyList<CodeBlockRun>> ListCodeBlockRunsAsync(Guid workspaceId, int limit, CancellationToken cancellationToken)
    { lock (_codeBlockGate) return Task.FromResult<IReadOnlyList<CodeBlockRun>>(_codeRuns.Values
        .Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.CreatedAt).Take(limit).ToArray()); }

    public Task<bool> CreateCodeBlockRunAsync(CodeBlockRun run, CancellationToken cancellationToken)
    {
        lock (_codeBlockGate)
        {
            if (!_codeWorkspaces.TryGetValue(run.WorkspaceId, out var workspace) || workspace.Revision != run.InputRevision ||
                workspace.OwnerUserId != run.OwnerUserId || _codeRuns.ContainsKey(run.Id) ||
                _codeRuns.Values.Any(r => r.WorkspaceId == run.WorkspaceId && !r.IsTerminal)) return Task.FromResult(false);
            _codeRuns.Add(run.Id, run);
            return Task.FromResult(true);
        }
    }

    public Task<bool> SaveCodeBlockRunAsync(CodeBlockRun run, int expectedRevision, CancellationToken cancellationToken)
    {
        lock (_codeBlockGate)
        {
            if (!_codeRuns.TryGetValue(run.Id, out var current) || current.Revision != expectedRevision || current.IsTerminal ||
                run.Revision != expectedRevision + 1 || current.LeaseId != run.LeaseId ||
                !_codeWorkspaces.TryGetValue(run.WorkspaceId, out var workspace) || workspace.Revision != run.InputRevision ||
                current.WorkspaceId != run.WorkspaceId || current.OwnerUserId != run.OwnerUserId) return Task.FromResult(false);
            _codeRuns[run.Id] = run with { LeaseUntil = run.IsTerminal || run.State == CodeBlockRunState.NeedsClarification
                ? null : current.LeaseUntil, LeaseId = run.IsTerminal || run.State == CodeBlockRunState.NeedsClarification ? null : run.LeaseId };
            return Task.FromResult(true);
        }
    }

    public Task<CodeBlockRun?> TryLeaseCodeBlockRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        lock (_codeBlockGate)
        {
            var run = _codeRuns.Values.Where(r => CodeBlockStoreRules.CanLease(r, DateTimeOffset.UtcNow))
                .OrderBy(r => r.CreatedAt).FirstOrDefault();
            if (run is null) return Task.FromResult<CodeBlockRun?>(null);
            var leased = CodeBlockStoreRules.Lease(run, leaseDuration);
            _codeRuns[run.Id] = leased;
            return Task.FromResult<CodeBlockRun?>(leased);
        }
    }

    public Task<bool> RenewCodeBlockLeaseAsync(Guid runId, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        lock (_codeBlockGate)
        {
            if (!_codeRuns.TryGetValue(runId, out var run) || run.LeaseId != leaseId ||
                run.State is not (CodeBlockRunState.Indexing or CodeBlockRunState.Generating)) return Task.FromResult(false);
            _codeRuns[runId] = run with { LeaseUntil = DateTimeOffset.UtcNow.Add(leaseDuration) };
            return Task.FromResult(true);
        }
    }

    internal void RestoreCodeBlockRecords(IEnumerable<CodeBlockWorkspace> workspaces, IEnumerable<CodeBlockRun> runs)
    {
        lock (_codeBlockGate)
        {
            _codeWorkspaces.Clear(); _codeRuns.Clear();
            foreach (var workspace in workspaces) _codeWorkspaces.Add(workspace.Id, workspace);
            foreach (var run in runs.Where(r => _codeWorkspaces.ContainsKey(r.WorkspaceId))) _codeRuns.Add(run.Id, run);
        }
    }
    internal (CodeBlockWorkspace[] Workspaces, CodeBlockRun[] Runs) SnapshotCodeBlocks()
    { lock (_codeBlockGate) return (_codeWorkspaces.Values.ToArray(), _codeRuns.Values.ToArray()); }
    internal void RemoveCodeBlockRevision(Guid id) => _diagramRevisions.TryRemove(id, out _);
}
