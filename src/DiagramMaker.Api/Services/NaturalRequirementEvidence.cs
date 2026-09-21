using DiagramMaker.Domain;
using System.Text.RegularExpressions;

namespace DiagramMaker.Services;

internal static class NaturalRequirementEvidence
{
    // Segment fully masked text, so partial multiline private keys cannot leak.
    public static IReadOnlyList<NaturalPromptRange> Prepare(string prompt)
    {
        var masked = new SecretMasker().MaskWithOffsets(prompt);
        var result = new List<NaturalPromptRange>();
        foreach (Match line in Regex.Matches(masked.Text, @"[^\r\n]+"))
        foreach (Match sentence in Regex.Matches(line.Value, @".+?(?:[.!?。！？](?=\s|$)|$)"))
        {
            var start = line.Index + sentence.Index;
            var end = start + sentence.Length;
            while (start < end && char.IsWhiteSpace(masked.Text[start])) start++;
            while (end > start && char.IsWhiteSpace(masked.Text[end - 1])) end--;
            if (start == end) continue;
            var originalStart = masked.Starts[start];
            var originalEnd = masked.Ends[end - 1];
            result.Add(new($"source-{originalStart}-{originalEnd}", originalStart, originalEnd, prompt[originalStart..originalEnd]));
        }
        return result;
    }

    public static IReadOnlyList<string> RangeIds(NaturalRequirement requirement) =>
        requirement.SourceRangeIds is { Count: > 0 } ids ? ids : requirement.SourceRangeId is { Length: > 0 } id ? [id] : [];

    public static bool TryResolve(string prompt, NaturalRequirement requirement, IReadOnlyList<NaturalPromptRange> ranges,
        out NaturalRequirement resolved)
    {
        resolved = requirement;
        if (requirement.Origin == "assumption")
        {
            resolved = requirement with { SourceQuote = "", SourceRangeId = null, SourceRangeIds = [] };
            return true;
        }
        var ids = RangeIds(requirement);
        if (ids.Count > 0)
        {
            if (ids.Distinct().Count() != ids.Count || ids.Any(id => !ranges.Any(range => range.Id == id))) return false;
        }
        else
        {
            // Legacy quotations must identify one span; tolerate only whitespace differences.
            if (string.IsNullOrWhiteSpace(requirement.SourceQuote)) return false;
            var masked = new SecretMasker().MaskWithOffsets(prompt);
            var pattern = string.Join(@"\s+", Regex.Split(requirement.SourceQuote.Trim(), @"\s+").Select(Regex.Escape));
            var matches = Regex.Matches(masked.Text, pattern);
            if (matches.Count != 1) return false;
            var found = matches[0];
            var start = masked.Starts[found.Index];
            var end = masked.Ends[found.Index + found.Length - 1];
            ids = ranges.Where(range => range.StartOffset < end && range.EndOffset > start).Select(range => range.Id).ToArray();
            if (ids.Count == 0) return false;
        }
        resolved = requirement with { SourceRangeId = ids[0], SourceRangeIds = ids,
            SourceQuote = string.Join("\n", ids.Select(id => ranges.Single(range => range.Id == id).Text)) };
        return true;
    }

    public static NaturalRequirements Attach(string prompt, NaturalRequirements requirements)
    {
        var ranges = Prepare(prompt);
        var enriched = requirements.Requirements.Select(requirement =>
        {
            if (!TryResolve(prompt, requirement, ranges, out var resolved))
                throw new LlmClientException("NATURAL_EVIDENCE_INVALID", "원문 근거를 유일하게 확인할 수 없습니다.");
            return resolved;
        }).ToArray();
        var scenarios = requirements.Scenarios is { Count: > 0 } planned ? planned : LegacyScenarios(prompt, enriched, ranges);
        return requirements with { Requirements = enriched.Select(requirement => requirement with {
                ScenarioId = scenarios.FirstOrDefault(scenario => scenario.RequirementIds.Contains(requirement.Id))?.Id }).ToArray(),
            SourceRanges = ranges, Scenarios = scenarios };
    }

    public static IReadOnlyList<NaturalScenario> EffectiveScenarios(NaturalRequirements requirements) =>
        requirements.Scenarios is { Count: > 0 } scenarios ? scenarios :
        [new("scenario-1", requirements.Title, requirements.Requirements.Select(r => r.Id).ToArray(),
            requirements.Requirements.SelectMany(RangeIds).Distinct().ToArray())];

    public static NaturalRequirements ForScenario(NaturalRequirements requirements, NaturalScenario scenario)
    {
        var ids = scenario.RequirementIds.ToHashSet(StringComparer.Ordinal);
        var selected = requirements.Requirements.Where(r => ids.Contains(r.Id) || r.Kind is "entity" or "interlock").ToArray();
        var rangeIds = selected.SelectMany(RangeIds).ToHashSet(StringComparer.Ordinal);
        return requirements with { Title = scenario.Title, Requirements = selected,
            SourceRanges = (requirements.SourceRanges ?? []).Where(range => rangeIds.Contains(range.Id)).ToArray(), Scenarios = [scenario] };
    }

    private static IReadOnlyList<NaturalScenario> LegacyScenarios(string prompt, IReadOnlyList<NaturalRequirement> requirements,
        IReadOnlyList<NaturalPromptRange> ranges)
    {
        var starts = new[] { 0 }.Concat(Regex.Matches(prompt, @"(?:\r\n|\n|\r)[\t ]*(?:\r\n|\n|\r)")
            .Select(m => m.Index + m.Length)).ToArray();
        var groups = requirements.GroupBy(r => starts.Last(start => start <=
            (ranges.FirstOrDefault(range => range.Id == r.SourceRangeId)?.StartOffset ?? 0))).OrderBy(g => g.Key);
        return groups.Select((group, index) => new NaturalScenario($"scenario-{index + 1}", group.First().Text,
            group.Select(r => r.Id).ToArray(), group.SelectMany(RangeIds).Distinct().ToArray())).ToArray();
    }
}
