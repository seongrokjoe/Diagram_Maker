using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class CodexCliCompletionTransportTests
{
    private static JsonElement Schema => JsonSerializer.SerializeToElement(new
    { type = "object", properties = new { result = new { type = "string" } }, required = new[] { "result" } });
    private static string Events(string content = "{\"result\":\"ok\"}") =>
        JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = content } }) + "\n" +
        "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}";

    [Fact]
    public void SchemaCopyIsStrictWithoutChangingCorporateContract()
    {
        using var original = JsonDocument.Parse("""{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"value":{"type":"string"}}}}}}""");
        var normalized = CodexCliCompletionTransport.NormalizeSchema(original.RootElement);
        Assert.False(normalized["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("items", normalized["required"]![0]!.GetValue<string>());
        Assert.False(normalized["properties"]!["items"]!["items"]!["additionalProperties"]!.GetValue<bool>());
        Assert.False(original.RootElement.TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public async Task CompletionPreservesKoreanInstructionsOnlyInStdinAndCleansSchema()
    {
        var runner = new FakeRunner();
        using var transport = Transport(runner);
        var result = await transport.CompleteAsync(new("한국어 지시 \"인용\"", "샘플 $() `본문`", 100, false, Schema), default);
        Assert.Equal("{\"result\":\"ok\"}", result.Content);
        Assert.Equal(14, result.TotalTokens);
        Assert.True(result.StructuredOutputApplied);
        Assert.Contains("instructions", runner.Input!);
        var received = runner.Input!;
        using var envelope = JsonDocument.Parse(received[received.IndexOf('{')..]);
        Assert.Equal("한국어 지시 \"인용\"", envelope.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("샘플 $() `본문`", envelope.RootElement.GetProperty("input").GetString());
        Assert.DoesNotContain(runner.Arguments!, argument => argument.Contains("샘플") || argument.Contains("본문"));
        Assert.False(Directory.Exists(runner.Directory));
        Assert.Equal(1, runner.Completions);
    }

    [Theory]
    [InlineData("{\"type\":\"turn.failed\",\"error\":{\"message\":\"401 unauthorized\"}}", "CODEX_LOGIN_REQUIRED")]
    [InlineData("{\"type\":\"error\",\"message\":\"429 quota\"}", "CODEX_RATE_LIMIT")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\"}}", "CODEX_UNEXPECTED_TOOL")]
    [InlineData("{\"type\":\"turn.completed\"}", "CODEX_RESPONSE_INVALID")]
    public void InvalidEventsNeverBecomeSuccessfulDiagrams(string output, string code)
    {
        Assert.Equal(code, Assert.Throws<LlmClientException>(() => CodexCliCompletionTransport.ParseEvents(output, true)).Code);
    }

    [Theory]
    [InlineData("command_execution", "command_execution")]
    [InlineData("private-value-123", "unknown")]
    public void UnexpectedToolDiagnosticsExposeOnlyTheEventKind(string kind, string reportedKind)
    {
        var events = JsonSerializer.Serialize(new { type = "item.started", item = new
        { type = kind, command = "PRIVATE_TOOL_ARGUMENTS", output = "PRIVATE_TOOL_OUTPUT" } });
        var error = Assert.Throws<LlmClientException>(() => CodexCliCompletionTransport.ParseEvents(events, true));
        Assert.Equal("CODEX_UNEXPECTED_TOOL", error.Code);
        Assert.Contains($"({reportedKind})", error.Message);
        Assert.DoesNotContain("PRIVATE_", error.Message);
        Assert.DoesNotContain("private-value", error.Message);
    }

    [Fact]
    public void OnlyFinalMessageIsUsedAndMalformedJsonIsRejected()
    {
        var events = "{\"type\":\"item.completed\",\"item\":{\"type\":\"reasoning\",\"text\":\"not a result\"}}\n" + Events();
        Assert.Equal("{\"result\":\"ok\"}", CodexCliCompletionTransport.ParseEvents(events, true).Content);
        Assert.Equal("CODEX_RESPONSE_INVALID", Assert.Throws<LlmClientException>(() => CodexCliCompletionTransport.ParseEvents(Events("not JSON"), true)).Code);
    }

    [Fact]
    public void NonfatalCliWarningItemsDoNotDiscardACompletedResponse()
    {
        const string warning = """{"type":"item.completed","item":{"type":"error","message":"Code mode host is disabled. PRIVATE_DIAGNOSTIC_PATH"}}""";
        var parsed = CodexCliCompletionTransport.ParseEvents(warning + "\n" + Events(), true);
        Assert.Equal("{\"result\":\"ok\"}", parsed.Content);
        Assert.Equal(10, parsed.InputTokens);
        Assert.Equal("CODEX_RESPONSE_INVALID", Assert.Throws<LlmClientException>(() =>
            CodexCliCompletionTransport.ParseEvents(warning, true)).Code);
        Assert.Equal("CODEX_RATE_LIMIT", Assert.Throws<LlmClientException>(() =>
            CodexCliCompletionTransport.ParseEvents(warning + "\n" + Events() +
                "\n{\"type\":\"turn.failed\",\"error\":{\"message\":\"429 quota\"}}", true)).Code);
    }

    [Fact]
    public void ChildArgumentsDisableExecutionAndDoNotBypassManagedRules()
    {
        var args = CodexCliCompletionTransport.BuildArguments("C:/sample work", true, "test-model");
        Assert.Contains("read-only", args);
        Assert.Contains("--ignore-user-config", args);
        Assert.Contains("mcp_servers={}", args);
        Assert.Contains("web_search=\"disabled\"", args);
        Assert.Contains("skip_host_skill_discovery", args);
        Assert.DoesNotContain(args, arg => arg.Contains("dangerously") || arg == "--ignore-rules");
        foreach (var feature in CodexCliCompletionTransport.DisabledFeatures) Assert.Contains(feature, args);
    }

    [Fact]
    public async Task MissingLoginStopsBeforeAnyInferenceAndRemainsBlockedForSession()
    {
        var runner = new FakeRunner { LoggedIn = false };
        using var transport = Transport(runner);
        for (var i = 0; i < 2; i++)
            Assert.Equal("CODEX_LOGIN_REQUIRED", (await Assert.ThrowsAsync<LlmClientException>(() =>
                transport.CompleteAsync(new("", "synthetic", 10, false), default))).Code);
        Assert.Equal(0, runner.Completions);
    }

    [Fact]
    public async Task TimeoutAndCancellationCleanRequestDirectories()
    {
        var runner = new FakeRunner { Delay = true };
        using var transport = Transport(runner, 1);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => transport.CompleteAsync(new("", "synthetic", 10, false, Schema), default));
        Assert.Equal("CODEX_TIMEOUT", error.Code);
        Assert.False(Directory.Exists(runner.Directory));
        using var source = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.CompleteAsync(new("", "synthetic", 10, false), source.Token));
    }

    [Fact]
    public async Task ConcurrentRequestsAreSerialized()
    {
        var runner = new FakeRunner { ShortDelay = true };
        using var transport = Transport(runner);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => transport.CompleteAsync(new("", "synthetic", 10, false, Schema), default)));
        Assert.Equal(1, runner.Peak);
        Assert.Equal(3, runner.Completions);
    }

    private static CodexCliCompletionTransport Transport(FakeRunner runner, int timeout = 10) => new(
        new CodexTestOptions { Enabled = true, ExecutablePath = "unused-native-test-double", RequestTimeoutSeconds = timeout }, new LlmOptions(), runner);

    private sealed class FakeRunner : ICodexProcessRunner
    {
        public bool LoggedIn { get; init; } = true;
        public bool Delay { get; init; }
        public bool ShortDelay { get; init; }
        public int Completions { get; private set; }
        public int Peak { get; private set; }
        private int _active;
        public string? Input { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string? Directory { get; private set; }
        public async Task<CodexProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, string? input, CancellationToken token)
        {
            if (arguments.Contains("--help")) return new(0, "--output-schema --ignore-user-config --ephemeral", "");
            if (arguments[0] == "features") return new(0, string.Join('\n', CodexCliCompletionTransport.DisabledFeatures.Append("skip_host_skill_discovery").Select(feature => feature + " stable false")), "");
            if (arguments[0] == "login") return new(LoggedIn ? 0 : 1, "", LoggedIn ? "Logged in using ChatGPT" : "Not logged in");
            Input = input; Arguments = arguments; Directory = directory; Completions++;
            Peak = Math.Max(Peak, Interlocked.Increment(ref _active));
            try
            {
                if (Delay) await Task.Delay(Timeout.Infinite, token);
                if (ShortDelay) await Task.Delay(40, token);
                return new(0, Events(), "");
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
