using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static partial class NaturalDesignValidation
{
    internal static string[] SourceIssueCodes => ["NaturalRequirementOmitted", "NaturalConditionChanged",
        "NaturalUnsupportedClaim", "NaturalEvidenceMismatch", "NaturalEntityMismatch", "NaturalScenarioMismatch"];

    public static SharedValidationProblem? RequirementsReview(NaturalSourceReview review,
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
        if (review.Issues is null || review.Issues.Count > 30) return new("NaturalReviewIssuesInvalid", details);
        var requirementIds = requirements.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var issue in review.Issues)
        {
            if (issue is null || !SourceIssueCodes.Contains(issue.Code) || string.IsNullOrWhiteSpace(issue.Instruction) ||
                issue.Field is not ("text" or "kind" or "sourceRangeIds" or "scenarios" or "entities") ||
                issue.SourceRangeIds is not { Count: > 0 } || issue.RequirementIds is null ||
                issue.SourceRangeIds.Distinct().Count() != issue.SourceRangeIds.Count ||
                issue.RequirementIds.Distinct().Count() != issue.RequirementIds.Count ||
                issue.Code != "NaturalRequirementOmitted" && issue.RequirementIds.Count == 0)
                return new("NaturalReviewIssuesInvalid", details with { Field = "issues" });
            if (issue.SourceRangeIds.Any(id => !expected.Contains(id)) || issue.RequirementIds.Any(id => !requirementIds.Contains(id)))
                return new("NaturalReviewTargetUnknown", details with { Field = "issues" });
        }
        return null;
    }

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
        "NaturalTooManyItems" or "NaturalTooFewItems" or "NaturalFieldUnexpected" or "NaturalRepairNoProgress" or
        "FormatRepairBudgetExhausted" or "ContentRepairBudgetExhausted" or "NaturalInitialMissing" or
        "NaturalInitialIncomingInvalid" or "NaturalInitialOutgoingInvalid" or "NaturalFinalOutgoingInvalid" or
        "NaturalMeaningMissing" or "NaturalMeaningKeysInvalid" or "NaturalMeaningReferencesInvalid" or "NaturalIntegrationScopeInvalid" or
        "NaturalRequirementOmitted" or "NaturalConditionChanged" or "NaturalUnsupportedClaim" or
        "NaturalEvidenceMismatch" or "NaturalEntityMismatch" or "NaturalScenarioMismatch" or
        "NaturalOriginInvalid" or "NaturalEvidenceMissing" or "NaturalEvidenceDuplicate" or
        "NaturalEvidenceUnknown" or "NaturalEvidenceAmbiguous" or "NaturalUnsafeText" or "NaturalExplicitRequirementsMissing" or
        "NaturalScenarioInvalid" or "NaturalScenarioCountInvalid" or "NaturalScenarioFieldsInvalid" or
        "NaturalScenarioDuplicateId" or "NaturalScenarioAssignmentsMissing" or "NaturalScenarioUnknownRequirement" or
        "NaturalScenarioSourceInvalid" or "NaturalScenarioRequirementMissing" or "NaturalQuestionDuplicateId" or
        "NaturalQuestionSourceInvalid" or "NaturalQuestionInvalid" or "NaturalRequirementsInvalid" or "NaturalRequirementIdsInvalid" or
        "NaturalRequirementEvidenceInvalid" or "NaturalReviewFieldsMissing" or "NaturalReviewUnknownIds" or "NaturalReviewDuplicateIds" or
        "NaturalReviewMissingIds" or "NaturalReviewEvidenceInvalid" or "NaturalReviewFieldInvalid" or "NaturalReviewIssuesInvalid" or "NaturalReviewDecisionInvalid" or "NaturalReviewTargetUnknown" or
        "NaturalDesignFieldsInvalid" or "NaturalNodeInvalid" or "NaturalClassMembersMissing" or "NaturalMemberInvalid" or
        "NaturalEdgeInvalid" or "NaturalClassRelationInvalid" or "NaturalRequirementCoverageMissing" or "NaturalInterlockMissing" or "NaturalStateBoundaryInvalid" or
        "NaturalReviewInvalid" or "NaturalAcceptedIdsChanged" or "NaturalRequirementsReviewInvalid" or
        "EmptyContent" or "MixedContent" or "WrongRoot" or "MalformedJson" or "Deserialization" or "InvalidFields" => code,
        "NaturalSemanticIssue" => code,
        _ => "NaturalReviewIssueUnclassified"
    };

    internal static string DiagnosticField(string? field) => field switch {
        "requirements" or "sourceRangeIds" or "origin" or "scenarios" or "questions" or "structure" or "json" or
        "text" or "kind" or "label" or "guard" or "action" or "event" or "id" or "shape" or "assumption" or
        "members" or "details" or "controlPath" or "type" or "sourceId" or "targetId" or "entities" or "concepts" or "connections" or "meaning" or "requirementIds" => field,
        _ => "review"
    };
}
