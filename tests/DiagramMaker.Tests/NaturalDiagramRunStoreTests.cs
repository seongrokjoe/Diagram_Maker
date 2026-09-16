using DiagramMaker.Domain;
using DiagramMaker.Storage;

namespace DiagramMaker.Tests;

public sealed class NaturalDiagramRunStoreTests
{
    [Fact]
    public void RunStateUsesNamesInHttpJsonAndReadsLegacyNumericValues()
    {
        Assert.Equal("\"Completed\"", System.Text.Json.JsonSerializer.Serialize(NaturalDiagramRunState.Completed));
        Assert.Equal(NaturalDiagramRunState.Completed, System.Text.Json.JsonSerializer.Deserialize<NaturalDiagramRunState>("2"));
    }

    private static readonly CancellationToken Ct = CancellationToken.None;
    private static NaturalDiagramRun Run(string owner = "alice")
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), owner, new("두 시나리오를 그려줘", "flowchart"),
            NaturalDiagramRunState.Queued, now, now, StageMessage: "queued");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseReplacementRejectsStaleWorkerAndOwnerListIsIsolated(bool file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "natural-run-" + Guid.NewGuid().ToString("N"), "store.json");
        await using IAppStore store = file ? new LocalFileAppStore(path) : new InMemoryAppStore();
        await store.InitializeAsync(Ct);
        var run = Run();
        Assert.True(await store.CreateNaturalDiagramRunAsync(run, Ct));
        Assert.False(await store.CreateNaturalDiagramRunAsync(run, Ct));

        var first = (await store.TryLeaseNaturalDiagramRunAsync(TimeSpan.FromSeconds(-1), Ct))!;
        var second = (await store.TryLeaseNaturalDiagramRunAsync(TimeSpan.FromMinutes(1), Ct))!;

        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.False(await store.UpdateNaturalDiagramRunAsync(first with { Revision = second.Revision + 1 },
            second.Revision, first.LeaseId, Ct));
        Assert.True(await store.RenewNaturalDiagramRunLeaseAsync(second.Id, second.LeaseId!.Value, TimeSpan.FromMinutes(1), Ct));
        Assert.Single(await store.ListNaturalDiagramRunsAsync("alice", 10, Ct));
        Assert.Empty(await store.ListNaturalDiagramRunsAsync("bob", 10, Ct));
    }

    [Fact]
    public async Task LocalRunProgressAndScenarioPagesSurviveRestart()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "natural-run-" + Guid.NewGuid().ToString("N"), "store.json");
        var run = Run();
        var ir = new DiagramIr("flowchart", "Saved", [new("a", "A", "component", null, "unchanged", Confidence.Inferred, [])], [], [], []);
        var artifact = new DiagramArtifact(Guid.NewGuid(), "flowchart", 1, ir, "flowchart LR\n a[A]", DateTimeOffset.UtcNow);
        await using (var first = new LocalFileAppStore(path))
        {
            await first.InitializeAsync(Ct);
            Assert.True(await first.CreateNaturalDiagramRunAsync(run, Ct));
            var leased = (await first.TryLeaseNaturalDiagramRunAsync(TimeSpan.FromMinutes(1), Ct))!;
            var view = new NaturalDiagramViewResult("view", new("view", "flowchart", "flow-vertical-overview"),
                artifact, "Generating", Pages: [new("view-scenario-1", "scenario-1", "첫 시나리오", artifact)]);
            Assert.True(await first.UpdateNaturalDiagramRunAsync(leased with { Revision = leased.Revision + 1,
                Progress = 55, Views = [view], StageMessage = "첫 페이지 저장" }, leased.Revision, leased.LeaseId, Ct));
        }

        await using var second = new LocalFileAppStore(path);
        await second.InitializeAsync(Ct);
        var restored = await second.GetNaturalDiagramRunAsync(run.Id, Ct);
        Assert.NotNull(restored);
        Assert.Equal(55, restored.Progress);
        Assert.Equal("view-scenario-1", Assert.Single(Assert.Single(restored.Views!).Pages!).Id);
    }
}
