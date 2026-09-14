using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

internal sealed record SharedPreparedDiagram(SharedDiagramInput Input, DiagramIr Candidate,
    IReadOnlyDictionary<string, string> NodeItems, IReadOnlyDictionary<string, string> EdgeItems,
    IReadOnlyDictionary<string, string> ControlItems);

internal sealed class SharedSemanticProjection
{
    public const string Version = "shared-semantic-v4";
    internal static bool Improves(DiagramArtifact saved, SemanticGeneration incoming) =>
        incoming.Status == "Semantic" || saved.Explanation?.Status != "Semantic" &&
            (incoming.Explanation?.Coverage?.VerifiedUnits ?? 0) >= (saved.Explanation?.Coverage?.VerifiedUnits ?? 0);
    public Dictionary<string, SharedSemanticItem> Items { get; } = new(StringComparer.Ordinal);
    public List<SharedPreparedDiagram> Diagrams { get; } = [];
    private readonly bool codeBlocks;
    private readonly IReadOnlyDictionary<string, SourceFact> facts;
    private readonly IReadOnlyList<ExecutionMeaningResult>? meanings;

    public SharedSemanticProjection(bool codeBlocks, IReadOnlyList<SourceFact> facts, IReadOnlyList<ExecutionMeaningResult>? meanings = null)
    {
        this.codeBlocks = codeBlocks;
        this.facts = facts.ToDictionary(f => f.Id);
        this.meanings = meanings;
    }

    public void Add(SharedDiagramInput input)
    {
        var candidate = Compact(input.Diagram, input.Selection);
        string Item(string kind, string label, IEnumerable<string> ids, IEnumerable<CodeContext>? contexts = null,
            IEnumerable<string>? details = null)
        {
            var factIds = ids.Distinct().Order(StringComparer.Ordinal).ToArray();
            var context = (contexts ?? []).Distinct().ToArray();
            var sourceDetails = (details ?? []).Distinct().ToArray();
            var key = SemanticExecution.Hash(JsonSerializer.Serialize(new { kind, factIds, context,
                // Explicit refinement instructions are semantic inputs; direction,
                // fonts, page IDs and diagram type are not.
                input.Selection.RefinementInstruction,
                fallback = factIds.Length == 0 || kind is "state" or "control" ? label : null }));
            Items.TryAdd(key, new(key, kind, label, factIds, context, sourceDetails,
                factIds.Where(facts.ContainsKey).SelectMany(id => facts[id].ChangeIds).Distinct().ToArray(),
                FirstLocation(factIds.Where(facts.ContainsKey).Select(id => facts[id])), input.Selection.RefinementInstruction));
            return key;
        }
        var nodeItems = new Dictionary<string, string>();
        foreach (var node in candidate.Nodes.Where(n => n.Kind is not ("entry" or "exit") &&
            !(n.Kind == "participant" && (n.SourceFactIds is null || n.SourceFactIds.Count == 0))))
        {
            var contexts = (node.SourceFactIds ?? []).Where(facts.ContainsKey).Select(id => facts[id].Context)
                .Append(node.Context).Where(c => c is not null).Cast<CodeContext>().Distinct().ToArray();
            var kind = node.Kind is "class" or "type" or "struct" or "interface" or "record" ? "type" :
                node.Kind is "participant" or "method" or "function" or "responsibility" ? "symbol" : node.Kind;
            nodeItems[node.Id] = Item(kind, node.QualifiedName ?? node.Label, node.SourceFactIds ?? [], contexts, node.Details);
        }
        var edgeItems = new Dictionary<string, string>();
        foreach (var edge in candidate.Edges.Where(e => e.RelationOrigin != "user" &&
            (candidate.Type is "sequence" or "state" || e.Type is "calls" or "dependency")))
            if (edge.Type is not ("response" or "return" or "throw"))
                edgeItems[edge.Id] = Item("message", edge.Label, edge.SourceFactIds ?? [], edge.Context is null ? [] : [edge.Context],
                    edge.Call is null ? [] : [JsonSerializer.Serialize(edge.Call, PromptJson.Options)]);
        var controlItems = new Dictionary<string, string>();
        foreach (var block in CodeBlockPlanValidation.ControlBlocks(candidate.SequenceBlocks ?? []))
        {
            // Branch polarity belongs to the annotation identity as well as the
            // immutable sequence tree. Equal-looking branches must not collide.
            var edgeIds = ControlEdges(block).ToHashSet();
            controlItems[block.Id] = Item("control", block.Label, block.SourceFactIds ?? candidate.Edges.Where(e => edgeIds.Contains(e.Id))
                .SelectMany(e => e.SourceFactIds ?? []).ToArray(), details: [block.Kind, block.OriginalExpression ?? block.Label]);
        }
        Diagrams.Add(new(input, candidate, nodeItems, edgeItems, controlItems));
    }

    private static IEnumerable<string> ControlEdges(SequenceBlock block) =>
        (block.EdgeId is null ? Enumerable.Empty<string>() : [block.EdgeId]).Concat(block.Children.SelectMany(ControlEdges));

    public void AddChanges(IEnumerable<string> selectedChangeIds)
    {
        foreach (var changeId in selectedChangeIds.Distinct())
        {
            var source = facts.Values.Where(f => f.ChangeIds.Contains(changeId) && f.Kind is "symbol" or "source").ToArray();
            var key = "change-" + changeId;
            Items[key] = new(key, "change", "변경 전후 차이", source.Select(f => f.Id).ToArray(),
                [], [], [changeId], FirstLocation(source));
        }
    }

    private static SourceSpan? FirstLocation(IEnumerable<SourceFact> facts) => facts.Select(f => f.Span).OfType<SourceSpan>()
        .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.StartLine).ThenBy(s => s.StartOffset).FirstOrDefault();

    public SharedDiagramGroup Apply(SharedSemanticResponse semantics)
    {
        var annotations = semantics.Items.ToDictionary(i => i.Id);
        var results = new Dictionary<string, SemanticGeneration>();
        foreach (var prepared in Diagrams)
        {
            var candidate = prepared.Candidate;
            try
            {
            var missing = prepared.NodeItems.Values.Concat(prepared.EdgeItems.Values).Concat(prepared.ControlItems.Values)
                .Any(id => !annotations.ContainsKey(id));
            var pageFacts = candidate.Nodes.SelectMany(n => n.SourceFactIds ?? []).Concat(candidate.Edges.SelectMany(e => e.SourceFactIds ?? [])).ToHashSet();
            var changes = facts.Values.Where(f => pageFacts.Contains(f.Id)).SelectMany(f => f.ChangeIds).Distinct().ToArray();
            missing |= !codeBlocks && changes.Any(id => !annotations.ContainsKey("change-" + id));
            var warnings = new List<string>();
            string? failureStage = null;
            var required = prepared.NodeItems.Values.Concat(prepared.EdgeItems.Values).Concat(prepared.ControlItems.Values)
                .Concat(codeBlocks ? [] : changes.Select(id => "change-" + id)).Distinct().ToArray();
            if (missing)
            {
                var needed = prepared.NodeItems.Values.Concat(prepared.EdgeItems.Values).Concat(prepared.ControlItems.Values)
                    .Concat(changes.Select(id => "change-" + id)).Where(id => !annotations.ContainsKey(id)).ToHashSet();
                var failure = semantics.Failures?.FirstOrDefault(f => f.ItemIds.Any(needed.Contains));
                var warning = failure is null ? "일부 코드 묶음의 의미 검토를 완료하지 못했습니다." :
                    (failure.Stage == "generation" ? "의미 생성 단계: " : failure.Stage == "plan-validation" ? "생성 응답 검증 단계: " : "의미 검토 단계: ") +
                    LlmFailure.Describe(new LlmClientException(failure.Code, "Shared annotation incomplete.", serverErrorCategory: failure.Category));
                warnings.Add(warning);
                failureStage = failure?.Stage ?? "semantic-review";
            }
            var elements = candidate.Nodes.Select(n => new SemanticElement(n.Id,
                prepared.NodeItems.TryGetValue(n.Id, out var key) && annotations.TryGetValue(key, out var annotation) ? annotation.Summary : n.Label, [n.Id])).ToArray();
            var messages = candidate.Edges.Where(e => prepared.EdgeItems.ContainsKey(e.Id) ||
                candidate.Type == "sequence").Select(e => new SemanticMessage(e.Id,
                prepared.EdgeItems.TryGetValue(e.Id, out var key) && annotations.TryGetValue(key, out var annotation) ? annotation.Summary : e.Label)).ToArray();
            var changeExplanations = changes.Where(id => annotations.ContainsKey("change-" + id)).Select(id => new PageChangeExplanation(id, annotations["change-" + id].Description,
                facts.Values.Where(f => f.ChangeIds.Contains(id)).Select(f => f.Id).ToArray(),
                candidate.Nodes.Where(n => (n.SourceFactIds ?? []).Any(f => facts.TryGetValue(f, out var value) && value.ChangeIds.Contains(id))).Select(n => n.Id).ToArray(),
                candidate.Edges.Where(e => (e.SourceFactIds ?? []).Any(f => facts.TryGetValue(f, out var value) && value.ChangeIds.Contains(id))).Select(e => e.Id).ToArray())).ToArray();
            var plan = new DiagramPlan(semantics.Summary, elements, messages, [], changeExplanations);
            if (InternalLlmClient.ValidatePlan(candidate, plan) is not null ||
                !codeBlocks && !missing && InternalLlmClient.ValidateChanges(candidate, plan, facts.Values.ToArray()) is not null)
            {
                results[prepared.Input.Key] = new(candidate, "Incomplete", ["공유 의미의 구조·변경 근거 검증을 완료하지 못했습니다."], [], 0, FailureStage: "plan-validation");
                continue;
            }
            var projected = InternalLlmClient.ApplyPlan(candidate, plan);
            SequenceBlock Control(SequenceBlock block) => block with
            {
                Label = prepared.ControlItems.TryGetValue(block.Id, out var key) && annotations.TryGetValue(key, out var annotation) ? annotation.Summary : block.Label,
                Children = block.Children.Select(Control).ToArray()
            };
            projected = projected with
            {
                Nodes = projected.Nodes.Select(n => prepared.NodeItems.TryGetValue(n.Id, out var key) && annotations.ContainsKey(key)
                    ? n with { Label = n.Kind == "state" ? annotations[key].Summary + " (" + candidate.Nodes.First(original => original.Id == n.Id).Label + ")" : n.Label,
                        Details = (n.Details ?? []).Append(annotations[key].Description).Distinct().ToArray() } : n).ToArray(),
                Edges = projected.Edges.Select(e => e with { Label = candidate.Type == "flowchart" ? e.Label switch
                    { "true" or "then" => "예", "false" or "else" => "아니요", "return" => "종료", _ => e.Label }
                    : prepared.EdgeItems.TryGetValue(e.Id, out var key) && annotations.ContainsKey(key)
                        ? annotations[key].Summary + (e.Call is null ? "" : "\n" + CallPresentationBuilder.Compact(e.Call)) : e.Label }).ToArray(),
                SequenceBlocks = projected.SequenceBlocks?.Select(Control).ToArray()
            };
            var behaviors = projected.Nodes.Where(n => prepared.NodeItems.TryGetValue(n.Id, out var key) && annotations.ContainsKey(key)).Select(n => new CodeBlockBehavior(n.Id,
                annotations[prepared.NodeItems[n.Id]].Description,
                n.SourceFactIds ?? [], [n.Id], [])).ToArray();
            var evidence = projected.Nodes.SelectMany(n => n.EvidenceIds).Concat(projected.Edges.SelectMany(e => e.EvidenceIds))
                .Concat(changeExplanations.SelectMany(c => c.FactIds).Where(facts.ContainsKey).SelectMany(id => facts[id].EvidenceIds)).Distinct().ToArray();
            var explanation = new DiagramExplanation(semantics.Summary, changeExplanations,
                pageFacts.Concat(changeExplanations.SelectMany(c => c.FactIds)).Distinct().ToArray(), evidence, missing ? "Incomplete" : "Semantic", warnings,
                Behaviors: codeBlocks ? behaviors : null, Coverage: new(required.Length, required.Count(annotations.ContainsKey),
                    required.Count(id => !annotations.ContainsKey(id)), required.Count(id => !annotations.ContainsKey(id) &&
                        semantics.Failures?.Any(f => f.ItemIds.Contains(id)) == true)));
            var validator = new DiagramValidator();
            validator.Validate(projected);
            _ = new MermaidCompiler(validator).Compile(projected);
            results[prepared.Input.Key] = new(projected, missing ? "Incomplete" : "Semantic", warnings, [], 0, explanation, failureStage);
            }
            catch (Exception error) when (error is DiagramValidationException or DiagramGenerationException)
            {
                results[prepared.Input.Key] = new(candidate, "Incomplete", ["이 페이지의 구조 검증을 완료하지 못했습니다. Code 다이어그램을 확인하세요."], [], 0,
                    FailureStage: "projection");
            }
        }
        return new(semantics.Summary, semantics.RecommendedType, results,
            codeBlocks ? null : new ChangeUnderstanding(semantics.Summary, Items.Values.Where(i => i.Kind == "change" && annotations.ContainsKey(i.Id))
                .Select(i => new ChangeExplanation(i.ChangeIds.Single(), annotations[i.Id].Description, i.FactIds)).ToArray()));
    }

    private DiagramIr Compact(DiagramIr candidate, DiagramViewSelection selection)
    {
        if (candidate.Type != "flowchart" || selection.Overrides?.DetailLevel == "detailed") return candidate;
        var consumed = new HashSet<string>();
        var groups = new List<string[]>();
        var nodes = candidate.Nodes.ToDictionary(n => n.Id);
        string? Unit(DiagramNode node)
        {
            if (node.Context?.Span is not { StartOffset: { } start, EndOffset: { } end } span) return null;
            foreach (var meaning in meanings ?? [])
            {
                if (meaning.Plan is null || meaning.Input.Span.FilePath != span.FilePath || meaning.Input.Span.RevisionSha != span.RevisionSha) continue;
                var events = ExecutionSequenceProjection.Flatten(meaning.Input.Events).ToDictionary(e => e.Id);
                foreach (var unit in meaning.Plan.Units)
                    if (unit.EventIds.Any(id => events[id].StartOffset >= start && events[id].EndOffset <= end))
                        return meaning.Input.Id + "/" + unit.EventIds[0];
            }
            return null;
        }
        bool Eligible(DiagramNode node) => node.Kind == "operation" && node.Context is not null &&
            node.Context.Purpose is not ("call" or "assertion") && node.Group is not null;
        foreach (var node in candidate.Nodes)
        {
            if (!consumed.Add(node.Id)) continue;
            var chain = new List<string> { node.Id };
            var current = node;
            while (Eligible(current) && chain.Count < 32)
            {
                var outgoing = candidate.Edges.Where(e => e.SourceId == current.Id).ToArray();
                if (outgoing.Length != 1 || outgoing[0].Type == "loopBack" || !nodes.TryGetValue(outgoing[0].TargetId, out var next) ||
                    consumed.Contains(next.Id) || !Eligible(next) || next.Group != current.Group || next.DetailPageId != current.DetailPageId ||
                    candidate.Edges.Count(e => e.TargetId == next.Id) != 1 ||
                    JsonSerializer.Serialize(current.Context!.ControlPath) != JsonSerializer.Serialize(next.Context!.ControlPath) ||
                    current.Context.Span.FilePath != next.Context.Span.FilePath || current.Context.Span.RevisionSha != next.Context.Span.RevisionSha ||
                    meanings is not null && (Unit(current) is not { } unit || Unit(next) != unit)) break;
                chain.Add(next.Id); consumed.Add(next.Id); current = next;
            }
            groups.Add(chain.ToArray());
        }
        var plan = new DiagramPlan("연속된 준비 동작", groups.Select((ids, i) => new SemanticElement("chain-" + i, "코드의 준비 동작", ids)).ToArray(), [], []);
        if (InternalLlmClient.ValidatePlan(candidate, plan) is not null) return candidate;
        var merged = InternalLlmClient.ApplyPlan(candidate, plan);
        return merged with { Nodes = merged.Nodes.Select((n, i) => n with
        {
            Label = groups[i].Length == 1 ? nodes[groups[i][0]].Label : n.Label,
            // Every original statement is retained in the reviewed context and
            // evidence even when a straight-line preparation chain is compressed.
            Details = groups[i].Select(id => nodes[id].Context?.Statement ?? nodes[id].Label).ToArray(),
            Context = groups[i].Length > 1 && n.Context is { } context ? context with
                { Statement = string.Join("\n", groups[i].Select(id => nodes[id].Context?.Statement)),
                    Span = context.Span with { EndLine = nodes[groups[i][^1]].Context!.Span.EndLine,
                        EndOffset = nodes[groups[i][^1]].Context!.Span.EndOffset } } : n.Context
        }).ToArray() };
    }
}
