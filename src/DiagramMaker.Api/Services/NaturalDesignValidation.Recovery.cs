using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static partial class NaturalDesignValidation
{
    public static SharedValidationProblem? RequirementsReview(NaturalRequirementsReview review,
        IReadOnlyList<NaturalPromptRange> source, NaturalRequirements requirements)
    {
        var expected = source.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var returned = review.ReviewedSourceRangeIds ?? [];
        var details = new LlmValidationDetails(expected.Count, returned.Count, expected.Except(returned).Count(),
            returned.Count - returned.Distinct().Count(), returned.Count(id => !expected.Contains(id)), Field: "reviewedSourceRangeIds");
        if (review.ReviewedSourceRangeIds is null) return new("NaturalReviewFieldsMissing", details);
        if (details.UnknownItems > 0) return new("NaturalReviewUnknownIds", details);
        if (details.DuplicateItems > 0) return new("NaturalReviewDuplicateIds", details);
        if (details.MissingItems > 0) return new("NaturalReviewMissingIds", details);
        if (review.Issues is null || review.Issues.Count > 30 || review.Issues.Any(issue => issue is null ||
            string.IsNullOrWhiteSpace(issue.ItemId) || string.IsNullOrWhiteSpace(issue.Field) ||
            string.IsNullOrWhiteSpace(issue.Code) || string.IsNullOrWhiteSpace(issue.Instruction)))
            return new("NaturalReviewIssuesInvalid", details with { Field = "issues" });
        if (review.Accepted != (review.Issues.Count == 0)) return new("NaturalReviewDecisionInvalid", details with { Field = "accepted" });
        var targets = expected.Concat(requirements.Requirements.Select(r => r.Id)).ToHashSet(StringComparer.Ordinal);
        if (review.Issues.Any(issue => !targets.Contains(issue.ItemId)))
            return new("NaturalReviewTargetUnknown", details with { Field = "issues.itemId" });
        return null;
    }

    // Free-form model issue codes/fields must never become a diagnostic export.
    internal static string DiagnosticCode(string? code) => code switch {
        "NullItem" or "DuplicateId" or "MissingItems" or "NaturalRequirementIdMissing" or "NaturalRequirementTextMissing" or
        "NaturalFieldTooLong" or "NaturalFieldMissing" or "NaturalFieldTypeInvalid" or "NaturalFieldEnumInvalid" or
        "NaturalTooManyItems" or "NaturalOriginInvalid" or "NaturalEvidenceMissing" or "NaturalEvidenceDuplicate" or
        "NaturalEvidenceUnknown" or "NaturalEvidenceAmbiguous" or "NaturalUnsafeText" or "NaturalExplicitRequirementsMissing" or
        "NaturalScenarioInvalid" or "NaturalQuestionInvalid" or "NaturalRequirementsInvalid" or "NaturalRequirementIdsInvalid" or
        "NaturalRequirementEvidenceInvalid" or "NaturalReviewFieldsMissing" or "NaturalReviewUnknownIds" or "NaturalReviewDuplicateIds" or
        "NaturalReviewMissingIds" or "NaturalReviewIssuesInvalid" or "NaturalReviewDecisionInvalid" or "NaturalReviewTargetUnknown" or
        "NaturalDesignFieldsInvalid" or "NaturalNodeInvalid" or "NaturalClassMembersMissing" or "NaturalMemberInvalid" or
        "NaturalEdgeInvalid" or "NaturalClassRelationInvalid" or "NaturalRequirementCoverageMissing" or "NaturalInterlockMissing" or "NaturalStateBoundaryInvalid" or
        "NaturalReviewInvalid" or "NaturalAcceptedIdsChanged" or "NaturalRequirementsReviewInvalid" or
        "EmptyContent" or "MixedContent" or "WrongRoot" or "MalformedJson" or "Deserialization" or "InvalidFields" => code,
        _ => "NaturalSemanticIssue"
    };

    internal static string DiagnosticField(string? field) => field switch {
        "requirements" or "sourceRangeIds" or "origin" or "scenarios" or "questions" or "structure" or "json" or
        "text" or "kind" or "label" or "guard" or "action" or "event" => field,
        _ => "review"
    };
}
