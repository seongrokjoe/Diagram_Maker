using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class DiagramVariants
{
    public static bool IsAi(DiagramArtifact artifact) => artifact.Explanation?.Status == "Semantic";
    public static DiagramPage PreserveCode(DiagramPage page) => page with
    { CodeDiagram = page.CodeDiagram ?? page.Diagram, AiState = page.AiState ?? "Pending", ResultKind = "static" };

    public static DiagramPage Apply(DiagramPage page, SemanticGeneration? generated, MermaidCompiler compiler, bool final = false)
    {
        var code = page.CodeDiagram ?? page.Diagram;
        if (generated?.Status == "Semantic")
            return page with { CodeDiagram = code, AiState = "Completed", ResultKind = "semantic",
                Diagram = page.Diagram with { Id = IsAi(page.Diagram) ? page.Diagram.Id : Guid.NewGuid(),
                    Ir = generated.Diagram, MermaidDsl = compiler.Compile(generated.Diagram), Explanation = generated.Explanation } };
        // Partial annotations remain in checkpoints. Code diagrams never contain AI prose.
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
        return new(all.Count(p => (p.ResultKind == "semantic" || IsAi(p.Diagram))),
            all.Count(p => p.AiState == "Failed" || terminal && !(p.ResultKind == "semantic" || IsAi(p.Diagram)) && p.AiState is not "Pending") + emptyFailures,
            all.Count(p => !(p.ResultKind == "semantic" || IsAi(p.Diagram)) && (p.AiState == "Pending" || !terminal && p.AiState is null)),
            all.Count(p => p.CodeDiagram is not null || !IsAi(p.Diagram)), reused);
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
