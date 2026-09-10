using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class CodeBlockStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static CodeBlockWorkspaceInput Input(string code = "void Run() { Save(); }") =>
        new("Example", [new CodeBlockInput("one", "csharp", "Main", code)]);
    private static CodeBlockWorkspaceService Service(IAppStore store) => new(store, Options.Create(new CodeBlockOptions()), new DiagramPresetCatalog());
    private static string TempPath() => Path.Combine(AppContext.BaseDirectory, "code-store-" + Guid.NewGuid().ToString("N"), "store.json");
    private static IAppStore Store(bool file, string path) => file ? new LocalFileAppStore(path) : new InMemoryAppStore();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedInputCancelsOldRunAndRejectsStaleWorker(bool file)
    {
        await using var store = Store(file, TempPath());
        await store.InitializeAsync(Ct);
        var service = Service(store);
        var workspace = await service.CreateAsync(Input(), "alice", Ct);
        var run = await service.StartAsync(workspace.Id, new(1), "alice", Ct);
        var leased = (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!;
        var changed = await service.SaveAsync(workspace.Id, new(1, Input("void Run() { Stop(); }")), "alice", Ct);
        Assert.Equal(2, changed.Revision);
        Assert.Equal(CodeBlockRunState.Cancelled, (await store.GetCodeBlockRunAsync(run.Id, Ct))!.State);
        Assert.False(await store.SaveCodeBlockRunAsync(leased with { Revision = leased.Revision + 1, State = CodeBlockRunState.Completed }, leased.Revision, Ct));
        Assert.False(await store.RenewCodeBlockLeaseAsync(run.Id, leased.LeaseId!.Value, TimeSpan.FromMinutes(1), Ct));
        var replacement = await service.StartAsync(workspace.Id, new(2), "alice", Ct);
        Assert.Equal("void Run() { Stop(); }", replacement.Snapshot.Blocks[0].Code);
        Assert.Equal("void Run() { Save(); }", (await store.GetCodeBlockRunAsync(run.Id, Ct))!.Snapshot.Blocks[0].Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStartHasOneWinnerAndOwnershipIsChecked(bool file)
    {
        await using var store = Store(file, TempPath());
        await store.InitializeAsync(Ct);
        var service = Service(store);
        var workspace = await service.CreateAsync(Input(), "alice", Ct);
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try { return await service.StartAsync(workspace.Id, new(1), "alice", Ct); }
            catch (CodeBlockConflictException) { return null; }
        }));
        var run = Assert.Single(starts.OfType<CodeBlockRun>());
        Assert.Empty(await store.ListCodeBlockWorkspacesAsync("bob", 20, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetWorkspaceAsync(workspace.Id, "bob", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetRunAsync(run.Id, "bob", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelAsync(run.Id, "bob", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseIsReplacedAndOldLeaseCannotWrite(bool file)
    {
        await using var store = Store(file, TempPath());
        await store.InitializeAsync(Ct);
        var service = Service(store);
        var workspace = await service.CreateAsync(Input(), "alice", Ct);
        await service.StartAsync(workspace.Id, new(1), "alice", Ct);
        var first = (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromSeconds(-1), Ct))!;
        var second = (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!;
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.False(await store.RenewCodeBlockLeaseAsync(first.Id, first.LeaseId!.Value, TimeSpan.FromMinutes(1), Ct));
        Assert.False(await store.SaveCodeBlockRunAsync(first with { Revision = second.Revision + 1 }, second.Revision, Ct));
        Assert.True(await store.RenewCodeBlockLeaseAsync(second.Id, second.LeaseId!.Value, TimeSpan.FromMinutes(2), Ct));
        Assert.True(await store.SaveCodeBlockRunAsync(second with { Revision = second.Revision + 1, State = CodeBlockRunState.Completed }, second.Revision, Ct));
        Assert.Null(await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct));
    }

    [Fact]
    public async Task LocalQuestionAndOriginalSourceSurviveRestartAndDeletionRemovesSourceFiles()
    {
        var path = TempPath();
        CodeBlockWorkspace workspace;
        CodeBlockRun run;
        await using (var store = new LocalFileAppStore(path))
        {
            await store.InitializeAsync(Ct);
            var service = Service(store);
            workspace = await service.CreateAsync(Input("// 한글\r\nvoid Run() { Save(); }"), "alice", Ct);
            run = await service.StartAsync(workspace.Id, new(1), "alice", Ct);
            var leased = (await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct))!;
            Assert.True(await store.SaveCodeBlockRunAsync(leased with { Revision = leased.Revision + 1,
                State = CodeBlockRunState.NeedsClarification, Questions = [new("q", "대상?", "one", "s", "call", [], [new("target", "Save", "t")])] }, leased.Revision, Ct));
        }
        await using (var store = new LocalFileAppStore(path))
        {
            await store.InitializeAsync(Ct);
            var service = Service(store);
            var restored = await service.GetRunAsync(run.Id, "alice", Ct);
            Assert.Equal(CodeBlockRunState.NeedsClarification, restored.State);
            Assert.Equal(workspace.Input.Blocks[0].Code, restored.Snapshot.Blocks[0].Code);
            Assert.Null(await store.TryLeaseCodeBlockRunAsync(TimeSpan.FromMinutes(1), Ct));
            var queued = await service.AnswerAsync(run.Id, new(1, restored.Revision, [], true), "alice", Ct);
            Assert.True(queued.QuestionsResolved);
            Assert.Equal("unknown", Assert.Single(queued.Answers!).OptionId);
            await Assert.ThrowsAsync<CodeBlockConflictException>(() => service.AnswerAsync(run.Id, new(1, restored.Revision, [], true), "alice", Ct));
            await service.DeleteAsync(workspace.Id, 1, "alice", Ct);
            Assert.Null(await store.GetCodeBlockRunAsync(run.Id, Ct));
        }
        await using var reopened = new LocalFileAppStore(path);
        await reopened.InitializeAsync(Ct);
        Assert.Empty(await reopened.ListCodeBlockWorkspacesAsync("alice", 10, Ct));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ValidationRejectsSpoofedEvidenceInvalidGroupsAndEmptyGeneration()
    {
        await using var store = new InMemoryAppStore();
        var service = Service(store);
        Assert.Throws<ArgumentException>(() => service.ValidateInput(Input() with { Title = "  " }));
        Assert.Throws<ArgumentException>(() => service.ValidateInput(Input() with { Relations = [new("r", "one", "one", "calls", "code", "spoof")] }));
        Assert.Throws<ArgumentException>(() => service.ValidateInput(Input() with { Groups = [new("g", "Group", ["foreign"])] }));
        Assert.Throws<ArgumentException>(() => service.ValidateInput(Input() with { Groups = [new("g", "Group", ["one"], [null!])] }));
        Assert.Throws<ArgumentException>(() => service.ValidateInput(Input(new string('x', 100_001))));
        Assert.Throws<ArgumentException>(() => service.ValidateInput(Input() with { Blocks = [new("../path", "csharp", "Bad", "code")] }));
        var workspace = await service.CreateAsync(Input(""), "alice", Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(workspace.Id, new(1), "alice", Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyGroupsAndDisabledIncompleteRelationsRoundTripWithoutBlockingGeneration(bool file)
    {
        var path = TempPath();
        Guid id;
        await using (var store = Store(file, path))
        {
            await store.InitializeAsync(Ct);
            var service = Service(store);
            var input = Input() with { Groups = [new("main", "Main", ["one"], EnableThinking: true, EnableUserRelations: false),
                new("empty", "Empty", [], EnableThinking: false, EnableUserRelations: false)],
                Relations = [new("draft-relation", "one", "one", "uses", "user", "")] };
            var workspace = await service.CreateAsync(input, "alice", Ct); id = workspace.Id;
            var saved = await service.GetWorkspaceAsync(id, "alice", Ct);
            Assert.Equal(2, saved.Input.Groups!.Count);
            Assert.Empty(saved.Input.Groups[1].BlockIds);
            Assert.True(saved.Input.Groups[0].EnableThinking);
            Assert.Equal("", Assert.Single(saved.Input.Relations!).Description);
            await service.StartAsync(id, new(1), "alice", Ct);
            var active = input with { Groups = [input.Groups[0] with { EnableUserRelations = true }, input.Groups[1]] };
            service.ValidateInput(active); // Incomplete relation drafts can still be saved.
            Assert.Throws<ArgumentException>(() => service.ValidateInput(active, forGeneration: true));
            service.ValidateInput(active with { Relations = [input.Relations[0] with { Description = "사용자 제공 의존" }] }, forGeneration: true);
        }
        if (file)
        {
            await using var reopened = new LocalFileAppStore(path);
            await reopened.InitializeAsync(Ct);
            var restored = await Service(reopened).GetWorkspaceAsync(id, "alice", Ct);
            Assert.True(restored.Input.Groups![0].EnableThinking);
            Assert.False(restored.Input.Groups[0].EnableUserRelations);
            Assert.Empty(restored.Input.Groups[1].BlockIds);
            Assert.Single(restored.Input.Relations!);
        }
    }
}
