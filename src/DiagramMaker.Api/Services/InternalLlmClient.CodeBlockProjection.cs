using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    internal static DiagramIr ApplyCodePlan(DiagramIr candidate, CodeBlockSemanticPlan plan)
    {
        var generic = new DiagramPlan(plan.Summary, plan.Elements.Select(e => new SemanticElement(e.Id, e.Summary, e.NodeIds)).ToArray(), plan.Messages, []);
        var projected = ApplyPlan(candidate, generic);
        var nodes = projected.Nodes.Select((node, index) =>
        {
            var element = plan.Elements[index];
            var originals = element.NodeIds.Select(id => candidate.Nodes.First(n => n.Id == id)).ToArray();
            var returns = originals.Any(n => n.Kind == "return");
            return node with
            {
                Label = node.Kind is "condition" or "loop" ? DiagramPresentation.Condition(node.OriginalExpression ?? node.Label, element.Condition) : element.Summary,
                QualifiedName = originals[0].Label,
                Kind = returns ? "return" : node.Kind,
                Shape = returns ? "return" : node.Shape,
                Details = candidate.Type == "class" ? node.Details : (node.Details ?? []).Concat(originals.Select(n => n.Context?.Statement ?? n.Label))
                    .Append(element.Outcome).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray()
            };
        }).ToArray();
        var messages = plan.Messages.ToDictionary(m => m.EdgeId, m => m.Summary);
        var edges = projected.Edges.Select(e => e with
        {
            Label = candidate.Type == "flowchart" ? e.Label switch
            { "true" or "then" => "예", "false" or "else" => "아니요", "return" => "종료", _ => e.Label }
                : e.RelationOrigin == "user" ? "사용자 제공: " + messages.GetValueOrDefault(e.Id, e.Label.Replace("사용자 제공: ", ""))
                : messages.GetValueOrDefault(e.Id, e.Label)
        }).ToArray();
        var controls = (plan.Controls ?? []).ToDictionary(c => c.Id, c => c.Label);
        SequenceBlock NaturalControl(SequenceBlock b) => b with
        {
            Label = controls.GetValueOrDefault(b.Id, b.Kind == "scenario" ? plan.Summary : b.Label),
            Children = b.Children.Select(NaturalControl).ToArray()
        };
        return projected with { Nodes = nodes, Edges = edges, SequenceBlocks = projected.SequenceBlocks?.Select(NaturalControl).ToArray() };
    }
}
