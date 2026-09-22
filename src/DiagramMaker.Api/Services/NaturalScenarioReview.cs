using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record NaturalScenarioReview(IReadOnlyList<string> ReviewedRequirementIds, IReadOnlyList<NaturalIssue> Issues)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Accepted => Issues.Count == 0;
}

internal static class NaturalScenarioReviewValidation
{
    internal static readonly string[] Codes = ["NaturalRequirementOmitted", "NaturalConditionChanged", "NaturalUnsupportedClaim",
        "NaturalEvidenceMismatch", "NaturalEntityMismatch", "NaturalScenarioMismatch", "NaturalInterlockMissing",
        "NaturalInitialMissing", "NaturalInitialIncomingInvalid", "NaturalInitialOutgoingInvalid", "NaturalFinalOutgoingInvalid"];
    public static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new {
        type = "object", additionalProperties = false, required = new[] { "reviewedRequirementIds", "issues" },
        properties = new {
            reviewedRequirementIds = new { type = "array", items = new { type = "string" }, maxItems = 150 },
            issues = new { type = "array", maxItems = 12, items = new {
                type = "object", additionalProperties = false,
                required = new[] { "itemId", "field", "code", "instruction", "evidenceIds" },
                properties = new {
                    itemId = new { type = "string", maxLength = 80 }, field = new { type = "string", maxLength = 80 },
                    code = new { type = "string", @enum = Codes }, instruction = new { type = "string", maxLength = 300 },
                    evidenceIds = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string" } }
                } } }
        }
    });

    public static SharedValidationProblem? Check(NaturalScenarioReview value, NaturalRequirements requirements,
        IReadOnlySet<string> elements)
    {
        var expected = requirements.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var returned = value.ReviewedRequirementIds ?? [];
        var details = new LlmValidationDetails(expected.Count, returned.Count,
            expected.Except(returned).Count(), returned.Count - returned.Distinct().Count(), returned.Count(id => !expected.Contains(id)),
            Field: "reviewedRequirementIds");
        if (details.UnknownItems > 0) return new("NaturalReviewUnknownIds", details);
        if (details.DuplicateItems > 0) return new("NaturalReviewDuplicateIds", details);
        if (details.MissingItems > 0) return new("NaturalReviewMissingIds", details);
        var evidence = expected.Concat((requirements.SourceRanges ?? []).Select(r => r.Id)).ToHashSet();
        if (value.Issues is null || value.Issues.Count > 12) return new("NaturalReviewIssuesInvalid", details with { Field = "issues" });
        foreach (var issue in value.Issues)
        {
            if (issue is null || !Codes.Contains(issue.Code) || string.IsNullOrWhiteSpace(issue.Field) ||
                string.IsNullOrWhiteSpace(issue.Instruction) || issue.EvidenceIds is not { Count: > 0 and <= 6 })
                return new("NaturalReviewIssuesInvalid", details with { Field = "issues" });
            if (!expected.Contains(issue.ItemId) && !elements.Contains(issue.ItemId) || issue.EvidenceIds.Any(id => !evidence.Contains(id)))
                return new("NaturalReviewTargetUnknown", details with { Field = "issues.evidenceIds" });
        }
        return null;
    }
}
