using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record SharedValidationProblem(string Code, LlmValidationDetails Details);

internal static class SharedSemanticValidation
{
    // Normalize harmless presentation only. Code fences, executable markup,
    // links and Mermaid directives still fail the security/content checks.
    public static string PlainText(string value)
    {
        value = Regex.Replace(value, @"(?<!`)`([^`\r\n]+)`(?!`)", "$1");
        return Regex.Replace(value, @"\*\*([^*\r\n]+)\*\*|__([^_\r\n]+)__", "$1$2");
    }

    public static bool UnsafeText(string text) => Regex.IsMatch(text,
        @"```|%%\{|\b(?:javascript|data)\s*:|<\s*/?\s*(?:script|iframe|img|svg|style|object|embed|a|div|span|br|p|b|i|strong|em|html|body)\b(?:\s[^<>]*|/)?\s*>|<[^<>]*\bon\w+\s*=|!?(?:\[[^\]]*\])\([^)]*\)",
        RegexOptions.IgnoreCase);
    public static SharedValidationProblem? Check(SharedSemanticResponse value, IReadOnlySet<string> expected,
        IReadOnlyList<string> available, bool validateText = true)
    {
        var returned = (value.Items ?? []).Where(i => i is not null && !string.IsNullOrEmpty(i.Id)).Select(i => i.Id).ToArray();
        var details = new LlmValidationDetails(expected.Count, value.Items?.Count ?? 0,
            expected.Except(returned).Count(), returned.Length - returned.Distinct().Count(), returned.Count(id => !expected.Contains(id)));
        if (string.IsNullOrWhiteSpace(value.Summary) || value.Summary.Length > 500)
            return new("SharedSummaryInvalid", details with { Field = "summary", ActualLength = value.Summary?.Length ?? 0, AllowedLength = 500 });
        if (!available.Contains(value.RecommendedType)) return new("SharedRecommendedTypeInvalid", details with { Field = "recommendedType" });
        if (value.Items is null || value.Items.Any(i => i is null || string.IsNullOrEmpty(i.Id)))
            return new("SharedItemsInvalid", details with { Field = "items" });
        if (details.UnknownItems > 0) return new("SharedUnknownIds", details);
        if (details.DuplicateItems > 0) return new("SharedDuplicateIds", details);
        if (details.MissingItems > 0) return new("SharedMissingIds", details);
        if (value.Items.Count != expected.Count) return new("SharedItemCountMismatch", details);
        if (!validateText) return null;
        for (var i = 0; i < value.Items.Count; i++)
        {
            var error = CheckItem(value.Items[i], i, details);
            if (error is not null) return error;
        }
        return null;
    }

    public static SharedValidationProblem? CheckItem(SharedSemanticAnnotation item, int index, LlmValidationDetails? details = null)
    {
        details ??= new(1, 1);
        return Text(item.Summary, 80, "items.summary", index) ?? Text(item.Description, 500, "items.description", index);
        SharedValidationProblem? Text(string? text, int maximum, string field, int index)
        {
            var metadata = details with { Field = field, ItemIndex = index, ActualLength = text?.Length ?? 0, AllowedLength = maximum };
            if (string.IsNullOrWhiteSpace(text)) return new("SharedTextEmpty", metadata);
            if (text.Length > maximum) return new("SharedTextTooLong", metadata);
            if (!Regex.IsMatch(text, "[가-힣]")) return new("SharedTextNotKorean", metadata);
            if (UnsafeText(text))
                return new("SharedTextCodeSyntax", metadata);
            return null;
        }
    }

    public static string? CheckJson(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
            !root.TryGetProperty("summary", out var summary) || summary.ValueKind != System.Text.Json.JsonValueKind.String ||
            !root.TryGetProperty("recommendedType", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String ||
            !root.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) return "SharedItemsInvalid";
        foreach (var item in items.EnumerateArray())
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object || item.EnumerateObject().Count() != 3 ||
                !new[] { "id", "summary", "description" }.All(name => item.TryGetProperty(name, out var field) && field.ValueKind == System.Text.Json.JsonValueKind.String))
                return "SharedItemsInvalid";
        return null;
    }

    public static string RepairInstruction(string code) => code switch
    {
        "SharedSummaryInvalid" => "Return a nonempty summary of at most 500 characters.",
        "SharedRecommendedTypeInvalid" => "Copy one exact value from available into recommendedType.",
        "SharedTextEmpty" or "SharedTextTooLong" or "SharedTextNotKorean" or "SharedTextCodeSyntax" =>
            "Rewrite the indicated field as nonempty concise Korean prose: summary at most 80 characters, description at most 500. Identifiers and predicates are plain text; never include markup or code fences.",
        _ => "Return exactly one item for every supplied items.id. Copy those IDs exactly, with no missing, invented or repeated ID. Keep id, summary and description on each item."
    };
}
