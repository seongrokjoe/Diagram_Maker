using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;

namespace DiagramMaker.Tests;

public sealed class LocalFileAppStoreTests
{
    [Fact]
    public async Task AnalysisHistory_SurvivesRestartAndUsesNewestFirstLimit()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, $"history-registry-{Guid.NewGuid():N}.json");
        var planId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var first = new LocalFileAppStore(filePath))
        {
            await first.InitializeAsync(CancellationToken.None);
            for (var index = 0; index < 3; index++)
            {
                var job = new AnalysisJob(Guid.NewGuid(), new AnalyzeRequest(repositoryId, "base", "target", AnalysisPlanId: planId),
                    AnalysisState.Completed, "base", "target", 100, "done", null, null, null,
                    now.AddMinutes(index), now.AddMinutes(index), null);
                await first.SaveAnalysisAsync(job, CancellationToken.None);
            }
        }

        await using var second = new LocalFileAppStore(filePath);
        await second.InitializeAsync(CancellationToken.None);
        var history = await second.ListAnalysesByPlanAsync(planId, 2, CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.True(history[0].CreatedAt > history[1].CreatedAt);
    }

    [Fact]
    public async Task RepositoryRegistration_SurvivesStoreRestart()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, $"repository-registry-{Guid.NewGuid():N}.json");
        var repository = new RepositoryDefinition(
            Guid.NewGuid(), "Test repository", @"C:\Work\Repository", "main", ["Reviewer"], DateTimeOffset.UtcNow,
            new RepositoryAnalysisRules(1,
            [
                new IndirectCallRule("run-function", "RunFunction", true, "RunFunction", 0, 1,
                    [new IndirectCallAlias("m_strFunctionOprXfer", "Opr_Xfer")])
            ]));

        await using (var first = new LocalFileAppStore(filePath))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.SaveRepositoryAsync(repository, CancellationToken.None);
        }

        await using var second = new LocalFileAppStore(filePath);
        await second.InitializeAsync(CancellationToken.None);
        var restored = await second.GetRepositoryAsync(repository.Id, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(repository.LocalPath, restored.LocalPath);
        Assert.Equal(repository.DefaultBranch, restored.DefaultBranch);
        Assert.Equal(1, restored.AnalysisRules?.Revision);
        Assert.Equal("Opr_Xfer", Assert.Single(Assert.Single(restored.AnalysisRules!.IndirectCalls).Aliases).TargetType);
    }

    [Fact]
    public async Task NaturalDiagram_SurvivesStoreRestartWithoutRepositoryRegistry()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, $"diagram-registry-{Guid.NewGuid():N}.json");
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var ir = new DiagramIr(
            "flowchart", "Persistent",
            [new DiagramNode("a", "A", "component", null, "unchanged", Confidence.Inferred, [])],
            [], [], []);
        var record = new NaturalDiagramRecord(
            id,
            new NaturalDiagramRequest("A를 그려줘", "flowchart"),
            new DiagramArtifact(id, "flowchart", 1, ir, "flowchart LR\n    n_a[\"A\"]\n", now),
            now,
            "reviewer",
            id);

        await using (var first = new LocalFileAppStore(filePath))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.SaveNaturalDiagramAsync(record, CancellationToken.None);
        }

        Assert.False(File.Exists(filePath));
        await using var second = new LocalFileAppStore(filePath);
        await second.InitializeAsync(CancellationToken.None);
        var restored = await second.GetNaturalDiagramAsync(id, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("Persistent", restored.Diagram.Ir.Title);
    }

    [Fact]
    public async Task AnalysisPlan_SurvivesStoreRestartForThirtyDayDraftWorkflow()
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, $"plan-registry-{Guid.NewGuid():N}.json");
        var now = DateTimeOffset.UtcNow;
        var plan = new AnalysisPlan(
            Guid.NewGuid(), "reviewer", new AnalysisPlanRequest(Guid.NewGuid(), "target"),
            AnalysisPlanState.Ready, "base", "target", 100, "Ready", null, null,
            [], [], [], [], null, null, 3, now, now, now.AddDays(30), null);

        await using (var first = new LocalFileAppStore(filePath))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.SaveAnalysisPlanAsync(plan, CancellationToken.None);
        }

        await using var second = new LocalFileAppStore(filePath);
        await second.InitializeAsync(CancellationToken.None);
        var restored = await second.GetAnalysisPlanAsync(plan.Id, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(3, restored.Revision);
        Assert.Equal(plan.ExpiresAt, restored.ExpiresAt);
    }

    [Fact]
    public void LocalRepositoryPath_AcceptsRepositoryRootAndDotGitDirectory()
    {
        var repositoryPath = Path.Combine(AppContext.BaseDirectory, $"local-repository-{Guid.NewGuid():N}");
        var dotGitPath = Path.Combine(repositoryPath, ".git");
        Directory.CreateDirectory(dotGitPath);

        Assert.Equal(repositoryPath, LocalRepositoryPath.NormalizeAndValidate(repositoryPath));
        Assert.Equal(repositoryPath, LocalRepositoryPath.NormalizeAndValidate(dotGitPath));
    }

    [Fact]
    public void LocalRepositoryPath_RejectsRelativePath()
    {
        var exception = Assert.Throws<ArgumentException>(() => LocalRepositoryPath.NormalizeAndValidate("relative-repository"));
        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
