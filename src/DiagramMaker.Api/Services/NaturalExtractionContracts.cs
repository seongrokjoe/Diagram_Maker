using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// Wire-only contracts. The model extracts source statements, never decides their
// provenance or writes evidence quotes. Stored/public contracts stay compatible.
internal sealed record NaturalSourceRequirement(string Id, string Text, string Kind, IReadOnlyList<string> SourceRangeIds);
internal sealed record NaturalExtraction(string Title, IReadOnlyList<string> Entities,
    IReadOnlyList<NaturalSourceRequirement> Requirements, IReadOnlyList<NaturalScenario> Scenarios,
    IReadOnlyList<NaturalQuestion> Questions)
{
    public NaturalRequirements ToRequirements() => new(Title, Entities,
        Requirements.Select(item => new NaturalRequirement(item.Id, item.Text, item.Kind, "explicit", "",
            SourceRangeIds: item.SourceRangeIds)).ToArray(), Scenarios: Scenarios, Questions: Questions);

    public static NaturalExtraction FromRequirements(NaturalRequirements value) => new(value.Title, value.Entities,
        value.Requirements.Select(item => new NaturalSourceRequirement(item.Id, item.Text, item.Kind,
            NaturalRequirementEvidence.RangeIds(item))).ToArray(), value.Scenarios ?? [], value.Questions ?? []);
}

internal sealed record NaturalSourceIssue(string Code, string Field, IReadOnlyList<string> RequirementIds,
    IReadOnlyList<string> SourceRangeIds, string Instruction);
internal sealed record NaturalSourceReview(IReadOnlyList<string> ReviewedSourceRangeIds, IReadOnlyList<NaturalSourceIssue> Issues);
