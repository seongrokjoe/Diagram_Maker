using System.Text.Json;

namespace DiagramMaker.Services;

// A scope owns its budget across candidates, nested requests, splits and resumes.
// Charges/observations are idempotent because a resumed caller replays cached work.
public sealed class DiagramRecoveryBudget
{
    private readonly string key;
    private readonly SemanticExecution? execution;
    private readonly RecoveryState state;

    public DiagramRecoveryBudget(string key)
    {
        this.key = key;
        execution = SemanticExecution.Current;
        state = execution?.ReadRecovery<RecoveryState>(key) ?? new();
    }

    public int FormatUsed => state.Charges.Count(p => p.Value == "format");
    public int ContentUsed => state.Charges.Count(p => p.Value == "content");

    public async Task ChargeAsync(string kind, string operation)
    {
        execution?.Token.ThrowIfCancellationRequested();
        var id = SemanticExecution.Hash(kind + operation);
        if (state.Charges.ContainsKey(id)) return;
        if ((kind == "format" ? FormatUsed : ContentUsed) >= DiagramRecoveryPolicy.MaximumRepairs)
            throw new LlmClientException("LLM_REPAIR_EXHAUSTED", "The unit's shared repair budget is exhausted.",
                failureKind: kind == "format" ? "FormatRepairBudgetExhausted" : "ContentRepairBudgetExhausted");
        state.Charges[id] = kind;
        await Save();
    }

    public async Task<bool> ObserveAsync(string operation, string candidate, IEnumerable<string> issues, bool correction)
    {
        var id = SemanticExecution.Hash(operation);
        if (state.Observations.TryGetValue(id, out var saved)) return saved;
        var signature = string.Join("\n", issues.Distinct().Order(StringComparer.Ordinal));
        var hash = SemanticExecution.Hash(Canonical(candidate) + signature);
        var progress = state.Candidates.Add(hash);
        state.Repeats = signature == state.LastIssues && correction ? state.Repeats + 1 : 0;
        state.LastIssues = signature;
        progress &= state.Repeats < 3;
        state.Observations[id] = progress;
        await Save();
        return progress;
    }

    private Task Save() => execution?.SaveRecoveryAsync(key, state) ?? Task.CompletedTask;
    private static string Canonical(string value)
    {
        try { return JsonSerializer.Serialize(Order(JsonSerializer.Deserialize<JsonElement>(value))); }
        catch (JsonException) { return value.Trim(); }

        static object? Order(JsonElement item) => item.ValueKind switch {
            JsonValueKind.Object => item.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => Order(p.Value)),
            JsonValueKind.Array => item.EnumerateArray().Select(Order).ToArray(),
            _ => item
        };
    }

    internal sealed class RecoveryState
    {
        public Dictionary<string, string> Charges { get; set; } = [];
        public Dictionary<string, bool> Observations { get; set; } = [];
        public HashSet<string> Candidates { get; set; } = [];
        public string? LastIssues { get; set; }
        public int Repeats { get; set; }
    }
}
