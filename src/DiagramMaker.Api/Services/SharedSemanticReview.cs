using System.Text;
using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record SharedSemanticReview(IReadOnlyList<SharedItemReview> Items);
internal sealed record SharedItemReview(string Id, IReadOnlyList<string> Issues);

internal static class SharedReviewValidation
{
    internal static readonly string[] Codes = ["missing_action", "incorrect_outcome", "reversed_condition",
        "invented_call", "unsupported_role", "mixed_scope", "incorrect_change", "insufficient_evidence"];

    public static JsonElement Schema(int count) => JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, required = new[] { "items" },
        properties = new { items = new { type = "array", minItems = count, maxItems = count,
            items = new { type = "object", additionalProperties = false, required = new[] { "id", "issues" },
                properties = new { id = new { type = "string" }, issues = new { type = "array", maxItems = 3,
                    items = new { type = "string", @enum = Codes } } } } } }
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
            i.Issues.Distinct().Count() != i.Issues.Count || i.Issues.Any(code => !Codes.Contains(code))))
            return new("SharedReviewIssuesInvalid", details);
        return null;
    }

    public static int OutputBudget(string prompt, IReadOnlySet<string> ids)
    {
        var aliases = new PromptIds(ids);
        aliases.Encode(prompt);
        var longest = Codes.OrderByDescending(c => c.Length).Take(3).ToArray();
        var maximum = new SharedSemanticReview(ids.Select(id => new SharedItemReview(id, longest)).ToArray());
        // One token per UTF-8 byte is deliberately conservative; actual serving
        // tokenizers vary. Reserve room for the compact response's framing.
        return 256 + Encoding.UTF8.GetByteCount(aliases.Encode(JsonSerializer.Serialize(maximum, PromptJson.Options)));
    }

    public static object RepairIssues(IReadOnlyList<SharedItemReview> rejected) => rejected.Select(item => new
    {
        item.Id, item.Issues,
        instructions = item.Issues.Select(code => code switch
        {
            "missing_action" => "Include every observed core action and assertion in this item's evidence.",
            "incorrect_outcome" => "Correct arguments, assignments, return values and outcomes using this item's source.",
            "reversed_condition" => "Preserve the exact predicate polarity, branch, early return and loop conditions.",
            "invented_call" => "Remove calls and execution order unsupported by the supplied source and facts.",
            "unsupported_role" => "Describe only the observed role; remove invented business purpose.",
            "mixed_scope" => "Keep this annotation inside its own function and control scope.",
            "incorrect_change" => "Compare both Git revisions and distinguish added, removed and retained behavior.",
            _ => "State the evidence limitation instead of asserting an unsupported meaning."
        }).ToArray()
    }).ToArray();
}
