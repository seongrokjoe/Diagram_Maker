using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class DiagramVariants
{
    public static bool IsAi(DiagramArtifact artifact) => artifact.Explanation is { } explanation &&
        (explanation.Status == "Semantic" || explanation.Status == "Incomplete" && explanation.Coverage?.VerifiedUnits > 0);
    public static bool IsPartialAi(DiagramArtifact artifact) => artifact.Explanation is
        { Status: "Incomplete", Coverage.VerifiedUnits: > 0 };
    public static DiagramPage PreserveCode(DiagramPage page) => page with
    { CodeDiagram = page.CodeDiagram ?? page.Diagram, AiState = page.AiState ?? "Pending", ResultKind = "static" };

    public static DiagramPage Apply(DiagramPage page, SemanticGeneration? generated, MermaidCompiler compiler, bool final = false)
    {
        var code = page.CodeDiagram ?? page.Diagram;
        if (generated is { } accepted && (accepted.Status == "Semantic" ||
            accepted.Status == "Incomplete" && accepted.Explanation?.Coverage?.VerifiedUnits > 0))
        {
            var partial = accepted.Status == "Incomplete";
            return page with { CodeDiagram = code, AiState = partial ? "Partial" : "Completed", ResultKind = "semantic",
                Diagram = page.Diagram with { Id = IsAi(page.Diagram) ? page.Diagram.Id : Guid.NewGuid(),
                    Ir = accepted.Diagram, MermaidDsl = compiler.Compile(accepted.Diagram), Explanation = accepted.Explanation } };
        }
        // Unverified annotations remain in checkpoints. Code diagrams never contain unreviewed AI prose.
        if (IsAi(page.Diagram)) return page with { AiState = final ? "Failed" : page.AiState };
        return page with { CodeDiagram = code, Diagram = code, ResultKind = "static",
            AiState = final || generated?.Explanation?.Coverage?.FailedUnits > 0 ? "Failed" : "Pending" };
    }

    public static DiagramArtifact? Select(DiagramPage page, string? variant) => variant switch
    {
        null or "" => page.Diagram,
        "ai" => IsAi(page.Diagram) ? page.Diagram : null,
        "code" => page.CodeDiagram ?? (!IsAi(page.Diagram) ? page.Diagram : null),
        _ => null
    };

    public static DiagramResultCounts Count(IEnumerable<DiagramPage> pages, bool terminal, int emptyFailures = 0, int reused = 0)
    {
        var all = pages.ToArray();
        bool Ai(DiagramPage page) => page.ResultKind == "semantic" || IsAi(page.Diagram);
        bool Partial(DiagramPage page) => page.AiState == "Partial" || IsPartialAi(page.Diagram);
        return new(all.Count(p => Ai(p) && !Partial(p)),
            all.Count(p => p.AiState == "Failed" || terminal && !Ai(p) && p.AiState is not "Pending") + emptyFailures,
            all.Count(p => !Ai(p) && (p.AiState == "Pending" || !terminal && p.AiState is null)),
            all.Count(p => p.CodeDiagram is not null || !IsAi(p.Diagram)), reused,
            all.Count(p => Ai(p) && Partial(p)));
    }

    public static DiagramResultCounts CountAnalysis(IEnumerable<AnalysisDiagramGroupResult> groups, bool terminal)
    {
        var views = groups.SelectMany(g => g.Views is { Count: > 0 } ? g.Views :
            g.Diagram is null ? [] : new[] { new AnalysisDiagramViewResult(g.GroupId + "-view",
                new(g.GroupId + "-view", g.Diagram.Type, "balanced"), g.Diagram, [], "Completed") }).ToArray();
        static IEnumerable<DiagramPage> Pages(AnalysisDiagramViewResult view) => view.Document?.Pages ??
            (view.Diagram is null ? [] : [new("overview", view.Diagram.Ir.Title, view.Diagram)]);
        return Count(views.SelectMany(Pages), terminal, views.Count(v => v.State == "Failed" && !Pages(v).Any()),
            views.Where(v => v.Reused).SelectMany(Pages).Count(p => p.ResultKind == "semantic" || IsAi(p.Diagram)));
    }
}
