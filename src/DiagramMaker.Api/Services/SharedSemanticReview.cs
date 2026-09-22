using System.Text;
using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record SharedSemanticReview(IReadOnlyList<SharedItemReview> Items);
internal sealed record SharedItemReview(string Id, IReadOnlyList<string> Issues, IReadOnlyList<SharedReviewFinding>? Findings = null);
internal sealed record SharedReviewFinding(string Field, string Code, string Instruction, IReadOnlyList<string> EvidenceIds);
internal sealed record GroundedSharedItemReview(string Id, IReadOnlyList<SharedReviewFinding> Findings);
internal sealed record GroundedSharedReview(IReadOnlyList<GroundedSharedItemReview> Items);

internal static class SharedReviewValidation
{
    public static JsonElement GroundedSchema(int count) => JsonSerializer.SerializeToElement(new {
        type = "object", additionalProperties = false, required = new[] { "items" },
        properties = new { items = new { type = "array", minItems = count, maxItems = count,
            items = new { type = "object", additionalProperties = false, required = new[] { "id", "findings" },
                properties = new { id = new { type = "string" }, findings = new { type = "array", maxItems = 3,
                    items = new { type = "object", additionalProperties = false,
                        required = new[] { "field", "code", "instruction", "evidenceIds" }, properties = new {
                            field = new { type = "string", @enum = new[] { "summary", "description" } },
                            code = new { type = "string", @enum = Codes }, instruction = new { type = "string", maxLength = 300 },
                            evidenceIds = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string" } }
                        } } } } } } }
    });

    public static SharedValidationProblem? Check(GroundedSharedReview value, IReadOnlyList<SharedSemanticItem> items)
    {
        var expected = items.Select(i => i.Id).ToHashSet();
        if (value.Items is null || value.Items.Any(i => i is null || i.Findings is null || i.Findings.Count > 3 || i.Findings.Any(f => f is null)))
            return new("SharedReviewFieldsInvalid", new(expected.Count, value.Items?.Count ?? 0));
        var legacy = new SharedSemanticReview(value.Items.Select(i => new SharedItemReview(i.Id, i.Findings.Select(f => f.Code).Distinct().ToArray())).ToArray());
        if (Check(legacy, expected) is { } membership) return membership;
        foreach (var item in value.Items)
        {
            var allowed = items.Single(i => i.Id == item.Id).FactIds.Append(item.Id).ToHashSet();
            foreach (var finding in item.Findings)
            {
                if (finding is null || finding.Field is not ("summary" or "description") || string.IsNullOrWhiteSpace(finding.Instruction) ||
                    finding.EvidenceIds is not { Count: > 0 and <= 6 })
                    return new("SharedReviewIssuesInvalid", new(expected.Count, value.Items.Count, Field: "items.findings"));
                if (finding.EvidenceIds.Any(id => !allowed.Contains(id)))
                    return new("SharedReviewEvidenceUnknown", new(expected.Count, value.Items.Count, Field: "items.findings.evidenceIds"));
            }
        }
        return null;
    }
    internal static readonly string[] Aliases = ["M", "O", "C", "I", "R", "S", "G", "E"];
    internal static readonly string[] Codes = ["missing_action", "incorrect_outcome", "reversed_condition",
        "invented_call", "unsupported_role", "mixed_scope", "incorrect_change", "insufficient_evidence"];

    public static JsonElement Schema(int count) => JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, required = new[] { "items" },
        properties = new { items = new { type = "array", minItems = count, maxItems = count,
            items = new { type = "object", additionalProperties = false, required = new[] { "id", "issues" },
                properties = new { id = new { type = "string" }, issues = new { type = "array", maxItems = 3,
                    items = new { type = "string", @enum = Aliases } } } } } }
    });

    // Deserialization alone accepts omitted fields and additional properties.
    // Enforce the wire contract even when the server uses plain JSON prompting.
    public static string? CheckJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return "SharedReviewFieldsInvalid";
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array ||
                issues.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String))
                return "SharedReviewFieldsInvalid";
        }
        return null;
    }

    public static SharedValidationProblem? Check(SharedSemanticReview value, IReadOnlySet<string> expected)
    {
        var returned = (value.Items ?? []).Where(i => i is not null && !string.IsNullOrEmpty(i.Id)).Select(i => i.Id).ToArray();
        var details = new LlmValidationDetails(expected.Count, value.Items?.Count ?? 0,
            expected.Except(returned).Count(), returned.Length - returned.Distinct().Count(), returned.Count(id => !expected.Contains(id)));
        if (value.Items is null || value.Items.Any(i => i is null || string.IsNullOrEmpty(i.Id))) return new("SharedReviewFieldsInvalid", details);
        if (details.UnknownItems > 0) return new("SharedReviewUnknownIds", details);
        if (details.DuplicateItems > 0) return new("SharedReviewDuplicateIds", details);
        if (details.MissingItems > 0) return new("SharedReviewMissingIds", details);
        if (value.Items.Any(i => i.Issues is null || i.Issues.Count > 3 ||
                i.Issues.Select(Expand).Distinct().Count() != i.Issues.Count || i.Issues.Any(code => !Codes.Contains(Expand(code)))))
            return new("SharedReviewIssuesInvalid", details);
        return null;
    }

    public static int OutputBudget(string prompt, IReadOnlySet<string> ids)
    {
        var aliases = new PromptIds(ids);
        aliases.Encode(prompt);
        var maximum = new GroundedSharedReview(ids.Select(id => new GroundedSharedItemReview(id, [])).ToArray());
        // One token per UTF-8 byte is deliberately conservative; actual serving
        // tokenizers vary. Reserve room for the compact response's framing.
        // Reserve a bounded set of concrete corrections. If actual rejection
        // detail is larger, split the review using the same recovery budget.
        return 1024 + Encoding.UTF8.GetByteCount(aliases.Encode(JsonSerializer.Serialize(maximum, PromptJson.Options)));
    }

    internal static string Expand(string code) => Array.IndexOf(Aliases, code) is var index && index >= 0 ? Codes[index] : code;

    internal static string RepairInstruction(string code) => code switch
    {
        "missing_action" => "Include every observed core action and assertion in this item's evidence.",
        "incorrect_outcome" => "Correct arguments, assignments, return values and outcomes using this item's source.",
        "reversed_condition" => "Preserve the exact predicate polarity, branch, early return and loop conditions.",
        "invented_call" => "Remove calls and execution order unsupported by the supplied source and facts.",
        "unsupported_role" => "Describe only the observed role; remove invented business purpose.",
        "mixed_scope" => "Keep this annotation inside its own function and control scope.",
        "incorrect_change" => "Compare both Git revisions and distinguish added, removed and retained behavior.",
        _ => "State the evidence limitation instead of asserting an unsupported meaning."
    };

    public static object RepairIssues(IReadOnlyList<SharedItemReview> rejected) => rejected.Select(item => new
    {
        item.Id, item.Issues,
        findings = item.Findings,
        instructions = item.Findings is { Count: > 0 } findings ? findings.Select(f => f.Instruction).ToArray() : item.Issues.Select(RepairInstruction).ToArray()
    }).ToArray();
}
