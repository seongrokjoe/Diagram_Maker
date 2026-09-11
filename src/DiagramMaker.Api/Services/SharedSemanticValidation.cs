using System.Text.RegularExpressions;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record SharedValidationProblem(string Code, LlmValidationDetails Details);

internal static class SharedSemanticValidation
{
    public static SharedValidationProblem? Check(SharedSemanticResponse value, IReadOnlySet<string> expected,
        IReadOnlyList<string> available)
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
        for (var i = 0; i < value.Items.Count; i++)
        {
            var item = value.Items[i];
            var error = Text(item.Summary, 80, "items.summary", i) ?? Text(item.Description, 500, "items.description", i);
            if (error is not null) return error;
        }
        return null;

        SharedValidationProblem? Text(string? text, int maximum, string field, int index)
        {
            var metadata = details with { Field = field, ItemIndex = index, ActualLength = text?.Length ?? 0, AllowedLength = maximum };
            if (string.IsNullOrWhiteSpace(text)) return new("SharedTextEmpty", metadata);
            if (text.Length > maximum) return new("SharedTextTooLong", metadata);
            if (!Regex.IsMatch(text, "[가-힣]")) return new("SharedTextNotKorean", metadata);
            if (Regex.IsMatch(text, @"[<>`;{}]|&&|\|\||==|!=")) return new("SharedTextCodeSyntax", metadata);
            return null;
        }
    }

    public static string RepairInstruction(string code) => code switch
    {
        "SharedSummaryInvalid" => "Return a nonempty summary of at most 500 characters.",
        "SharedRecommendedTypeInvalid" => "Copy one exact value from available into recommendedType.",
        "SharedTextEmpty" or "SharedTextTooLong" or "SharedTextNotKorean" or "SharedTextCodeSyntax" =>
            "Rewrite the indicated field as nonempty concise Korean prose: summary at most 80 characters, description at most 500. Express predicates in words; keep code operators and markup in source evidence only.",
        _ => "Return exactly one item for every supplied items.id. Copy those IDs exactly, with no missing, invented or repeated ID. Keep id, summary and description on each item."
    };
}
