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
    private int reused;
    private int completed;
    public string Stage { get; private set; } = "preparing";
    public string UnitId { get; private set; } = "";
    public int BudgetSeconds { get; }
    public Guid? LeaseId { get; init; }
    public CancellationToken Token => budget.Token;
    public bool BudgetExpired => budget.IsCancellationRequested && !parent.IsCancellationRequested;
    public IReadOnlyList<SemanticCheckpoint> Checkpoints => checkpoints.ToArray();
    public IReadOnlyList<LlmDiagnostic> Diagnostics => diagnostics.ToArray();
    public SemanticProgress Progress => new(Stage, UnitId, completed, reused, diagnostics.Count, (long)watch.Elapsed.TotalSeconds, BudgetSeconds);

    public SemanticExecution(LlmOptions options, IReadOnlyList<SemanticCheckpoint>? saved, CancellationToken cancellationToken, Func<Task>? onProgress = null,
        IReadOnlyList<LlmDiagnostic>? savedDiagnostics = null)
    {
        previous = Slot.Value; Slot.Value = this;
        parent = cancellationToken; persist = onProgress;
        BudgetSeconds = options.SemanticJobBudgetSeconds;
        budget = CancellationTokenSource.CreateLinkedTokenSource(parent);
        budget.CancelAfter(TimeSpan.FromSeconds(BudgetSeconds));
        checkpoints = (saved ?? []).ToList(); diagnostics = (savedDiagnostics ?? []).ToList();
        fingerprint = Hash(JsonSerializer.Serialize(options) + InternalLlmClient.SemanticPromptVersion + InternalLlmClient.CodeBlockPromptVersion);
    }

    public static async Task<T?> RunAsync<T>(string stage, string key, Func<Task<T?>> work, Func<T, bool> accepted) where T : class
    {
        var current = Current;
        if (current is null) return await work();
        current.Token.ThrowIfCancellationRequested();
        var oldStage = current.Stage; var oldUnit = current.UnitId;
        var unitKey = Hash(current.fingerprint + stage + key);
        current.Stage = stage; current.UnitId = unitKey[..16];
        try
        {
            var cached = current.checkpoints.FirstOrDefault(c => c.Key == unitKey);
            if (cached is not null)
            {
                try
                {
                    var value = JsonSerializer.Deserialize<T>(cached.ValueJson);
                    if (value is not null && accepted(value)) { current.reused++; await current.NotifyAsync(); return value; }
                }
                catch (JsonException) { /* Old or incomplete checkpoints are recomputed. */ }
            }
            await current.NotifyAsync();
            var result = await work();
            if (result is not null && accepted(result))
            {
                current.checkpoints.RemoveAll(c => c.Key == unitKey);
                current.checkpoints.Add(new(unitKey, stage, JsonSerializer.Serialize(result)));
                current.completed++; await current.NotifyAsync();
            }
            return result;
        }
        finally { current.Stage = oldStage; current.UnitId = oldUnit; }
    }

    public async Task RecordAsync(LlmDiagnostic record)
    {
        var index = diagnostics.FindIndex(d => d.Id == record.Id);
        if (index >= 0) diagnostics[index] = record; else diagnostics.Add(record);
        await NotifyAsync();
    }
    public Task NotifyAsync() => persist?.Invoke() ?? Task.CompletedTask;
    public void Dispose() { Slot.Value = previous; budget.Dispose(); }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal static class LlmFailure
{
    public static string Describe(Exception exception) => exception switch
    {
        LlmClientException { Code: "LLM_DISABLED" } => "LLM이 비활성화되어 있습니다. LLM 설정을 확인하세요.",
        LlmClientException { Code: "LLM_RESPONSE_TRUNCATED" } e => $"LLM 출력이 토큰 한도에서 잘렸습니다 (요청 {e.RequestedMaxOutputTokens?.ToString() ?? "미확인"}, 사용 {e.CompletionTokens?.ToString() ?? "미확인"}). 더 작은 단위로 다시 생성하세요.",
        LlmClientException { Code: "LLM_CONTEXT_LIMIT" or "LLM_INPUT_LIMIT" } => "LLM 입력과 출력 예약이 문맥 한도를 초과했습니다. 문맥/토큰 설정을 확인하세요.",
        LlmClientException { Code: "LLM_REQUEST_TIMEOUT" or "LLM_NO_RESPONSE_TIMEOUT" } => "LLM 응답 시간 제한에 도달했습니다. 서버 대기 상태와 작업 진단을 확인하세요.",
        LlmClientException { Code: "LLM_SCHEMA_INVALID" } e => $"LLM 응답 계약 또는 코드 근거 검증 실패 ({e.FailureKind ?? e.Code}). 작업 진단에서 실패 단계를 확인하세요.",
        LlmClientException e => $"LLM 요청 실패 ({e.Code}). 작업 진단에서 서버 응답과 전송 여부를 확인하세요.",
        DiagramGenerationException e => e.Message,
        _ => "의미 검증을 완료하지 못했습니다. 작업 진단을 확인하세요."
    };
}
