using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// Wire-only contracts. The model extracts source statements, never decides their
// provenance or writes evidence quotes. Stored/public contracts stay compatible.
internal sealed record NaturalSourceRequirement(string Id, string Text, string Kind, IReadOnlyList<string> SourceRangeIds);
internal sealed record NaturalSourceScenario(string Id, string Title, IReadOnlyList<string> RequirementIds);
internal sealed record NaturalExtraction(string Title, IReadOnlyList<string> Entities,
    IReadOnlyList<NaturalSourceRequirement> Requirements, IReadOnlyList<NaturalSourceScenario> Scenarios,
    IReadOnlyList<NaturalQuestion> Questions)
{
    public NaturalRequirements ToRequirements() => new(Title, Entities,
        Requirements.Select(item => new NaturalRequirement(item.Id, item.Text, item.Kind, "explicit", "",
            SourceRangeIds: item.SourceRangeIds)).ToArray(), Scenarios: Scenarios.Select(item => new NaturalScenario(item.Id, item.Title, item.RequirementIds, [])).ToArray(), Questions: Questions);

    public static NaturalExtraction FromRequirements(NaturalRequirements value) => new(value.Title, value.Entities,
        value.Requirements.Select(item => new NaturalSourceRequirement(item.Id, item.Text, item.Kind,
            NaturalRequirementEvidence.RangeIds(item))).ToArray(), (value.Scenarios ?? []).Select(item => new NaturalSourceScenario(item.Id, item.Title, item.RequirementIds)).ToArray(), value.Questions ?? []);
}

internal sealed record NaturalSourceIssue(string Code, string Field, IReadOnlyList<string> RequirementIds,
    IReadOnlyList<string> SourceRangeIds, string Instruction);
internal sealed record NaturalSourceReview(IReadOnlyList<string> ReviewedSourceRangeIds, IReadOnlyList<NaturalSourceIssue> Issues);
