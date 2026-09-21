using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using DiagramMaker.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalReliabilityTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData("한글 🙂 첫 문장.\r\n다음 문장.")]
    [InlineData("- 로그인 검사\n- 저장 처리")]
    [InlineData("| 상태 | 전이 |\n| 대기 | 실행 |")]
    [InlineData("token=abcdefghij 다음 처리.")]
    [InlineData("Bearer abc.def.ghi 다음 처리.")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nabcdef\n-----END RSA PRIVATE KEY-----\n실행한다.")]
    [InlineData("password: x 비밀값 검사.")]
    [InlineData("A\r\n\r\nB")]
    public void ServerEvidenceMapsMaskedAndUnicodeInputBackToExactOriginal(string prompt)
    {
        var masker = new SecretMasker();
        var masked = masker.MaskWithOffsets(prompt);
        Assert.Equal(masker.Mask(prompt), masked.Text);
        Assert.Equal(masked.Text.Length, masked.Starts.Count);
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        Assert.NotEmpty(ranges);
        Assert.All(ranges, range => Assert.Equal(prompt[range.StartOffset..range.EndOffset], range.Text));
        Assert.Equal(ranges.Count, ranges.Select(range => range.Id).Distinct().Count());
        foreach (var range in ranges)
        {
            var requirement = new NaturalRequirement("r1", "동작", "behavior", "explicit", "changed quotation", SourceRangeIds: [range.Id]);
            Assert.True(NaturalRequirementEvidence.TryResolve(prompt, requirement, ranges, out var resolved));
            Assert.Equal(range.Text, resolved.SourceQuote);
        }
    }

    [Theory]
    [InlineData("동일 문장. 동일 문장.", "동일 문장.", false)]
    [InlineData("A  B\r\nC", "A B C", true)]
    [InlineData("조건이 참이면 실행.", "조건이 거짓이면 실행.", false)]
    [InlineData("🙂 실행한다.", "🙂 실행한다.", true)]
    public void LegacyQuoteRequiresOneUnambiguousMatch(string prompt, string quote, bool expected) =>
        Assert.Equal(expected, NaturalRequirementEvidence.TryResolve(prompt, new("r", "동작", "behavior", "explicit", quote),
            NaturalRequirementEvidence.Prepare(prompt), out _));

    [Fact]
    public void MultipleEvidenceIdsArePreservedAndUnknownOrDuplicateIdsAreRejected()
    {
        const string prompt = "준비한다. 실행한다.";
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        var requirement = new NaturalRequirement("r", "준비 후 실행", "behavior", "explicit", "", SourceRangeIds: ranges.Select(r => r.Id).ToArray());
        Assert.True(NaturalRequirementEvidence.TryResolve(prompt, requirement, ranges, out var resolved));
        Assert.Equal(2, resolved.SourceRangeIds!.Count);
        Assert.False(NaturalRequirementEvidence.TryResolve(prompt, requirement with { SourceRangeIds = ["unknown"] }, ranges, out _));
        Assert.False(NaturalRequirementEvidence.TryResolve(prompt, requirement with { SourceRangeIds = [ranges[0].Id, ranges[0].Id] }, ranges, out _));
    }

    [Fact]
    public void SemanticScenariosCanCrossParagraphsAndShareInterlocks()
    {
        const string prompt = "요청한다.\n\n승인한다.\n문을 닫아야 한다.";
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        var requirements = new NaturalRequirements("승인 흐름", [], ranges.Select((r, i) => new NaturalRequirement($"r{i}", r.Text,
            i == 2 ? "interlock" : "behavior", "explicit", "", SourceRangeIds: [r.Id])).ToArray(),
            Scenarios: [new("request", "요청과 승인", ["r0", "r1", "r2"], ranges.Select(r => r.Id).ToArray())]);
        var attached = NaturalRequirementEvidence.Attach(prompt, requirements);
        Assert.Single(attached.Scenarios!);
        Assert.Null(NaturalDesignValidation.Plan(attached));
        Assert.Contains(NaturalRequirementEvidence.ForScenario(attached, new("part", "승인", ["r1"], [ranges[1].Id])).Requirements,
            r => r.Kind == "interlock");
    }

    [Fact]
    public async Task InvalidItemIsRepairedWithoutReplacingAcceptedItems()
    {
        var model = new Model { RepairItem = true };
        var result = await Client(model).ExtractNaturalRequirementsAsync("요청한다. 승인한다.", false, Ct);
        Assert.Equal("원래 정상 항목", result!.Requirements.Single(r => r.Id == "r1").Text);
        Assert.Equal(2, result.Requirements.Count);
        Assert.Equal(new[] { "requirements", "requirements-repair", "requirements-review" }, model.Purposes);
    }

    [Fact]
    public async Task MaskedSecretsNeverAppearInExtractionOrReviewMessages()
    {
        var model = new Model();
        var result = await Client(model).ExtractNaturalRequirementsAsync("token=very_private_value 요청한다.", false, Ct);
        Assert.NotNull(result);
        Assert.All(model.Messages, text => Assert.DoesNotContain("very_private_value", text));
        Assert.Contains("very_private_value", result!.SourceRanges![0].Text);
    }

    [Fact]
    public async Task NullJsonConsumesAtMostTwoRepairsAndResumeDoesNotResetThem()
    {
        var model = new Model { InvalidJson = true };
        var options = new LlmOptions { Enabled = true };
        IReadOnlyList<SemanticCheckpoint> checkpoints;
        using (var execution = new SemanticExecution(options, null, Ct))
        {
            var error = await Assert.ThrowsAsync<LlmClientException>(() => Client(model).ExtractNaturalRequirementsAsync("요청한다.", false, Ct));
            Assert.Equal("NATURAL_REQUIREMENTS_INVALID", error.Code);
            checkpoints = execution.Checkpoints;
        }
        Assert.Equal(3, model.Purposes.Count);
        using (var execution = new SemanticExecution(options, checkpoints, Ct))
            await Assert.ThrowsAsync<LlmClientException>(() => Client(model).ExtractNaturalRequirementsAsync("요청한다.", false, Ct));
        Assert.Equal(3, model.Purposes.Count);
    }

    [Fact]
    public async Task InterruptedReviewResumesFromSavedExtraction()
    {
        var model = new Model { InterruptReview = true };
        IReadOnlyList<SemanticCheckpoint> saved;
        using (var execution = new SemanticExecution(new LlmOptions { Enabled = true }, null, Ct))
        {
            await Assert.ThrowsAsync<LlmClientException>(() => Client(model).ExtractNaturalRequirementsAsync("요청한다.", false, Ct));
            saved = execution.Checkpoints;
        }
        model.InterruptReview = false;
        using (var execution = new SemanticExecution(new LlmOptions { Enabled = true }, saved, Ct))
            Assert.NotNull(await Client(model).ExtractNaturalRequirementsAsync("요청한다.", false, Ct));
        Assert.Equal(1, model.Purposes.Count(p => p == "requirements"));
        Assert.Equal(2, model.Purposes.Count(p => p == "requirements-review"));
    }

    [Fact]
    public async Task TruncatedExtractionSplitsAllEvidenceWithoutDroppingInput()
    {
        var model = new Model { TruncateLarge = true };
        var result = await Client(model).ExtractNaturalRequirementsAsync("요청한다. 승인한다.", false, Ct);
        Assert.Equal(2, result!.Requirements.Count);
        Assert.Equal(2, result.Requirements.SelectMany(NaturalRequirementEvidence.RangeIds).Distinct().Count());
        Assert.Equal(5, model.Purposes.Count);
    }

    [Fact]
    public async Task ClarificationIsPersistedAndNotLeasedUntilAnswered()
    {
        await using var store = new InMemoryAppStore();
        var model = new Model { Ask = true };
        using var cache = new NaturalDiagramSessionCache();
        var options = Options.Create(new LlmOptions { Enabled = true });
        var service = new NaturalDiagramService(Client(model), new(new()), store, cache, new(), options, new TestEnvironment());
        var now = DateTimeOffset.UtcNow;
        var run = new NaturalDiagramRun(Guid.NewGuid(), "alice", new("요청한다."), NaturalDiagramRunState.Queued, now, now);
        await store.CreateNaturalDiagramRunAsync(run, Ct);
        var leased = (await store.TryLeaseNaturalDiagramRunAsync(TimeSpan.FromMinutes(1), Ct))!;
        await new NaturalDiagramRunProcessor(store, service, options).ProcessAsync(leased, Ct);
        var saved = (await store.GetNaturalDiagramRunAsync(run.Id, Ct))!;
        Assert.Equal(NaturalDiagramRunState.NeedsClarification, saved.State);
        Assert.Null(saved.LeaseId);
        Assert.NotEmpty(saved.Checkpoints!);
        Assert.Single(saved.Questions!);
        Assert.Null(await store.TryLeaseNaturalDiagramRunAsync(TimeSpan.FromMinutes(1), Ct));
        Assert.Equal(1, saved.QuestionVersion);
    }

    [Fact]
    public async Task LocalSaveFailureDoesNotExposeUnpersistedSuccess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "natural-save-failure-" + Guid.NewGuid().ToString("N"));
        await using var store = new LocalFileAppStore(Path.Combine(directory, "store.json"));
        await store.InitializeAsync(Ct);
        var now = DateTimeOffset.UtcNow;
        var run = new NaturalDiagramRun(Guid.NewGuid(), "alice", new("요청한다."), NaturalDiagramRunState.Queued, now, now);
        await store.CreateNaturalDiagramRunAsync(run, Ct);
        var runFile = Directory.EnumerateFiles(directory, run.Id.ToString("N") + ".json", SearchOption.AllDirectories).Single();
        Directory.CreateDirectory(runFile + ".tmp");
        var failure = await Record.ExceptionAsync(() => store.UpdateNaturalDiagramRunAsync(run with { Revision = 2, Progress = 95 }, 1, null, Ct));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(1, (await store.GetNaturalDiagramRunAsync(run.Id, Ct))!.Revision);
    }

    private static InternalLlmClient Client(Model model) => new(Options.Create(new LlmOptions { Enabled = true }), new(), new(), model, new(model));

    private sealed class Model : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public bool RepairItem, InvalidJson, InterruptReview, TruncateLarge, Ask;
        public List<string> Purposes { get; } = [];
        public List<string> Messages { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            Purposes.Add(request.Purpose!); Messages.Add(request.UserPrompt);
            using var doc = JsonDocument.Parse(request.UserPrompt);
            var ranges = doc.RootElement.GetProperty("sourceRanges").EnumerateArray().ToArray();
            if (InterruptReview && request.Purpose == "requirements-review") throw new LlmClientException("LLM_REQUEST_TIMEOUT", "synthetic interruption");
            if (TruncateLarge && ranges.Length > 1) return Result("{}", "length");
            if (InvalidJson) return Result("null");
            object value;
            if (request.Purpose == "requirements-review") value = new NaturalRequirementsReview(true,
                ranges.Select(r => r.GetProperty("id").GetString()!).ToArray(), []);
            else
            {
                var items = ranges.Select((r, i) => new NaturalRequirement($"r{i + 1}",
                    RepairItem && i == 0 ? request.Purpose == "requirements" ? "원래 정상 항목" : "덮어쓰면 안 되는 항목" : "동작",
                    "behavior", "explicit", "", SourceRangeIds: [RepairItem && i == 1 && request.Purpose == "requirements" ? "unknown" : r.GetProperty("id").GetString()!])).ToArray();
                value = new NaturalRequirements("설계", [], items,
                    Scenarios: [new("s1", "흐름", items.Select(item => item.Id).ToArray(), ranges.Select(r => r.GetProperty("id").GetString()!).ToArray())], Questions: Ask
                    ? [new("q1", "어떤 승인 방식을 사용합니까?", "흐름이 달라집니다.", [ranges[0].GetProperty("id").GetString()!], ["자동", "수동"])] : []);
            }
            return Result(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        private static Task<VllmCompletionResult> Result(string text, string finish = "stop") =>
            Task.FromResult(new VllmCompletionResult(text, finish, 1, true, false, 0, 5000, 100, 100, 200));
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
