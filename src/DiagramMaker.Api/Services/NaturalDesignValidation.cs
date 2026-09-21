using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal static class NaturalDesignValidation
{
    public const string Protocol = "natural-design-v3";
    public static string? Requirements(NaturalRequirements value, string prompt)
    {
        if (string.IsNullOrWhiteSpace(value.Title) || value.Requirements is not { Count: > 0 and <= 150 } ||
            value.Entities is null || value.Entities.Any(string.IsNullOrWhiteSpace)) return "NaturalRequirementsInvalid";
        if (value.Requirements.Any(r => r is null) || value.Requirements.Select(r => r.Id).Distinct().Count() != value.Requirements.Count) return "NaturalRequirementIdsInvalid";
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        foreach (var r in value.Requirements)
            if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.Text) || r.Text.Length > 500 ||
                r.Kind is not ("entity" or "behavior" or "state" or "interlock" or "error" or "data" or "member") ||
                r.Origin is not ("explicit" or "assumption") || SharedSemanticValidation.UnsafeText(r.Text) ||
                !NaturalRequirementEvidence.TryResolve(prompt, r, ranges, out _))
                return "NaturalRequirementEvidenceInvalid";
        return value.Requirements.Any(r => r.Origin == "explicit") ? null : "NaturalExplicitRequirementsMissing";
    }

    public static IReadOnlyList<NaturalIssue> RequirementIssues(NaturalRequirements value, string prompt)
    {
        var issues = new List<NaturalIssue>();
        var seen = new HashSet<string>();
        foreach (var item in value.Requirements ?? [])
        {
            var error = item is null ? "NullItem" : !seen.Add(item.Id ?? "") ? "DuplicateId" :
                Requirements(new(value.Title, value.Entities, [item]), prompt);
            if (item?.Origin == "assumption" && error == "NaturalExplicitRequirementsMissing") error = null;
            if (error is not null) issues.Add(new(item?.Id ?? "unknown", "requirements", error,
                "Return this item with a unique ID, supported kind and valid server sourceRangeIds; preserve accepted items."));
        }
        if (value.Requirements is not { Count: > 0 }) issues.Add(new("requirements", "requirements", "MissingItems", "Extract all source requirements."));
        return issues;
    }

    public static string? Plan(NaturalRequirements value)
    {
        var ids = value.Requirements.Select(r => r.Id).ToHashSet();
        var ranges = (value.SourceRanges ?? []).Select(r => r.Id).ToHashSet();
        if (value.Scenarios is { Count: > 0 } scenarios &&
            (scenarios.Count > 30 || scenarios.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Title) ||
                s.RequirementIds is not { Count: > 0 } || s.RequirementIds.Any(id => !ids.Contains(id)) ||
                s.SourceRangeIds is null || s.SourceRangeIds.Any(id => !ranges.Contains(id))) ||
             scenarios.Select(s => s.Id).Distinct().Count() != scenarios.Count ||
             !scenarios.SelectMany(s => s.RequirementIds).ToHashSet().SetEquals(ids))) return "NaturalScenarioInvalid";
        if (value.Questions is { Count: > 0 } questions && (questions.Count > 5 ||
            questions.Any(q => q is null || string.IsNullOrWhiteSpace(q.Id) || string.IsNullOrWhiteSpace(q.Text) ||
                string.IsNullOrWhiteSpace(q.Reason) || q.Text.Length > 500 || q.Reason.Length > 500 ||
                q.SourceRangeIds is not { Count: > 0 } || q.SourceRangeIds.Any(id => !ranges.Contains(id)) ||
                q.Choices is null || q.Choices.Count > 6 || q.Choices.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 500)) ||
            questions.Select(q => q.Id).Distinct().Count() != questions.Count)) return "NaturalQuestionInvalid";
        return null;
    }

    public static string? Design(NaturalDesign value, string type, NaturalRequirements requirements)
    {
        if (value.Nodes is null || value.Edges is null || value.Notes is null) return "NaturalDesignFieldsInvalid";
        if (value.Nodes.Count > DiagramValidator.MaximumNodes || value.Edges.Count > DiagramValidator.MaximumEdges) return "TooManyItems";
        var allowed = requirements.Requirements.Select(r => r.Id).ToHashSet();
        var assumptionIds = requirements.Requirements.Where(r => r.Origin == "assumption").Select(r => r.Id).ToHashSet();
        var mapped = new HashSet<string>();
        foreach (var node in value.Nodes)
        {
            if (node.RequirementIds is null || node.Members is null || node.Details is null ||
                node.Kind is not ("class" or "interface" or "participant" or "state" or "initial" or "final" or "operation" or "decision" or "terminal" or "component") ||
                node.Shape is not ("" or "decision" or "terminal" or "call" or "process") ||
                !References(node.RequirementIds, node.Assumption)) return "NaturalNodeInvalid";
            if (type == "class" && (node.Kind is not ("class" or "interface") || node.Members.Count == 0)) return "NaturalClassMembersMissing";
            foreach (var member in node.Members)
                if (member.Kind is not ("field" or "method") || member.Visibility is not ("public" or "private" or "protected" or "internal") ||
                    string.IsNullOrWhiteSpace(member.Name) || string.IsNullOrWhiteSpace(member.Type) || member.Parameters is null || member.Preconditions is null ||
                    member.Parameters.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Type))) return "NaturalMemberInvalid";
        }
        foreach (var edge in value.Edges)
        {
            if (edge.RequirementIds is null || edge.ControlPath is null || edge.ControlPath.Count > 32 ||
                !References(edge.RequirementIds, edge.Assumption) ||
                edge.ControlPath.Any(c => c.Kind is not ("alt" or "opt" or "loop") || string.IsNullOrWhiteSpace(c.Id))) return "NaturalEdgeInvalid";
            if (type == "class" && edge.Type is not ("inherits" or "implements" or "association" or "depends" or "aggregation" or "composition"))
                return "NaturalClassRelationInvalid";
        }
        if (requirements.Requirements.Any(r => !mapped.Contains(r.Id))) return "NaturalRequirementCoverageMissing";
        foreach (var requirement in requirements.Requirements.Where(r => r.Kind == "interlock"))
        {
            var nodes = value.Nodes.Where(n => n.RequirementIds.Contains(requirement.Id)).ToArray();
            var edges = value.Edges.Where(e => e.RequirementIds.Contains(requirement.Id)).ToArray();
            var present = type switch
            {
                "class" => nodes.Any(n => n.Members.Any(m => m.Kind == "method" && m.Preconditions.Count > 0)),
                "state" => edges.Any(e => !string.IsNullOrWhiteSpace(e.Guard)),
                "sequence" => edges.Any(e => e.ControlPath.Any(c => c.Kind is "alt" or "opt" && !string.IsNullOrWhiteSpace(c.Label))),
                _ => nodes.Any(n => (n.Shape == "decision" || n.Kind == "decision") && value.Edges.Count(e => e.SourceId == n.Id) >= 2)
            };
            if (!present) return "NaturalInterlockMissing";
        }
        if (type == "state" && (!value.Nodes.Any(n => n.Kind == "initial") ||
            value.Nodes.Where(n => n.Kind == "initial").Any(n => value.Edges.Count(e => e.SourceId == n.Id) != 1 || value.Edges.Any(e => e.TargetId == n.Id)) ||
            value.Nodes.Where(n => n.Kind == "final").Any(n => value.Edges.Any(e => e.SourceId == n.Id)))) return "NaturalStateBoundaryInvalid";
        var texts = value.Nodes.SelectMany(n => n.Details.Append(n.Label).Concat(n.Members.SelectMany(m =>
            m.Preconditions.Append(m.Name).Append(m.Type).Concat(m.Parameters.SelectMany(p => new[] { p.Name, p.Type })))))
            .Concat(value.Edges.SelectMany(e => new[] { e.Label, e.Event, e.Guard, e.Action }.Concat(e.ControlPath.SelectMany(c => new[] { c.Label, c.Branch }))))
            .Concat(value.Notes).Append(value.Title);
        if (texts.Any(t => t is null || t.Length > 1000 || SharedSemanticValidation.UnsafeText(t))) return "NaturalUnsafeText";
        return new DiagramValidator().GetFailureKind(Normalize(value, type));

        bool References(IReadOnlyList<string> ids, bool assumption)
        {
            if (ids.Any(id => !allowed.Contains(id)) || !assumption && (ids.Count == 0 || ids.Any(assumptionIds.Contains))) return false;
            mapped.UnionWith(ids); return true;
        }
    }

    public static string? Review(NaturalDesignReview value, NaturalRequirements requirements) =>
        value.ReviewedRequirementIds is null || value.Issues is null || value.Issues.Count > 30 ||
        value.ReviewedRequirementIds.Count != requirements.Requirements.Count ||
        !value.ReviewedRequirementIds.ToHashSet().SetEquals(requirements.Requirements.Select(r => r.Id)) ||
        value.Accepted != (value.Issues.Count == 0) || value.Accepted && value.ItemIssues is { Count: > 0 } ||
        value.ItemIssues?.Any(issue => issue is null || string.IsNullOrWhiteSpace(issue.ItemId) || string.IsNullOrWhiteSpace(issue.Code) ||
            string.IsNullOrWhiteSpace(issue.Field) || string.IsNullOrWhiteSpace(issue.Instruction)) == true ? "NaturalReviewInvalid" : null;

    public static DiagramIr Normalize(NaturalDesign value, string type)
    {
        string Mark(string text, bool assumption) => assumption ? text + " (설계 가정)" : text;
        string Member(NaturalMember m) => (m.Visibility switch { "private" => "-", "protected" => "#", "internal" => "~", _ => "+" }) +
            (m.Kind == "field" ? m.Type + " " + m.Name : m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.Type + " " + p.Name)) + ") " + m.Type);
        var nodes = value.Nodes.Select(n => new DiagramNode(n.Id, Mark(n.Label, n.Assumption), n.Kind, null, "unchanged", Confidence.Inferred, [],
            Shape: n.Shape == "" ? n.Kind == "decision" ? "decision" : null : n.Shape,
            Details: n.Members.Select(Member).Concat(n.Members.SelectMany(m => m.Preconditions.Select(p => "전제조건 " + m.Name + ": " + p)))
                .Concat(n.Details).ToArray())).ToArray();
        var edges = value.Edges.Select((e, index) => new DiagramEdge(e.Id, e.SourceId, e.TargetId, e.Type,
            Mark(type == "state" ? string.Join(" ", new[] { e.Event.Length > 0 ? e.Event : e.Label,
                e.Guard.Length > 0 ? "[" + e.Guard + "]" : "", e.Action.Length > 0 ? "/ " + e.Action : "" }.Where(s => s.Length > 0)) : e.Label, e.Assumption),
            "unchanged", Confidence.Inferred, [], type == "sequence" ? index + 1 : null, ControlPath: e.ControlPath)).ToArray();
        return new(type, value.Title, nodes, edges, value.Notes, [Protocol],
            SequenceBlocks: type == "sequence" ? SequenceStructure.FromEdges(edges) : null);
    }

    private static object Text(int max = 500) => new { type = "string", maxLength = max };
    private static object Choice(params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };
    private static object List(object items, int max = 500) => new { type = "array", items, maxItems = max };
    private static object Obj(params (string Name, object Value)[] fields) => new { type = "object", additionalProperties = false,
        properties = fields.ToDictionary(f => f.Name, f => f.Value), required = fields.Select(f => f.Name).ToArray() };
    private static object Strings(int max = 150) => List(Text(), max);
    public static readonly JsonElement RequirementsSchema = JsonSerializer.SerializeToElement(Obj(("title", Text()), ("entities", Strings()),
        ("requirements", List(Obj(("id", Text(80)), ("text", Text()),
            ("kind", Choice("entity", "behavior", "state", "interlock", "error", "data", "member")),
            ("origin", Choice("explicit", "assumption")), ("sourceQuote", Text(1000)), ("sourceRangeIds", Strings())), 150)),
        ("scenarios", List(Obj(("id", Text(80)), ("title", Text()), ("requirementIds", Strings()), ("sourceRangeIds", Strings())), 30)),
        ("questions", List(Obj(("id", Text(80)), ("text", Text()), ("reason", Text()), ("sourceRangeIds", Strings()), ("choices", Strings(6))), 5))));
    public static readonly JsonElement RequirementsReviewSchema = JsonSerializer.SerializeToElement(Obj(
        ("accepted", new { type = "boolean" }), ("reviewedSourceRangeIds", Strings(1000)),
        ("issues", List(Obj(("itemId", Text(80)), ("field", Text(80)), ("code", Text(80)), ("instruction", Text())), 30))));
    public static readonly JsonElement DesignSchema = JsonSerializer.SerializeToElement(Obj(("title", Text()),
        ("nodes", List(Obj(("id", Text(80)), ("label", Text(240)),
            ("kind", Choice("class", "interface", "participant", "state", "initial", "final", "operation", "decision", "terminal", "component")),
            ("shape", Choice("", "decision", "terminal", "call", "process")),
            ("members", List(Obj(("name", Text(120)), ("kind", Choice("field", "method")),
                ("visibility", Choice("public", "private", "protected", "internal")), ("type", Text(120)),
                ("parameters", List(Obj(("name", Text(120)), ("type", Text(120))), 40)), ("preconditions", Strings(30))), 100)),
            ("details", Strings(30)), ("requirementIds", Strings()), ("assumption", new { type = "boolean" })))),
        ("edges", List(Obj(("id", Text(80)), ("sourceId", Text(80)), ("targetId", Text(80)),
            ("type", Choice("flow", "message", "response", "transition", "inherits", "implements", "association", "depends", "aggregation", "composition")),
            ("label", Text(240)), ("event", Text(240)), ("guard", Text(240)), ("action", Text(240)),
            ("controlPath", List(Obj(("id", Text(80)), ("kind", Choice("alt", "opt", "loop")), ("label", Text(240)), ("branch", Text(240))), 32)),
            ("requirementIds", Strings()), ("assumption", new { type = "boolean" })))), ("notes", Strings(100))));
    public static readonly JsonElement ReviewSchema = JsonSerializer.SerializeToElement(Obj(("accepted", new { type = "boolean" }),
        ("reviewedRequirementIds", Strings()), ("issues", Strings(30)),
        ("itemIssues", List(Obj(("itemId", Text(80)), ("field", Text(80)), ("code", Text(80)), ("instruction", Text())), 30))));

    public static JsonElement DesignSchemaFor(string type)
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(DesignSchema.GetRawText())!;
        var node = schema["properties"]!["nodes"]!["items"]!;
        var edge = schema["properties"]!["edges"]!["items"]!;
        void Remove(System.Text.Json.Nodes.JsonNode item, string field)
        {
            item["properties"]!.AsObject().Remove(field);
            item["required"] = new System.Text.Json.Nodes.JsonArray(item["required"]!.AsArray()
                .Where(value => value!.GetValue<string>() != field).Select(value => value!.DeepClone()).ToArray());
        }
        if (type != "class") Remove(node, "members");
        if (type != "flowchart") Remove(node, "shape");
        if (type != "sequence") Remove(edge, "controlPath");
        if (type != "state") foreach (var field in new[] { "event", "guard", "action" }) Remove(edge, field);
        return JsonSerializer.SerializeToElement(schema);
    }

    public static NaturalDesign CompleteInapplicableFields(NaturalDesign design, string type) => design with {
        Nodes = design.Nodes?.Select(node => node is null ? null! : node with {
            Members = type == "class" ? node.Members : [], Shape = type == "flowchart" ? node.Shape : "" }).ToArray()!,
        Edges = design.Edges?.Select(edge => edge is null ? null! : edge with {
            ControlPath = type == "sequence" ? edge.ControlPath : [], Event = type == "state" ? edge.Event : "",
            Guard = type == "state" ? edge.Guard : "", Action = type == "state" ? edge.Action : "" }).ToArray()!
    };
}
