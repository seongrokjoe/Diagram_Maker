using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed record CodeBlockCandidatePage(string Id, string Title, DiagramIr Diagram,
    string Level = "summary", IReadOnlyList<string>? BlockIds = null, IReadOnlyList<string>? SymbolIds = null);

public sealed class CodeBlockProjectionService(DiagramPresetCatalog presets)
{
    public IReadOnlyList<DiagramAvailability> Availability(CodeBlockGraph graph, CodeBlockGroupSelection group)
    {
        var symbols = graph.Symbols.Where(s => group.BlockIds.Contains(s.BlockId)).ToArray();
        var ids = symbols.Select(s => s.Id).ToHashSet();
        var calls = symbols.SelectMany(s => s.Calls).Any(c => c.TargetSymbolId is not null && ids.Contains(c.TargetSymbolId));
        return [new("flowchart", symbols.Any(s => s.Steps.Count > 0), "실행 가능한 함수 본문 또는 처리 구문이 필요합니다."),
            new("sequence", calls, "코드로 확인된 호출 대상이 필요합니다. 사용자 관계는 코드 관계도에서 볼 수 있습니다."),
            new("class", symbols.Any(IsType), "실제 클래스·구조체·인터페이스 선언이 필요합니다."),
            new("state", graph.Transitions.Any(t => ids.Contains(t.SymbolId)), "동일 상태 변수의 조건과 대입으로 확인된 전이가 필요합니다."),
            new("code-relation", symbols.Length > 0, "함수 또는 타입 선언이나 처리 구문이 필요합니다.")];
    }
    public static string Recommend(IReadOnlyList<DiagramAvailability> availability, CodeBlockGraph graph, CodeBlockGroupSelection group)
    {
        if (availability.Any(a => a.Type == "state" && a.Available)) return "state";
        if (graph.Relations.Any(r => r.Kind == "calls" && r.FromBlockId != r.ToBlockId && group.BlockIds.Contains(r.FromBlockId) && group.BlockIds.Contains(r.ToBlockId))) return "sequence";
        return new[] { "flowchart", "class", "code-relation" }.FirstOrDefault(t => availability.Any(a => a.Type == t && a.Available)) ?? "code-relation";
    }
    public IReadOnlyList<CodeBlockCandidatePage> Build(CodeBlockGraph graph, IReadOnlyList<CodeBlockRelation> relations,
        CodeBlockGroupSelection group, DiagramViewSelection selection)
    {
        var symbols = graph.Symbols.Where(s => group.BlockIds.Contains(s.BlockId)).ToArray();
        var ids = symbols.Select(s => s.Id).ToHashSet();
        var localRelations = relations.Where(r => group.BlockIds.Contains(r.FromBlockId) && group.BlockIds.Contains(r.ToBlockId)).ToArray();
        var direction = selection.Overrides?.Direction ?? presets.Resolve(selection.DiagramType, selection.PresetId).Direction;
        var pages = new List<CodeBlockCandidatePage>();
        if (selection.DiagramType == "flowchart")
        {
            foreach (var symbol in symbols.Where(s => s.Steps.Count > 0))
                pages.Add(new CodeBlockCandidatePage(Detail(symbol.Id), symbol.Name, Flow([symbol], []), "detail", [symbol.BlockId], [symbol.Id]));
            if (symbols.Sum(s => s.Steps.Count) <= 450 && symbols.Sum(s => s.FlowEdges.Count) + localRelations.Length <= 480)
                pages.Insert(0, new CodeBlockCandidatePage("overview", group.Title, Flow(symbols, localRelations)));
            else pages.Insert(0, new CodeBlockCandidatePage("overview", group.Title, RelationDiagram("flowchart")));
        }
        else if (selection.DiagramType == "sequence")
        {
            foreach (var symbol in symbols.Where(s => s.Calls.Any(c => c.TargetSymbolId is not null && ids.Contains(c.TargetSymbolId))))
            {
                var edges = symbol.Calls.Where(c => c.TargetSymbolId is not null && ids.Contains(c.TargetSymbolId)).OrderBy(c => c.Order).Select(c =>
                    Edge(c.Id, symbol.Id, c.TargetSymbolId!, "message", c.Statement ?? c.Name,
                        graph.Evidence.Where(e => e.Location == c.Location).Select(e => e.Id).ToArray(), [c.Id], "code") with
                    { SequenceIndex = c.Order, ControlPath = c.ControlPath }).ToArray();
                var participants = edges.Select(e => e.TargetId).Append(symbol.Id).ToHashSet();
                var nodes = symbols.Where(s => participants.Contains(s.Id)).Select(s => Node(s) with { Kind = "participant", DetailPageId = Detail(s.Id) }).ToArray();
                var ir = new DiagramIr("sequence", symbol.Name, nodes, edges, ["이 페이지는 한 함수의 호출 순서입니다. 다른 진입점의 실행 순서를 가정하지 않습니다."],
                    ["code-block", graph.AnalyzerVersion], direction, [new SequenceBlock(StableIds.Create("scenario", symbol.Id), "scenario", symbol.Name, SequenceStructure.FromEdges(edges))]);
                pages.Add(new CodeBlockCandidatePage(Detail(symbol.Id), symbol.Name, ir));
            }
            if (pages.Count > 0) pages[0] = pages[0] with { Id = "overview" };
            // Participant drilldowns point only at pages that actually exist.
            var availablePages = pages.Select(p => p.Id).ToHashSet();
            for (var i = 0; i < pages.Count; i++) pages[i] = pages[i] with { Diagram = pages[i].Diagram with
            { Nodes = pages[i].Diagram.Nodes.Select(n => n with { DetailPageId = availablePages.Contains(n.DetailPageId ?? "") ? n.DetailPageId : null }).ToArray() } };
        }
        else if (selection.DiagramType == "class")
        {
            var types = symbols.Where(IsType).ToArray();
            var typeIds = types.Select(s => s.Id).ToHashSet();
            var nodes = types.Select(s => Node(s) with { Kind = "class", Details = s.Members.Select(m => m.Signature).ToArray() }).ToArray();
            var edges = localRelations.Where(r => r.FromSymbolId is not null && r.ToSymbolId is not null && typeIds.Contains(r.FromSymbolId) && typeIds.Contains(r.ToSymbolId))
                .Select(r => RelationEdge(r, r.FromSymbolId!, r.ToSymbolId!)).ToArray();
            pages.Add(new("overview", group.Title, new DiagramIr("class", group.Title, nodes, edges, [], ["code-block", graph.AnalyzerVersion], direction)));
        }
        else if (selection.DiagramType == "state")
        {
            foreach (var transitions in graph.Transitions.Where(t => ids.Contains(t.SymbolId)).GroupBy(t => (t.SymbolId, t.Variable)))
            {
                var symbol = symbols.First(s => s.Id == transitions.Key.SymbolId);
                string StateId(string value) => StableIds.Create("state", transitions.Key.SymbolId, transitions.Key.Variable, value);
                var nodes = transitions.SelectMany(t => new[] { t.From, t.To }).Distinct().Select(value => new DiagramNode(StateId(value), value, "state", null,
                    "unchanged", Confidence.Exact, transitions.Where(t => t.From == value || t.To == value).SelectMany(t => t.EvidenceIds).Distinct().ToArray(),
                    SourceFactIds: transitions.Where(t => t.From == value || t.To == value).Select(t => t.Id).ToArray())).ToArray();
                var edges = transitions.Select(t => Edge(t.Id, StateId(t.From), StateId(t.To), "transition", string.IsNullOrWhiteSpace(t.Condition) ? t.Variable : t.Condition,
                    t.EvidenceIds, [t.Id], "code")).ToArray();
                var title = $"{symbol.Name} / {transitions.Key.Variable}";
                pages.Add(new(pages.Count == 0 ? "overview" : StableIds.Create("state-page", transitions.Key.SymbolId, transitions.Key.Variable), title,
                    new DiagramIr("state", title, nodes, edges, ["초기·종료 상태는 코드 근거 없이 추가하지 않습니다."], ["code-block", graph.AnalyzerVersion], direction)));
            }
        }
        else pages.Add(new("overview", group.Title, RelationDiagram("code-relation")));
        // A single function has the same topology in overview and detail.
        if (selection.DiagramType == "flowchart" && pages.Count == 2 &&
            pages[0].Diagram.Nodes.Select(n => n.Id).ToHashSet().SetEquals(pages[1].Diagram.Nodes.Select(n => n.Id)) &&
            pages[0].Diagram.Edges.Select(e => e.Id).ToHashSet().SetEquals(pages[1].Diagram.Edges.Select(e => e.Id)))
        {
            pages[0] = pages[0] with { SymbolIds = pages[1].SymbolIds };
            pages.RemoveAt(1);
        }
        if (selection.DiagramType == "flowchart")
        {
            foreach (var large in pages.Where(p => p.Diagram.Nodes.Count > 400 || p.Diagram.Edges.Count > 450).ToArray())
            {
                var chunks = large.Diagram.Nodes.Chunk(150).ToArray();
                var destinations = chunks.SelectMany((chunk, index) => chunk.Select(node => (node.Id, page: index == 0 ? large.Id : large.Id + "-" + (index + 1))))
                    .ToDictionary(pair => pair.Id, pair => pair.page);
                var replacements = new List<CodeBlockCandidatePage>();
                for (var index = 0; index < chunks.Length; index++)
                {
                    var own = chunks[index].Select(n => n.Id).ToHashSet();
                    var links = large.Diagram.Edges.Where(e => own.Contains(e.SourceId) || own.Contains(e.TargetId)).ToArray();
                    var endpoints = links.SelectMany(e => new[] { e.SourceId, e.TargetId }).Concat(own).ToHashSet();
                    var pageId = index == 0 ? large.Id : large.Id + "-" + (index + 1);
                    var nodes = large.Diagram.Nodes.Where(n => endpoints.Contains(n.Id)).Select(n => n with
                    { DetailPageId = own.Contains(n.Id) ? null : destinations[n.Id] }).ToArray();
                    replacements.Add(large with { Id = pageId, Title = $"{large.Title} · {index + 1}/{chunks.Length}",
                        Diagram = large.Diagram with { Nodes = nodes, Edges = links,
                            Notes = large.Diagram.Notes.Append("큰 함수의 연속 구간입니다. 경계의 원본 노드를 선택하면 연결된 코드 구간을 열 수 있습니다.").ToArray() } });
                }
                var at = pages.IndexOf(large); pages.RemoveAt(at); pages.InsertRange(at, replacements);
            }
        }
        var pageIds = pages.Select(p => p.Id).ToHashSet();
        for (var i = 0; i < pages.Count; i++) pages[i] = pages[i] with
        {
            BlockIds = pages[i].BlockIds ?? group.BlockIds,
            SymbolIds = pages[i].SymbolIds ?? symbols.Where(s => pages[i].Diagram.Nodes.Any(n =>
                (n.SourceFactIds ?? []).Contains(s.Id) || s.Steps.Any(step => (n.SourceFactIds ?? []).Contains(step.Id)))) .Select(s => s.Id).ToArray(),
            Diagram = pages[i].Diagram with { Nodes = pages[i].Diagram.Nodes.Select(n => n with
                { DetailPageId = pageIds.Contains(n.DetailPageId ?? "") && n.DetailPageId != pages[i].Id ? n.DetailPageId : null }).ToArray() }
        };
        return pages;

        DiagramIr Flow(IEnumerable<CodeBlockSymbol> items, IReadOnlyList<CodeBlockRelation> links)
        {
            var selected = items.Where(s => s.Steps.Count > 0).ToArray();
            var nodes = selected.SelectMany(s => s.Steps.Select(step => new DiagramNode(step.Id,
                step.Kind == "entry" ? $"{s.Name} 시작" : step.Kind == "exit" ? "종료" : step.Label,
                step.Kind, $"{s.Name} · 블럭 {Array.IndexOf(group.BlockIds.ToArray(), s.BlockId) + 1}, {s.Location.StartLine}행", "unchanged", Confidence.Exact, step.EvidenceIds,
                Shape: step.Kind switch { "entry" or "exit" => "terminal", "condition" or "loop" => "decision", "call" => "call", "return" => "return", _ => null },
                SourceFactIds: [step.Id], DetailPageId: Detail(s.Id), Context: step.Kind is "entry" or "exit" ? null : Context(step)))).ToArray();
            var edges = selected.SelectMany(s => s.FlowEdges.Select((e, i) => Edge(StableIds.Create(s.Id, "flow", i), e.SourceId, e.TargetId,
                e.Type, e.Label, s.EvidenceIds, [s.Id], "code"))).ToList();
            foreach (var relation in links.Where(r => r.Origin == "code" && r.Kind == "calls"))
            {
                var from = selected.FirstOrDefault(s => s.Id == relation.FromSymbolId);
                var target = selected.FirstOrDefault(s => s.Id == relation.ToSymbolId);
                var call = from?.Calls.FirstOrDefault(c => c.Id == relation.CallSiteId);
                var step = call is null ? null : from?.Steps.Where(s => s.Kind != "entry" && s.Kind != "exit" &&
                    s.Location.StartOffset <= call.Location.StartOffset && s.Location.EndOffset >= call.Location.EndOffset)
                    .OrderBy(s => s.Location.EndOffset - s.Location.StartOffset).FirstOrDefault();
                var entry = target?.Steps.FirstOrDefault(s => s.Kind == "entry") ?? target?.Steps.FirstOrDefault();
                if (step is not null && entry is not null) edges.Add(RelationEdge(relation, step.Id, entry.Id) with { IsIndirect = true });
            }
            return new DiagramIr("flowchart", group.Title, nodes, edges,
                links.Any(r => r.Origin == "user") ? ["위치·순서 미확인 사용자 관계는 코드 관계도에서 확인하세요."] : [], ["code-block", graph.AnalyzerVersion], direction);
        }
        DiagramIr RelationDiagram(string type)
        {
            var nodes = symbols.Select(s => Node(s) with { Shape = IsType(s) ? "type" : "method", DetailPageId = s.Steps.Count > 0 && type == "flowchart" ? Detail(s.Id) : null }).ToList();
            string Endpoint(string blockId, string? symbolId)
            {
                if (symbolId is not null && ids.Contains(symbolId)) return symbolId;
                var id = StableIds.Create("block", blockId);
                if (nodes.All(n => n.Id != id)) nodes.Add(new DiagramNode(id, group.Title + " / " + blockId, "block", null, "unchanged", Confidence.Inferred,
                    graph.Evidence.Where(e => e.BlockId == blockId).Select(e => e.Id).ToArray(), SourceFactIds: symbols.Where(s => s.BlockId == blockId).Select(s => s.Id).ToArray()));
                return id;
            }
            var edges = localRelations.Select(r => RelationEdge(r, Endpoint(r.FromBlockId, r.FromSymbolId), Endpoint(r.ToBlockId, r.ToSymbolId))).ToArray();
            return new DiagramIr(type, group.Title, nodes, edges, ["그룹 안의 독립 항목 사이에는 관계나 실행 순서를 추가하지 않습니다."], ["code-block", graph.AnalyzerVersion], direction);
        }
    }
    public static string Detail(string symbolId) => "detail-" + symbolId;
    private static bool IsType(CodeBlockSymbol s) => !s.IsFragment && s.Kind is "class" or "type" or "struct" or "interface" or "record";
    private static DiagramNode Node(CodeBlockSymbol s) => new(s.Id, s.Name, s.Kind, null, "unchanged", Confidence.Exact, s.EvidenceIds, SourceFactIds: [s.Id]);
    private static DiagramEdge RelationEdge(CodeBlockRelation r, string from, string to) => Edge(r.Id, from, to, r.Kind,
        r.Origin == "user" ? "사용자 제공: " + r.Description : r.Description, r.EvidenceIds ?? [], [r.Id], r.Origin);
    private static DiagramEdge Edge(string id, string from, string to, string type, string label, IReadOnlyList<string> evidence,
        IReadOnlyList<string> facts, string origin) => new(id, from, to, type, label.Length > 240 ? label[..237] + "…" : label,
            "unchanged", origin == "code" ? Confidence.Exact : Confidence.Inferred, evidence, SourceFactIds: facts, RelationOrigin: origin);
    private static CodeContext Context(CodeBlockStep s) => new(s.Statement, s.Target, s.Receiver, s.Arguments ?? [], s.AssignedTo, null, [],
        s.ControlPath, new SourceSpan("code-block", "", s.Location.BlockId, s.Location.StartLine, s.Location.EndLine, s.Location.StartOffset, s.Location.EndOffset),
        s.Kind == "call" ? "call" : s.Purpose);
}
