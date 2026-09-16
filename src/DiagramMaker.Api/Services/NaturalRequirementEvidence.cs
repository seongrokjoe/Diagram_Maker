using DiagramMaker.Domain;
using System.Text.RegularExpressions;

namespace DiagramMaker.Services;

internal static class NaturalRequirementEvidence
{
    public static NaturalRequirements Attach(string prompt, NaturalRequirements requirements)
    {
        var ranges = new List<NaturalPromptRange>();
        var located = new List<(NaturalRequirement Requirement, int Start, int End)>();
        var searchAt = 0;
        foreach (var requirement in requirements.Requirements)
        {
            if (requirement.Origin != "explicit" || string.IsNullOrEmpty(requirement.SourceQuote))
            {
                located.Add((requirement, -1, -1));
                continue;
            }
            var start = prompt.IndexOf(requirement.SourceQuote, searchAt, StringComparison.Ordinal);
            if (start < 0) start = prompt.IndexOf(requirement.SourceQuote, StringComparison.Ordinal);
            var end = start < 0 ? -1 : start + requirement.SourceQuote.Length;
            if (start >= 0) searchAt = end;
            located.Add((requirement, start, end));
        }

        var paragraphStarts = ParagraphStarts(prompt);
        var scenarioKeys = located.Where(item => item.Start >= 0)
            .Select(item => ParagraphAt(paragraphStarts, item.Start)).Distinct().Order().ToArray();
        if (scenarioKeys.Length == 0) scenarioKeys = [0];
        var scenarioIds = scenarioKeys.Select((key, index) => (key, id: $"scenario-{index + 1}"))
            .ToDictionary(item => item.key, item => item.id);
        var enriched = new List<NaturalRequirement>();
        foreach (var item in located)
        {
            var scenarioKey = item.Start >= 0 ? ParagraphAt(paragraphStarts, item.Start) : scenarioKeys[0];
            var scenarioId = scenarioIds.GetValueOrDefault(scenarioKey, scenarioIds[scenarioKeys[0]]);
            string? rangeId = null;
            if (item.Start >= 0)
            {
                rangeId = $"range-{ranges.Count + 1}";
                ranges.Add(new(rangeId, item.Start, item.End, prompt[item.Start..item.End]));
            }
            enriched.Add(item.Requirement with { SourceRangeId = rangeId, ScenarioId = scenarioId });
        }
        var scenarios = scenarioKeys.Select((key, index) =>
        {
            var id = scenarioIds[key];
            var assigned = enriched.Where(requirement => requirement.ScenarioId == id).ToArray();
            var title = assigned.FirstOrDefault(requirement => requirement.Origin == "explicit")?.Text
                ?? assigned.FirstOrDefault()?.Text ?? $"시나리오 {index + 1}";
            if (title.Length > 80) title = title[..80];
            return new NaturalScenario(id, title, assigned.Select(requirement => requirement.Id).ToArray(),
                assigned.Select(requirement => requirement.SourceRangeId).OfType<string>().Distinct().ToArray());
        }).ToArray();
        return requirements with { Requirements = enriched, SourceRanges = ranges, Scenarios = scenarios };
    }

    public static IReadOnlyList<NaturalScenario> EffectiveScenarios(NaturalRequirements requirements) =>
        requirements.Scenarios is { Count: > 0 } scenarios ? scenarios :
        [new("scenario-1", requirements.Title, requirements.Requirements.Select(requirement => requirement.Id).ToArray(),
            requirements.Requirements.Select(requirement => requirement.SourceRangeId).OfType<string>().Distinct().ToArray())];

    public static NaturalRequirements ForScenario(NaturalRequirements requirements, NaturalScenario scenario)
    {
        var ids = scenario.RequirementIds.ToHashSet(StringComparer.Ordinal);
        var selected = requirements.Requirements.Where(requirement => ids.Contains(requirement.Id)).ToArray();
        var rangeIds = selected.Select(requirement => requirement.SourceRangeId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return requirements with { Title = scenario.Title, Requirements = selected,
            SourceRanges = (requirements.SourceRanges ?? []).Where(range => rangeIds.Contains(range.Id)).ToArray(),
            Scenarios = [scenario] };
    }

    private static int[] ParagraphStarts(string prompt)
    {
        var starts = new List<int> { 0 };
        foreach (Match separator in Regex.Matches(prompt, @"(?:\r\n|\n|\r)[\t ]*(?:\r\n|\n|\r)"))
        {
            var next = separator.Index + separator.Length;
            while (next < prompt.Length && prompt[next] is ' ' or '\t') next++;
            if (next < prompt.Length) starts.Add(next);
        }
        return starts.Distinct().Order().ToArray();
    }

    private static int ParagraphAt(IReadOnlyList<int> starts, int offset)
    {
        var result = starts[0];
        foreach (var start in starts)
        {
            if (start > offset) break;
            result = start;
        }
        return result;
    }
}
