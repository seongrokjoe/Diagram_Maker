using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed class SemanticExecution : IDisposable
{
    private static readonly AsyncLocal<SemanticExecution?> Slot = new();
    public static SemanticExecution? Current => Slot.Value;
    private readonly SemanticExecution? previous;
    private readonly CancellationTokenSource budget;
    private readonly CancellationToken parent;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly string fingerprint;
    private readonly Func<Task>? persist;
    private readonly List<SemanticCheckpoint> checkpoints;
    private readonly List<LlmDiagnostic> diagnostics;
    private readonly HashSet<string> reused = [];
    private readonly HashSet<string> completedThisAttempt = [];
    private readonly List<HashSet<string>> activeDependencies = [];
    private readonly int initialRequests;
    private readonly long previousElapsed;
    private readonly int attemptNumber;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private (string Group, string? Parent, int Attempt, IReadOnlyList<string> Ancestors)? requestScope;
    private readonly bool protocolUpgraded;
    public string Stage { get; private set; } = "preparing";
    public string UnitId { get; private set; } = "";
    public int BudgetSeconds { get; }
    public Guid? LeaseId { get; init; }
    public CancellationToken Token => budget.Token;
    public bool BudgetExpired => budget.IsCancellationRequested && !parent.IsCancellationRequested;
    public LlmClientException? RequestFailure { get; private set; }
    public void StopRequests(LlmClientException error) => RequestFailure ??= error;
    internal const string SharedPolicyVersion = "shared-requests-v7";
    private DateTimeOffset lastProgressAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, SemanticCoverage> coverage = new();
    private Func<SharedDiagramGroup, Task>? sharedProgress;
    public IDisposable BeginSharedProjection(Func<SharedDiagramGroup, Task> callback)
    {
        var before = sharedProgress;
        sharedProgress = callback;
        return new RequestScope(() => sharedProgress = before);
    }
    public Task ReportSharedProjectionAsync(SharedDiagramGroup value) => sharedProgress?.Invoke(value) ?? Task.CompletedTask;
    public async Task ReportCoverageAsync(string key, SemanticCoverage value)
    {
        coverage[key] = value;
        await NotifyAsync();
    }
    internal static string PolicyFingerprint(LlmOptions options) =>
        Hash(JsonSerializer.Serialize(options) + InternalLlmClient.SemanticPromptVersion + InternalLlmClient.CodeBlockPromptVersion + SharedPolicyVersion);
    public IReadOnlyList<SemanticCheckpoint> Checkpoints => checkpoints.ToArray();
    public IReadOnlyList<LlmDiagnostic> Diagnostics => diagnostics.ToArray();
    // Page wrappers and their constituent completion checkpoints describe the same
    // work; do not count both as independent completed units.
    private static bool Counted(SemanticCheckpoint value) => value.Stage is not ("code-page" or "git-page" or "execution-meaning") &&
        !value.Stage.StartsWith("shared-", StringComparison.Ordinal);
    public SemanticProgress Progress => new(Stage, UnitId,
        checkpoints.Count(c => Counted(c) && c.State == "Completed"), reused.Count, diagnostics.Count,
        (long)watch.Elapsed.TotalSeconds, BudgetSeconds, checkpoints.Count(Counted),
        completedThisAttempt.Count, diagnostics.Count - initialRequests,
        previousElapsed + (long)watch.Elapsed.TotalSeconds, attemptNumber, startedAt,
        diagnostics.Sum(d => d.TransportAttempts > 0 ? d.TransportAttempts : d.Sent ? 1 + d.Retries : 0),
        diagnostics.Sum(d => d.TokenizationRequests), checkpoints.Count(c => Counted(c) && c.State == "Failed"),
        diagnostics.Count(d => !d.Sent && d.State == "Failed"),
        diagnostics.Skip(initialRequests).Sum(d => d.TransportAttempts > 0 ? d.TransportAttempts : d.Sent ? 1 + d.Retries : 0),
        diagnostics.Skip(initialRequests).Sum(d => d.TokenizationRequests),
        diagnostics.Sum(d => d.ElapsedMilliseconds), diagnostics.Skip(initialRequests).Sum(d => d.ElapsedMilliseconds),
        diagnostics.LastOrDefault(d => d.State == "Running")?.StartedAt,
        diagnostics.Where(d => d.ErrorCode is not null).Take(1)
            .Concat(diagnostics.Where(d => d.ErrorCode is not null && d.RecoveryState != "Recovered").TakeLast(3))
            .Concat(diagnostics.Where(d => d.ErrorCode is not null).TakeLast(3)).DistinctBy(d => d.Id).ToArray(),
        diagnostics.LastOrDefault(), protocolUpgraded, lastProgressAt,
        coverage.Count == 0 ? null : new(coverage.Values.Sum(c => c.TotalUnits), coverage.Values.Sum(c => c.VerifiedUnits),
            coverage.Values.Sum(c => c.PendingUnits), coverage.Values.Sum(c => c.FailedUnits)));

    public SemanticExecution(LlmOptions options, IReadOnlyList<SemanticCheckpoint>? saved, CancellationToken cancellationToken, Func<Task>? onProgress = null,
        IReadOnlyList<LlmDiagnostic>? savedDiagnostics = null, SemanticProgress? savedProgress = null)
    {
        previous = Slot.Value; Slot.Value = this;
        parent = cancellationToken; persist = onProgress;
        BudgetSeconds = options.SemanticJobBudgetSeconds;
        budget = CancellationTokenSource.CreateLinkedTokenSource(parent);
        budget.CancelAfter(TimeSpan.FromSeconds(BudgetSeconds));
        checkpoints = (saved ?? []).ToList();
        protocolUpgraded = savedProgress?.ProtocolUpgraded == true ||
            checkpoints.Any(c => c.Stage.StartsWith("shared-", StringComparison.Ordinal)) &&
            !(savedDiagnostics ?? []).Any(d => d.ProtocolVersion == SharedPolicyVersion);
        diagnostics = (savedDiagnostics ?? []).Select(d => d.State is "Running" or "Preparing"
            ? d with { State = "Interrupted", ErrorCode = "PROCESS_INTERRUPTED" } : d).ToList();
        initialRequests = diagnostics.Count;
        previousElapsed = savedProgress?.TotalElapsedSeconds is > 0 ? savedProgress.TotalElapsedSeconds : savedProgress?.ElapsedSeconds ?? 0;
        attemptNumber = savedProgress is null ? 1 : savedProgress.AttemptNumber + 1;
        fingerprint = PolicyFingerprint(options);
    }

    public static async Task<T?> RunAsync<T>(string stage, string key, Func<Task<T?>> work, Func<T, bool> accepted) where T : class
    {
        var current = Current;
        if (current is null) return await work();
        current.Token.ThrowIfCancellationRequested();
        var oldStage = current.Stage; var oldUnit = current.UnitId;
        var unitKey = Hash(current.fingerprint + stage + key);
        foreach (var parent in current.activeDependencies) parent.Add(unitKey);
        var dependencies = new HashSet<string>();
        current.activeDependencies.Add(dependencies);
        current.Stage = stage; current.UnitId = unitKey[..16];
        try
        {
            var cached = current.checkpoints.FirstOrDefault(c => c.Key == unitKey);
            if (cached is not null)
            {
                try
                {
                    var value = JsonSerializer.Deserialize<T>(cached.ValueJson);
                    if (value is not null && (cached.State == "Failed" || accepted(value)))
                    {
                        if (cached.State == "Completed" && Counted(cached)) current.reused.Add(unitKey);
                        foreach (var dependency in cached.Dependencies ?? [])
                            if (current.checkpoints.Any(c => c.Key == dependency && c.State == "Completed" && Counted(c)))
                                current.reused.Add(dependency);
                        foreach (var scope in current.activeDependencies) scope.UnionWith(cached.Dependencies ?? []);
                        await current.NotifyAsync(); return value;
                    }
                }
                catch (JsonException) { /* Old or incomplete checkpoints are recomputed. */ }
                if (cached.State == "Failed" && cached.ErrorCode is { } errorCode)
                    throw new LlmClientException(errorCode, "이 단위는 이전 실행에서 실패했습니다. 완료 결과를 보존하고 실패 범위를 다시 생성하세요.",
                        failureKind: cached.FailureKind, rejectedContent: cached.RejectedContent, validationDetails: cached.ValidationDetails);
            }
            current.checkpoints.RemoveAll(c => c.Key == unitKey);
            current.checkpoints.Add(new(unitKey, stage, "null", "Pending"));
            await current.NotifyAsync();
            var result = await work();
            if (result is not null && current.RequestFailure is null)
            {
                var success = accepted(result);
                var wasSplit = current.checkpoints.Any(c => c.Key == unitKey && c.WasSplit);
                current.checkpoints.RemoveAll(c => c.Key == unitKey);
                var checkpoint = new SemanticCheckpoint(unitKey, stage, JsonSerializer.Serialize(result), success ? "Completed" : "Failed",
                    Dependencies: dependencies.ToArray(), WasSplit: wasSplit);
                current.checkpoints.Add(checkpoint);
                if (success && Counted(checkpoint)) current.completedThisAttempt.Add(unitKey);
                await current.NotifyAsync();
            }
            return result;
        }
        catch (LlmClientException error)
        {
            // Operational interruptions remain retryable. Replaying an invalid
            // semantic response on every resume cannot make forward progress.
            if (error.Code is "LLM_SCHEMA_INVALID" or "LLM_RESPONSE_TRUNCATED" or "LLM_INPUT_LIMIT" or "LLM_CONTEXT_LIMIT" or "LLM_INPUT_CHARACTERS" or "LLM_OUTPUT_BUDGET")
            {
                current.checkpoints.RemoveAll(c => c.Key == unitKey);
                current.checkpoints.Add(new(unitKey, stage, "null", "Failed", error.Code, error.FailureKind, error.RejectedContent,
                    ValidationDetails: error.ValidationDetails));
                await current.NotifyAsync();
            }
            throw;
        }
        finally { current.activeDependencies.Remove(dependencies); current.Stage = oldStage; current.UnitId = oldUnit; }
    }

    public async Task RecordAsync(LlmDiagnostic record)
    {
        if (record.ProtocolVersion is null && requestScope is { } scope)
            record = record with { ProtocolVersion = SharedPolicyVersion, RecoveryGroupId = scope.Group,
                ParentGroupId = scope.Parent, Attempt = scope.Attempt, AncestorGroupIds = scope.Ancestors };
        if (record.ProtocolVersion is not null && record.ErrorCode is not null && record.RecoveryState is null)
            record = record with { RecoveryState = "Retrying" };
        var index = diagnostics.FindIndex(d => d.Id == record.Id);
        if (index >= 0) diagnostics[index] = record; else diagnostics.Add(record);
        await NotifyAsync();
    }
    public IDisposable BeginRequestScope(string group, string? parentGroup, int attempt)
    {
        var before = requestScope;
        // Preflight splits have no HTTP diagnostic of their own. Persist the
        // complete ancestry on requests so recovery still reaches those leaves after resume.
        var ancestors = parentGroup is null ? [] : new[] { parentGroup }
            .Concat(before is { } enclosing && (enclosing.Group == parentGroup || enclosing.Group == group)
                ? enclosing.Ancestors : []).Distinct(StringComparer.Ordinal).ToArray();
        requestScope = (group, parentGroup, attempt, ancestors);
        return new RequestScope(() => requestScope = before);
    }
    public async Task SetRecoveryAsync(string group, string state, bool descendants = false, bool protocolOnly = false)
    {
        var groups = new HashSet<string> { group };
        if (descendants)
        {
            int before;
            do
            {
                before = groups.Count;
                foreach (var record in diagnostics.Where(d => d.ParentGroupId is not null && groups.Contains(d.ParentGroupId)))
                    if (record.RecoveryGroupId is not null) groups.Add(record.RecoveryGroupId);
            } while (groups.Count > before);
        }
        for (var i = 0; i < diagnostics.Count; i++)
        {
            var record = diagnostics[i];
            if (record.ErrorCode is not null && record.RecoveryGroupId is not null &&
                (groups.Contains(record.RecoveryGroupId) || descendants && record.AncestorGroupIds?.Contains(group) == true) &&
                record.RecoveryState != "Recovered" && (!protocolOnly || record.ValidationCode != "SemanticReviewRejected"))
                diagnostics[i] = record with { RecoveryState = state };
        }
        await NotifyAsync();
    }
    private sealed class RequestScope(Action restore) : IDisposable { public void Dispose() => restore(); }
    public async Task MarkSplitAsync()
    {
        var index = checkpoints.FindIndex(c => c.Key.StartsWith(UnitId, StringComparison.Ordinal));
        if (UnitId.Length == 0 || index < 0) return;
        checkpoints[index] = checkpoints[index] with { State = "Split", WasSplit = true };
        await NotifyAsync();
    }
    public Task NotifyAsync()
    {
        lastProgressAt = DateTimeOffset.UtcNow;
        return persist?.Invoke() ?? Task.CompletedTask;
    }
    public void Dispose() { Slot.Value = previous; budget.Dispose(); }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal static class LlmFailure
{
    public static bool StopsRequests(LlmClientException error) => error.Code.StartsWith("LLM_HTTP_", StringComparison.Ordinal) ||
        error.Code is "LLM_DISABLED" or "LLM_REDIRECT_BLOCKED" or "LLM_OUTPUT_LIMIT" or "LLM_REQUEST_INVALID" or "LLM_TRANSPORT" or "LLM_REQUEST_TIMEOUT" or "LLM_NO_RESPONSE_TIMEOUT";
    public static bool CanResume(IReadOnlyList<SemanticCheckpoint>? checkpoints, string? stopReason) =>
        stopReason is "budget" or "user-cancelled" || checkpoints?.Any(c => c.State is "Pending" or "Split") == true;
    public static string Describe(Exception exception) => exception switch
    {
        LlmClientException { Code: "LLM_DISABLED" } => "LLM이 비활성화되어 있습니다. LLM 설정을 확인하세요.",
        LlmClientException { Code: "LLM_RESPONSE_TRUNCATED" } e => $"LLM 출력이 토큰 한도에서 잘렸습니다 (요청 {e.RequestedMaxOutputTokens?.ToString() ?? "미확인"}, 사용 {e.CompletionTokens?.ToString() ?? "미확인"}). 더 작은 단위로 다시 생성하세요.",
        LlmClientException { Code: "LLM_CONTEXT_LIMIT" or "LLM_INPUT_LIMIT" } => "LLM 입력과 출력 예약이 문맥 한도를 초과했습니다. 문맥/토큰 설정을 확인하세요.",
        LlmClientException { Code: "LLM_INPUT_CHARACTERS" } => "요청의 문자 수 한도를 초과했습니다. 분할 가능한 근거를 나누고 완료된 결과를 보존합니다.",
        LlmClientException { Code: "LLM_REQUEST_TIMEOUT" or "LLM_NO_RESPONSE_TIMEOUT" } => "LLM 응답 시간 제한에 도달했습니다. 서버 대기 상태와 작업 진단을 확인하세요.",
        LlmClientException { Code: "LLM_SCHEMA_INVALID" } e => $"LLM 응답 계약 또는 코드 근거 검증 실패 ({e.FailureKind ?? e.Code}). 작업 진단에서 실패 단계를 확인하세요.",
        LlmClientException { ServerErrorCategory: "schema-constraint" or "output-field" or "template" } => "서버가 요청 계약을 지원하지 않습니다. 구조화 출력·채팅 템플릿 설정을 확인하세요.",
        LlmClientException { ServerErrorCategory: "authentication" } => "서버 접근 권한을 확인하세요.",
        LlmClientException { ServerErrorCategory: "model" } => "설정한 모델이 서버에서 제공되는지 확인하세요.",
        LlmClientException { ServerErrorCategory: "output-limit" } => "서버의 최대 출력 토큰 설정을 확인하세요.",
        LlmClientException e => $"LLM 요청 실패 ({e.Code}). 작업 진단에서 서버 응답과 전송 여부를 확인하세요.",
        DiagramGenerationException e => e.Message,
        _ => "의미 검증을 완료하지 못했습니다. 작업 진단을 확인하세요."
    };
}
