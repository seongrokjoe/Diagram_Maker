using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public sealed partial class LocalFileAppStore
{
    private readonly SemaphoreSlim _codeFileGate = new(1, 1);
    private string CodeWorkspaceDirectory => Path.Combine(Path.GetDirectoryName(_filePath)!, "code-block-workspaces");
    private string CodeRunDirectory => Path.Combine(Path.GetDirectoryName(_filePath)!, "code-block-runs");

    private async Task InitializeCodeBlocksAsync(CancellationToken cancellationToken)
    {
        var workspaces = new List<CodeBlockWorkspace>();
        var runs = new List<CodeBlockRun>();
        await LoadRecordsAsync(CodeWorkspaceDirectory, json => { workspaces.Add(Deserialize<CodeBlockWorkspace>(json)); return Task.CompletedTask; }, cancellationToken);
        await LoadRecordsAsync(CodeRunDirectory, json => { runs.Add(Deserialize<CodeBlockRun>(json)); return Task.CompletedTask; }, cancellationToken);
        var byId = workspaces.ToDictionary(w => w.Id);
        foreach (var orphan in runs.Where(r => !byId.ContainsKey(r.WorkspaceId)))
            File.Delete(Path.Combine(CodeRunDirectory, $"{orphan.Id:N}.json"));
        // A workspace write/delete is committed before its affected run files. Recover interruptions conservatively.
        runs = runs.Where(r => byId.ContainsKey(r.WorkspaceId)).Select(r => !r.IsTerminal &&
            byId[r.WorkspaceId].Revision != r.InputRevision ? CodeBlockStoreRules.Cancel(r) : r).ToList();
        _inner.RestoreCodeBlockRecords(workspaces, runs);
        var runIds = runs.Select(r => r.Id).ToHashSet();
        var stale = (await _inner.ListAllDiagramRevisionsAsync(cancellationToken))
            .Where(r => r.SourceKind == "code-block" && !runIds.Contains(r.SourceId)).ToArray();
        foreach (var revision in stale)
        {
            _inner.RemoveCodeBlockRevision(revision.Id);
            File.Delete(Path.Combine(_diagramRevisionDirectory, $"{revision.Id:N}.json"));
        }
    }

    private async Task<T> ReadCodeBlocksAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        await _codeFileGate.WaitAsync(cancellationToken);
        try { return await read(); }
        finally { _codeFileGate.Release(); }
    }

    private async Task<T> MutateCodeBlocksAsync<T>(Func<InMemoryAppStore, Task<T>> mutate, CancellationToken cancellationToken)
    {
        await _codeFileGate.WaitAsync(cancellationToken);
        try
        {
            var before = _inner.SnapshotCodeBlocks();
            await using var staged = new InMemoryAppStore();
            staged.RestoreCodeBlockRecords(before.Workspaces, before.Runs);
            var result = await mutate(staged);
            var after = staged.SnapshotCodeBlocks();
            var oldWorkspaces = before.Workspaces.ToDictionary(w => w.Id);
            var oldRuns = before.Runs.ToDictionary(r => r.Id);
            try
            {
                foreach (var workspace in after.Workspaces.Where(w => !oldWorkspaces.TryGetValue(w.Id, out var old) || old != w))
                    await PersistRecordAsync(CodeWorkspaceDirectory, workspace.Id, workspace, cancellationToken);
                // Removing this authoritative file makes leftover run files unreachable after a crash.
                foreach (var workspace in before.Workspaces.Where(w => after.Workspaces.All(a => a.Id != w.Id)))
                    File.Delete(Path.Combine(CodeWorkspaceDirectory, $"{workspace.Id:N}.json"));
                foreach (var run in after.Runs.Where(r => !oldRuns.TryGetValue(r.Id, out var old) || old != r))
                    await PersistRecordAsync(CodeRunDirectory, run.Id, run, cancellationToken);
                var removedIds = before.Runs.Where(r => after.Runs.All(a => a.Id != r.Id)).Select(r => r.Id).ToHashSet();
                foreach (var id in removedIds) File.Delete(Path.Combine(CodeRunDirectory, $"{id:N}.json"));
                foreach (var revision in (await _inner.ListAllDiagramRevisionsAsync(cancellationToken))
                    .Where(r => r.SourceKind == "code-block" && removedIds.Contains(r.SourceId)))
                {
                    File.Delete(Path.Combine(_diagramRevisionDirectory, $"{revision.Id:N}.json"));
                    _inner.RemoveCodeBlockRevision(revision.Id);
                }
                _inner.RestoreCodeBlockRecords(after.Workspaces, after.Runs);
            }
            catch
            {
                // Do not continue serving an in-memory version inconsistent with an interrupted disk write.
                await InitializeCodeBlocksAsync(CancellationToken.None);
                throw;
            }
            return result;
        }
        finally { _codeFileGate.Release(); }
    }

    public Task<CodeBlockWorkspace?> GetCodeBlockWorkspaceAsync(Guid id, CancellationToken cancellationToken) =>
        ReadCodeBlocksAsync(() => _inner.GetCodeBlockWorkspaceAsync(id, cancellationToken), cancellationToken);
    public Task<IReadOnlyList<CodeBlockWorkspace>> ListCodeBlockWorkspacesAsync(string ownerUserId, int limit, CancellationToken cancellationToken) =>
        ReadCodeBlocksAsync(() => _inner.ListCodeBlockWorkspacesAsync(ownerUserId, limit, cancellationToken), cancellationToken);
    public Task<bool> SaveCodeBlockWorkspaceAsync(CodeBlockWorkspace workspace, int expectedRevision, CancellationToken cancellationToken) =>
        MutateCodeBlocksAsync(s => s.SaveCodeBlockWorkspaceAsync(workspace, expectedRevision, cancellationToken), cancellationToken);
    public Task<bool> DeleteCodeBlockWorkspaceAsync(Guid id, string ownerUserId, int expectedRevision, CancellationToken cancellationToken) =>
        MutateCodeBlocksAsync(s => s.DeleteCodeBlockWorkspaceAsync(id, ownerUserId, expectedRevision, cancellationToken), cancellationToken);
    public Task<CodeBlockRun?> GetCodeBlockRunAsync(Guid id, CancellationToken cancellationToken) =>
        ReadCodeBlocksAsync(() => _inner.GetCodeBlockRunAsync(id, cancellationToken), cancellationToken);
    public Task<IReadOnlyList<CodeBlockRun>> ListCodeBlockRunsAsync(Guid workspaceId, int limit, CancellationToken cancellationToken) =>
        ReadCodeBlocksAsync(() => _inner.ListCodeBlockRunsAsync(workspaceId, limit, cancellationToken), cancellationToken);
    public Task<bool> CreateCodeBlockRunAsync(CodeBlockRun run, CancellationToken cancellationToken) =>
        MutateCodeBlocksAsync(s => s.CreateCodeBlockRunAsync(run, cancellationToken), cancellationToken);
    public Task<bool> SaveCodeBlockRunAsync(CodeBlockRun run, int expectedRevision, CancellationToken cancellationToken) =>
        MutateCodeBlocksAsync(s => s.SaveCodeBlockRunAsync(run, expectedRevision, cancellationToken), cancellationToken);
    public Task<CodeBlockRun?> TryLeaseCodeBlockRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        MutateCodeBlocksAsync(s => s.TryLeaseCodeBlockRunAsync(leaseDuration, cancellationToken), cancellationToken);
    public Task<bool> RenewCodeBlockLeaseAsync(Guid runId, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        MutateCodeBlocksAsync(s => s.RenewCodeBlockLeaseAsync(runId, leaseId, leaseDuration, cancellationToken), cancellationToken);
}
