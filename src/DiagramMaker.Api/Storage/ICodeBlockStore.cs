using DiagramMaker.Domain;

namespace DiagramMaker.Storage;

public interface ICodeBlockStore
{
    Task<CodeBlockWorkspace?> GetCodeBlockWorkspaceAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodeBlockWorkspace>> ListCodeBlockWorkspacesAsync(string ownerUserId, int limit, CancellationToken cancellationToken);
    // expectedRevision=0 creates. Updates atomically cancel unfinished runs based on the old input.
    Task<bool> SaveCodeBlockWorkspaceAsync(CodeBlockWorkspace workspace, int expectedRevision, CancellationToken cancellationToken);
    Task<bool> DeleteCodeBlockWorkspaceAsync(Guid id, string ownerUserId, int expectedRevision, CancellationToken cancellationToken);
    Task<CodeBlockRun?> GetCodeBlockRunAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodeBlockRun>> ListCodeBlockRunsAsync(Guid workspaceId, int limit, CancellationToken cancellationToken);
    Task<bool> CreateCodeBlockRunAsync(CodeBlockRun run, CancellationToken cancellationToken);
    // Compare-and-swap rejects stale workers, cancelled work, changed input and replaced leases.
    Task<bool> SaveCodeBlockRunAsync(CodeBlockRun run, int expectedRevision, CancellationToken cancellationToken);
    Task<CodeBlockRun?> TryLeaseCodeBlockRunAsync(TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> RenewCodeBlockLeaseAsync(Guid runId, Guid leaseId, TimeSpan leaseDuration, CancellationToken cancellationToken);
}

internal static class CodeBlockStoreRules
{
    public static CodeBlockRun Cancel(CodeBlockRun run) => run with
    {
        State = CodeBlockRunState.Cancelled, Revision = run.Revision + 1, LeaseUntil = null, LeaseId = null,
        StageMessage = "입력이 수정되었거나 작업이 취소되었습니다.", UpdatedAt = DateTimeOffset.UtcNow
    };
    public static bool CanLease(CodeBlockRun run, DateTimeOffset now) => run.State == CodeBlockRunState.Queued ||
        run.State is CodeBlockRunState.Indexing or CodeBlockRunState.Generating && (run.LeaseUntil is null || run.LeaseUntil < now);
    public static CodeBlockRun Lease(CodeBlockRun run, TimeSpan duration) => run with
    {
        State = run.Graph is null ? CodeBlockRunState.Indexing : CodeBlockRunState.Generating,
        Revision = run.Revision + 1, LeaseId = Guid.NewGuid(), LeaseUntil = DateTimeOffset.UtcNow.Add(duration),
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
