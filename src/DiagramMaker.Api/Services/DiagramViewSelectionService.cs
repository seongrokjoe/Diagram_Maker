using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class DiagramViewSelectionService
{
    public static IReadOnlyList<DiagramViewSelection> EffectiveViews(this AnalysisGroupSelection group)
    {
        if (group.Views is { Count: > 0 }) return group.Views;
        return [new DiagramViewSelection($"{group.Id}-view", group.DiagramType, group.PresetId, group.Overrides)];
    }

    public static IReadOnlyList<DiagramViewSelection> EffectiveViews(this NaturalDiagramRequest request)
    {
        if (request.Views is { Count: > 0 }) return request.Views;
        return [new DiagramViewSelection("primary", request.DiagramType, request.PresetId, request.Style)];
    }

    public static AnalysisGroupSelection NormalizeViews(this AnalysisGroupSelection group)
    {
        var views = group.EffectiveViews().Select(Normalize).ToArray();
        var primary = views[0];
        return group with
        {
            Id = group.Id.Trim(),
            Title = group.Title.Trim(),
            DiagramType = primary.DiagramType,
            PresetId = primary.PresetId,
            Overrides = primary.Overrides,
            Views = views
        };
    }

    public static DiagramViewSelection Normalize(DiagramViewSelection view) => view with
    {
        Id = view.Id.Trim(),
        DiagramType = view.DiagramType.Trim().ToLowerInvariant(),
        PresetId = view.PresetId.Trim()
    };
}
