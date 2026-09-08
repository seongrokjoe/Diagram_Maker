using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// The ordinary Git parser/indexer is used, but only the prepared immutable sample pair is accessible.
public sealed class SampleGitWorkerClient(GitWorkerClient inner, CodexSampleCatalog samples) : IGitWorkerClient
{
    public Task<GitRepositoryInspection> InspectAsync(string localPath, CancellationToken token) =>
        throw new ArgumentException("샘플 테스트에서는 저장소를 등록하거나 검사할 수 없습니다.");

    public async Task<GitComparison> CompareAsync(RepositoryDefinition repository, AnalyzeRequest request, CancellationToken token)
    {
        await CheckAsync(repository, request.BaseRevision, request.TargetRevision, token);
        var comparison = await inner.CompareAsync(repository, request, token);
        samples.VerifyComparison(repository.Id, comparison);
        return comparison;
    }

    public async Task<PreparedRepositoryAnalysis> PrepareAsync(RepositoryDefinition repository, string baseRevision, string targetRevision, CancellationToken token)
    {
        await CheckAsync(repository, baseRevision, targetRevision, token);
        var prepared = await inner.PrepareAsync(repository, baseRevision, targetRevision, token);
        samples.VerifyComparison(repository.Id, prepared.Comparison);
        return prepared;
    }

    public async Task<IReadOnlyList<GitCommitSummary>> ListCommitsAsync(RepositoryDefinition repository, string? query, int skip, int limit, CancellationToken token)
    {
        var sample = samples.Repository(repository.Id);
        var commits = new[] { await GetCommitAsync(repository, sample.TargetSha, token), await GetCommitAsync(repository, sample.BaseSha, token) };
        return commits.Where(commit => string.IsNullOrEmpty(query) || commit.Sha.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            commit.Message.Contains(query, StringComparison.OrdinalIgnoreCase)).Skip(skip).Take(limit).ToArray();
    }

    public async Task<GitCommitSummary> GetCommitAsync(RepositoryDefinition repository, string revision, CancellationToken token)
    {
        await samples.VerifyRepositoryAsync(repository, token);
        var sample = samples.Repository(repository.Id);
        if (revision != sample.BaseSha && revision != sample.TargetSha) throw new ArgumentException("샘플 커밋만 조회할 수 있습니다.");
        var commit = await inner.GetCommitAsync(repository, revision, token);
        if (commit.Sha != revision || commit.AuthorName != "Synthetic Test" || commit.AuthorEmail != "synthetic@example.invalid" ||
            commit.Message.Trim() != (revision == sample.BaseSha ? "Synthetic baseline" : "Synthetic change"))
            throw new ArgumentException("샘플 커밋 정보가 변경되었습니다.");
        return commit;
    }

    public async Task<EvidenceSnippet> ReadEvidenceAsync(RepositoryDefinition repository, string revisionSha,
        string filePath, int startLine, int endLine, CancellationToken token)
    {
        await samples.VerifyRepositoryAsync(repository, token);
        var sample = samples.Repository(repository.Id);
        if (revisionSha != sample.BaseSha && revisionSha != sample.TargetSha || filePath != samples.Scenario(sample.ScenarioId).FileName)
            throw new ArgumentException("샘플 파일 근거만 조회할 수 있습니다.");
        return await inner.ReadEvidenceAsync(repository, revisionSha, filePath, startLine, endLine, token);
    }

    private async Task CheckAsync(RepositoryDefinition repository, string before, string after, CancellationToken token)
    {
        await samples.VerifyRepositoryAsync(repository, token);
        var sample = samples.Repository(repository.Id);
        if (before != sample.BaseSha || after != sample.TargetSha) throw new ArgumentException("등록된 샘플 변경 전·후 커밋만 사용할 수 있습니다.");
    }
}
