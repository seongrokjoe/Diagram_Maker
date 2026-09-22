using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static partial class NaturalDesignValidation
{
    public static IReadOnlyList<NaturalIssue> DesignIssues(NaturalDesign design, string type, NaturalRequirements requirements)
    {
        if (design.Nodes is null || design.Edges is null) return [new("design", "structure", "NaturalDesignFieldsInvalid", "Return non-null concepts and connections.")];
        var mapped = design.Nodes.Where(n => n is not null).SelectMany(n => n.RequirementIds ?? [])
            .Concat(design.Edges.Where(e => e is not null).SelectMany(e => e.RequirementIds ?? [])).ToHashSet();
        var missing = requirements.Requirements.Where(r => !mapped.Contains(r.Id)).Select(r => new NaturalIssue(r.Id,
            "requirementIds", "NaturalRequirementCoverageMissing",
            "Implement this requirement in actual structure. If already implemented, cite its existing concepts/connections; otherwise add only its missing behavior. A note or an unrelated ID attachment does not satisfy it.")).ToArray();
        if (missing.Length > 0) return missing;
        var error = Design(design, type, requirements);
        if (error is null) return [];
        var instruction = error switch {
            "NaturalInterlockMissing" => "Implement the interlock as a blocking decision with both branches, conditional messages, a state guard, or method preconditions for this diagram type.",
            "NaturalClassMembersMissing" => "Provide the grounded or explicitly assumed fields/methods of each class.",
            "NaturalStateBoundaryInvalid" => "Each initial pseudo-state needs exactly one outgoing transition and no incoming transitions; final states have no outgoing transitions.",
            "NaturalNodeInvalid" => "Correct concept kind/shape/provenance. Do not represent a design assumption as explicit source behavior.",
            _ => "Correct the identified structural rule. Preserve valid concepts, relationships, original conditions and source scope." };
        return [new(requirements.Requirements.Count == 1 ? requirements.Requirements[0].Id : "design", "structure", error, instruction)];
    }
}
