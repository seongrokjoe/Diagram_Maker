using System.Text.Json;
using System.Text.RegularExpressions;
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
                required = new[] { "itemId", "field", "code", "instruction", "evidenceIds", "sourceQuote", "relatedElementIds" },
                properties = new {
                    itemId = new { type = "string", maxLength = 80 }, field = new { type = "string", maxLength = 80 },
                    code = new { type = "string", @enum = Codes }, instruction = new { type = "string", maxLength = 300 },
                    evidenceIds = new { type = "array", minItems = 1, maxItems = 6, items = new { type = "string" } },
                    sourceQuote = new { type = "string", maxLength = 500 },
                    relatedElementIds = new { type = "array", maxItems = 6, items = new { type = "string" } }
                } } }
        }
    });

    public static SharedValidationProblem? Check(NaturalScenarioReview value, NaturalRequirements requirements,
        IReadOnlySet<string> elements, NaturalDesign? design = null)
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
                string.IsNullOrWhiteSpace(issue.Instruction) || issue.EvidenceIds is not { Count: > 0 and <= 6 } ||
                issue.RelatedElementIds is null || issue.RelatedElementIds.Count > 6)
                return new("NaturalReviewIssuesInvalid", details with { Field = "issues" });
            if (!expected.Contains(issue.ItemId) && !elements.Contains(issue.ItemId) ||
                issue.EvidenceIds.Any(id => !evidence.Contains(id)) || issue.RelatedElementIds.Any(id => !elements.Contains(id)))
                return new("NaturalReviewTargetUnknown", details with { Field = "issues.evidenceIds" });
            if (design is not null && elements.Contains(issue.ItemId) && !AllowedField(issue.ItemId, issue.Field, design))
                return new("NaturalReviewFieldInvalid", details with { Field = "issues.field" });
            if (issue.Code == "NaturalConditionChanged" && !GroundedQuote(issue, requirements))
                return new("NaturalReviewEvidenceInvalid", details with { Field = "issues.sourceQuote" });
        }
        return null;
    }

    private static bool AllowedField(string itemId, string field, NaturalDesign design) =>
        design.Nodes.Any(node => node.Id == itemId) ? field is "label" or "kind" or "shape" or "members" or
            "details" or "requirementIds" or "assumption" :
        design.Edges.Any(edge => edge.Id == itemId) && field is "label" or "sourceId" or "targetId" or "type" or
            "event" or "guard" or "action" or "controlPath" or "requirementIds" or "assumption" or "order";

    private static bool GroundedQuote(NaturalIssue issue, NaturalRequirements requirements)
    {
        if (string.IsNullOrWhiteSpace(issue.SourceQuote)) return false;
        var ranges = (requirements.SourceRanges ?? []).Where(range => issue.EvidenceIds!.Contains(range.Id))
            .Select(range => range.Text);
        var requirementTexts = requirements.Requirements.Where(item => issue.EvidenceIds!.Contains(item.Id))
            .Select(item => item.SourceQuote.Length > 0 ? item.SourceQuote : item.Text);
        var quote = Normalize(issue.SourceQuote);
        var masker = new SecretMasker();
        return ranges.Concat(requirementTexts).Any(text =>
            Normalize(text).Contains(quote, StringComparison.Ordinal) ||
            Normalize(masker.Mask(text)).Contains(quote, StringComparison.Ordinal));
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}