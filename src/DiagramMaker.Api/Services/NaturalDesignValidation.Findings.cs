using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static partial class NaturalDesignValidation
{
    public static IReadOnlyList<NaturalIssue> PlanFindings(NaturalRequirements value)
    {
        var findings = new List<NaturalIssue>();
        var requirements = value.Requirements ?? [];
        var known = requirements.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var ranges = (value.SourceRanges ?? []).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var scenarios = value.Scenarios ?? [];
        if (scenarios.Count is 0 or > 30)
            findings.Add(new("scenarios", "scenarios", "NaturalScenarioCountInvalid",
                "Return between one and thirty scenarios covering the supplied requirements.", TargetKind: "scenario"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < scenarios.Count; index++)
        {
            var scenario = scenarios[index];
            if (scenario is null || string.IsNullOrWhiteSpace(scenario.Id) || string.IsNullOrWhiteSpace(scenario.Title))
            {
                findings.Add(new(scenario?.Id ?? "scenario", "scenarios", "NaturalScenarioFieldsInvalid",
                    "Supply a nonempty scenario ID and title.", TargetKind: "scenario", ItemIndex: index));
                continue;
            }
            if (!seen.Add(scenario.Id))
                findings.Add(new(scenario.Id, "id", "NaturalScenarioDuplicateId",
                    "Give this scenario a unique ID; preserve the other accepted IDs.", TargetKind: "scenario", ItemIndex: index));
            if (scenario.RequirementIds is not { Count: > 0 })
                findings.Add(new(scenario.Id, "requirementIds", "NaturalScenarioAssignmentsMissing",
                    "Connect this scenario to its actual supplied requirements.", TargetKind: "scenario", ItemIndex: index));
            foreach (var id in scenario.RequirementIds ?? [])
                if (!known.Contains(id))
                    findings.Add(new(scenario.Id, "requirementIds", "NaturalScenarioUnknownRequirement",
                        "Remove the unknown requirement ID and use only supplied IDs.", TargetKind: "scenario", ItemIndex: index));
                else covered.Add(id);
            if (scenario.SourceRangeIds is null || scenario.SourceRangeIds.Any(id => !ranges.Contains(id)))
                findings.Add(new(scenario.Id, "sourceRangeIds", "NaturalScenarioSourceInvalid",
                    "Use the server-derived source ranges of assigned requirements.", TargetKind: "scenario", ItemIndex: index));
        }
        foreach (var requirement in requirements.Where(item => !covered.Contains(item.Id)))
            findings.Add(new(requirement.Id, "requirementIds", "NaturalScenarioRequirementMissing",
                "Assign this supplied requirement to a connected scenario.", TargetKind: "requirement",
                EvidenceIds: NaturalRequirementEvidence.RangeIds(requirement)));
        var questions = value.Questions ?? [];
        if (questions.Count > 5)
            findings.Add(new("questions", "questions", "NaturalQuestionInvalid",
                "Return at most five clarification questions.", TargetKind: "question"));
        seen.Clear();
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            if (question is null || string.IsNullOrWhiteSpace(question.Id) || string.IsNullOrWhiteSpace(question.Text) ||
                string.IsNullOrWhiteSpace(question.Reason) || question.Text.Length > 500 || question.Reason.Length > 500 ||
                question.Choices is null || question.Choices.Count > 6 ||
                question.Choices.Any(choice => string.IsNullOrWhiteSpace(choice) || choice.Length > 500))
            {
                findings.Add(new(question?.Id ?? "question", "questions", "NaturalQuestionInvalid",
                    "Correct the question fields and choices without rewriting requirements.", TargetKind: "question", ItemIndex: index));
                continue;
            }
            if (!seen.Add(question.Id))
                findings.Add(new(question.Id, "id", "NaturalQuestionDuplicateId",
                    "Give this question a unique ID.", TargetKind: "question", ItemIndex: index));
            if (question.SourceRangeIds is not { Count: > 0 } || question.SourceRangeIds.Any(id => !ranges.Contains(id)))
                findings.Add(new(question.Id, "sourceRangeIds", "NaturalQuestionSourceInvalid",
                    "Cite an existing source range for this question.", TargetKind: "question", ItemIndex: index));
        }
        return findings;
    }
}