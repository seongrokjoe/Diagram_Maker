using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public static class DiagramDocumentBuilder
{
    public static DiagramViewDocument Build(DiagramArtifact artifact, EvidenceBundle bundle)
    {
        var ir = artifact.Ir;
        var pages = new List<DiagramPage>();
        if (ir.Type == "sequence" && ir.SequenceBlocks is { Count: > 0 })
        {
            var overviewEdges = new List<DiagramEdge>();
            var overviewBlocks = new List<SequenceBlock>();
            foreach (var scenario in ir.SequenceBlocks)
            {
                var ids = MessageIds([scenario]).ToHashSet();
                var edges = ir.Edges.Where(edge => ids.Contains(edge.Id)).ToArray();
                if (edges.Length == 0) continue;
                var nodes = NodesFor(edges, ir.Nodes);
                var pageId = StableIds.Create("page", scenario.Id);
                pages.Add(Page(pageId, scenario.Label, ir with { Nodes = nodes, Edges = edges, SequenceBlocks = [scenario] }));
                var first = edges[0];
                overviewEdges.Add(first);
                overviewBlocks.Add(new SequenceBlock(scenario.Id, "scenario", scenario.Label,
                    [new SequenceBlock(first.Id, "message", first.Label, [], first.Id),
                     new SequenceBlock(pageId + "_ref", "note", $"상세 보기 · {edges.Length}개 호출", [],
                        ParticipantIds: [first.SourceId, first.TargetId], DetailPageId: pageId)]));
            }
            ir = ir with { Nodes = NodesFor(overviewEdges, ir.Nodes), Edges = overviewEdges, SequenceBlocks = overviewBlocks,
                Notes = ir.Notes.Append("요약에는 각 시나리오의 첫 호출을 표시합니다. 전체 호출과 분기는 상세 페이지에서 확인하세요.").ToArray() };
        }
        else if (ir.Type == "flowchart" && (ir.Nodes.Count > 20 || ir.Nodes.Select(node => node.Group).Distinct().Count() > 1))
        {
            var summaries = new List<DiagramNode>();
            foreach (var group in ir.Nodes.GroupBy(node => node.Group ?? ir.Title))
            {
                var nodes = group.ToArray();
                var ids = nodes.Select(node => node.Id).ToHashSet();
                var pageId = StableIds.Create("page", group.Key);
                pages.Add(Page(pageId, group.Key, ir with { Nodes = nodes,
                    Edges = ir.Edges.Where(edge => ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId)).ToArray() }));
                summaries.Add(new DiagramNode(StableIds.Create("summary", pageId),
                    $"{group.Key} · {nodes.Count(node => node.Kind is "condition" or "case")}개 분기 요소",
                    "detailRef", null, "unchanged", Confidence.Exact, nodes.SelectMany(node => node.EvidenceIds).Distinct().ToArray(),
                    "call", SourceFactIds: nodes.SelectMany(node => node.SourceFactIds ?? [node.Id]).Distinct().ToArray(),
                    DetailPageId: pageId, AbstractionKind: "method-reference"));
            }
            ir = ir with { Nodes = summaries, Edges = [], Notes = ir.Notes.Append("독립 메서드 사이에 실행 순서 화살표를 만들지 않습니다. 각 항목의 세부 보기에서 전체 분기를 확인하세요.").ToArray() };
        }
        else if (ir.Type is "class" or "code-relation")
        {
            if (ir.Type == "class")
            {
                foreach (var node in ir.Nodes)
                {
                    var pageId = StableIds.Create("members", node.Id);
                    pages.Add(Page(pageId, node.Label + " · 전체 멤버", ir with { Nodes = [node], Edges = [] }));
                }
                ir = ir with { Nodes = ir.Nodes.Select(node => node with { Details = (node.Details ?? []).Take(6).ToArray(),
                    DetailPageId = StableIds.Create("members", node.Id) }).ToArray(),
                    Notes = ir.Notes.Append("클래스별 수집된 전체 멤버는 세부 보기에서 확인하세요. 요약에는 변경 줄과 겹치는 멤버를 우선하여 최대 6개 선언을 표시합니다.").ToArray() };
            }
            else
            {
                foreach (var group in ir.Nodes.GroupBy(node => node.Group ?? ir.Title))
                {
                    var ids = group.Select(node => node.Id).ToHashSet();
                    var pageId = StableIds.Create("implementation", group.Key);
                    // Include neighboring implementations so cross-owner calls retain both endpoints.
                    var edges = ir.Edges.Where(edge => ids.Contains(edge.SourceId) || ids.Contains(edge.TargetId)).ToArray();
                    ids.UnionWith(edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }));
                    pages.Add(Page(pageId, group.Key, ir with { Nodes = ir.Nodes.Where(node => ids.Contains(node.Id)).ToArray(), Edges = edges }));
                }
                var overview = ir.Nodes.Where(node => node.Kind is "change" or "responsibility").Select(node => node with
                { DetailPageId = StableIds.Create("implementation", node.Group ?? ir.Title) }).ToArray();
                if (overview.Length > 0)
                {
                    var ids = overview.Select(node => node.Id).ToHashSet();
                    ir = ir with { Nodes = overview, Edges = ir.Edges.Where(edge => ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId)).ToArray() };
                }
            }
        }
        pages.Insert(0, new DiagramPage("overview", "핵심 요약", artifact with { Ir = ir, MermaidDsl = "" }));
        return UpdateCoverage(new DiagramViewDocument("overview", pages.SelectMany(Partition).ToArray(), []), bundle);
    }

    public static DiagramViewDocument UpdateCoverage(DiagramViewDocument document, EvidenceBundle bundle)
    {
        var coverage = bundle.ChangeIds.Select(changeId =>
        {
            var changeFacts = bundle.Facts.Where(fact => fact.ChangeIds.Contains(changeId)).ToArray();
            var type = document.Pages[0].Diagram.Ir.Type;
            var required = changeFacts.Where(fact => type switch
            {
                "sequence" => fact.Kind == "calls",
                "flowchart" => fact.Kind is "operation" or "call" or "condition" or "case" or "loop" or "return" or "break" or "continue" or "throw",
                _ => fact.Kind == "symbol"
            }).ToArray();
            // Target behavior is the primary obligation. Deleted-only changes use
            // the base facts and cannot be "covered" by an unrelated participant.
            if (required.Any(fact => fact.Span?.RevisionSha == bundle.TargetSha))
                required = required.Where(fact => fact.Span is null || fact.Span.RevisionSha == bundle.TargetSha).ToArray();
            var requiredIds = required.Select(fact => fact.Id).ToHashSet();
            var perPage = document.Pages.ToDictionary(page => page.Id, page => RenderedFacts(page.Diagram.Ir).ToHashSet());
            var pages = perPage.Where(pair => pair.Value.Overlaps(requiredIds)).Select(pair => pair.Key).ToArray();
            var covered = perPage.Values.SelectMany(ids => ids).ToHashSet();
            var missing = requiredIds.Except(covered).Order().ToArray();
            var state = requiredIds.Count == 0 ? "Unavailable" : missing.Length > 0 ? pages.Length > 0 ? "Partial" : "Unavailable" :
                perPage[document.OverviewPageId].IsSupersetOf(requiredIds) ? "Overview" : "Detail";
            return new ChangeCoverage(changeId, state, pages, state is "Overview" or "Detail" ? null :
                requiredIds.Count == 0 ? "이 변경을 이 유형으로 설명할 제어 흐름·호출·타입 근거가 없습니다." :
                $"필수 원본 사실 {requiredIds.Count}개 중 {missing.Length}개가 표시되지 않았습니다.", missing);
        }).ToArray();
        return document with { Coverage = coverage };
    }

    private static IEnumerable<string> RenderedFacts(DiagramIr ir)
    {
        if (ir.Type == "sequence")
        {
            var messages = ir.SequenceBlocks is null ? ir.Edges.Select(edge => edge.Id).ToHashSet() : MessageIds(ir.SequenceBlocks).ToHashSet();
            return ir.Edges.Where(edge => messages.Contains(edge.Id)).SelectMany(edge => edge.SourceFactIds ?? []);
        }
        return ir.Nodes.Where(node => node.Kind is not ("entry" or "exit" or "detailRef") && node.AbstractionKind != "page-boundary" &&
            // A class overview may hide the changed member. Only its full member
            // page can discharge the symbol obligation.
            !(ir.Type == "class" && node.DetailPageId is not null))
            .SelectMany(node => node.SourceFactIds ?? []);
    }

    private static IEnumerable<DiagramPage> Partition(DiagramPage page)
    {
        const int nodeLimit = 100;
        const int edgeLimit = 160;
        var ir = page.Diagram.Ir;
        if (ir.Nodes.Count <= nodeLimit && ir.Edges.Count <= edgeLimit) return [page];
        var parts = new List<DiagramPage>();
        if (ir.Type == "sequence")
        {
            var order = ir.SequenceBlocks is null ? ir.Edges.Select(edge => edge.Id) : MessageIds(ir.SequenceBlocks);
            var edgeMap = ir.Edges.ToDictionary(edge => edge.Id);
            var index = 0;
            foreach (var chunk in order.Chunk(nodeLimit / 2))
            {
                var edges = chunk.Select(id => edgeMap[id]).ToArray();
                var nodes = NodesFor(edges, ir.Nodes);
                var blocks = ir.SequenceBlocks is null ? SequenceStructure.FromEdges(edges) : SequenceStructure.Prune(ir.SequenceBlocks,
                    chunk.ToHashSet(), nodes.Select(node => node.Id).ToHashSet());
                var id = StableIds.Create(page.Id, "part", index++);
                parts.Add(Page(id, $"{page.Title} · {index}부", ir with { Nodes = nodes, Edges = edges, SequenceBlocks = blocks,
                    Notes = ir.Notes.Append("긴 시나리오의 연속 부분입니다. 경계의 분기·반복 문맥은 각 부분에 반복 표기되며 새 실행을 뜻하지 않습니다.").ToArray() }));
            }
            if (parts.Count == 0) return [page];
            var first = parts[0].Diagram.Ir.Nodes[0];
            var indexIr = ir with { Nodes = [first], Edges = [], SequenceBlocks = parts.Select(part =>
                new SequenceBlock(part.Id + "_ref", "note", part.Title, [], ParticipantIds: [first.Id], DetailPageId: part.Id)).ToArray() };
            return new[] { page with { Diagram = page.Diagram with { Ir = indexIr } } }.Concat(parts);
        }
        var chunks = ir.Nodes.Chunk(nodeLimit).ToArray();
        var home = chunks.SelectMany((chunk, index) => chunk.Select(node => (node.Id, Page: StableIds.Create(page.Id, "part", index, 0))))
            .ToDictionary(pair => pair.Id, pair => pair.Page);
        for (var index = 0; index < chunks.Length; index++)
        {
            var owned = chunks[index];
            var ownedIds = owned.Select(node => node.Id).ToHashSet();
            var edges = ir.Edges.Where(edge => ownedIds.Contains(edge.SourceId)).ToArray();
            var batches = edges.Length == 0 ? new[] { Array.Empty<DiagramEdge>() } : edges.Chunk(edgeLimit).ToArray();
            for (var batch = 0; batch < batches.Length; batch++)
            {
                var endpoints = batches[batch].SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).ToHashSet();
                var boundary = ir.Nodes.Where(node => !ownedIds.Contains(node.Id) && endpoints.Contains(node.Id)).Select(node => node with
                { Kind = "detailRef", Details = [], SourceFactIds = [], DetailPageId = home[node.Id], AbstractionKind = "page-boundary" });
                var id = StableIds.Create(page.Id, "part", index, batch);
                parts.Add(Page(id, $"{page.Title} · {index + 1}-{batch + 1}부", ir with
                { Nodes = owned.Concat(boundary).ToArray(), Edges = batches[batch],
                    Notes = ir.Notes.Append("다른 부분으로 이어지는 원본 관계는 상세 참조 노드로 표시합니다. 참조 노드는 새로운 처리 단계가 아닙니다.").ToArray() }));
            }
        }
        var references = parts.Select(part => new DiagramNode(StableIds.Create("part-ref", part.Id), part.Title,
            "detailRef", null, "unchanged", Confidence.Exact, [], DetailPageId: part.Id)).ToArray();
        var indexPage = page with { Diagram = page.Diagram with { Ir = ir with { Nodes = references, Edges = [] } } };
        // A very large page index is itself partitioned, without discarding links.
        if (references.Length <= nodeLimit) return new[] { indexPage }.Concat(parts);
        var nestedIndex = Partition(indexPage with { Id = page.Id + "_index" }).ToArray();
        nestedIndex[0] = nestedIndex[0] with { Id = page.Id };
        return nestedIndex.Concat(parts);
    }

    private static DiagramPage Page(string id, string title, DiagramIr ir) => new(id, title,
        new DiagramArtifact(Guid.NewGuid(), ir.Type, 1, ir with { Title = title }, "", DateTimeOffset.UtcNow));
    private static DiagramNode[] NodesFor(IEnumerable<DiagramEdge> edges, IReadOnlyList<DiagramNode> nodes)
    {
        var ids = edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).ToHashSet();
        return nodes.Where(node => ids.Contains(node.Id)).ToArray();
    }
    private static IEnumerable<string> MessageIds(IEnumerable<SequenceBlock> blocks) => blocks.SelectMany(block =>
        (block.EdgeId is null ? [] : new[] { block.EdgeId }).Concat(MessageIds(block.Children)));
}
