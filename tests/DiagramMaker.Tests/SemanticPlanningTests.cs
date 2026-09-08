using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class SemanticPlanningTests
{
    private static readonly string BaseSha = new('a', 40);
    private static readonly string TargetSha = new('b', 40);

    [Fact]
    public void CSharpCalls_PreserveRepeatedCallsNestedOrderAndOverloadBinding()
    {
        var graph = Analyze("class S { void Run() { Save(Parse()); Save(1); Save(2); Save(\"x\"); } int Parse() => 0; void Save(int n) {} void Save(string s) {} }");
        var run = graph.Versions.Single(version => version.QualifiedName == "S.Run");
        var calls = graph.Edges.Where(edge => edge.FromIdentityId == run.IdentityId && edge.Type == "calls").OrderBy(edge => edge.SequenceIndex).ToArray();
        Assert.Equal(5, calls.Length);
        Assert.Equal(5, calls.Select(edge => edge.Id).Distinct().Count());
        Assert.Equal("S.Parse", graph.Versions.Single(version => version.IdentityId == calls[0].ToIdentityId).QualifiedName);
        Assert.Equal(calls[1].ToIdentityId, calls[2].ToIdentityId);
        Assert.NotEqual(calls[2].ToIdentityId, calls[4].ToIdentityId);
        Assert.All(calls, edge => Assert.Equal(Confidence.Exact, edge.Confidence));
        Assert.Equal(5, calls.SelectMany(edge => edge.EvidenceIds).Distinct().Count());
    }

    [Fact]
    public void CSharpSwitchWithoutDefault_PreservesNoMatchAndBreakExit()
    {
        var graph = Analyze("class S { void Run(int n) { switch(n) { case 1: n++; break; case 2: return; } Save(); } void Save() {} }");
        var flow = Flow(graph, "S.Run");
        var decision = flow.Nodes.Single(node => node.Label.StartsWith("switch"));
        var save = flow.Nodes.Single(node => node.Label == "Save();");
        Assert.Contains(flow.Edges, edge => edge.SourceId == decision.Id && edge.TargetId == save.Id && edge.Label == "일치 없음");
        Assert.Contains(flow.Edges, edge => flow.Nodes.Any(node => node.Id == edge.SourceId && node.Kind == "break") && edge.TargetId == save.Id);
        Assert.DoesNotContain(flow.Edges, edge => flow.Nodes.Any(node => node.Id == edge.SourceId && node.Kind == "return") && edge.TargetId == save.Id);
    }

    [Fact]
    public void CSharpDoWhile_EntersBodyBeforeConditionAndSkipsUnreachableStatements()
    {
        var graph = Analyze("class S { void Run() { do { Save(); } while(false); return; Save(); } void Save() {} }");
        var flow = Flow(graph, "S.Run");
        var entry = flow.Nodes.Single(node => node.Kind == "entry");
        var first = flow.Edges.Single(edge => edge.SourceId == entry.Id);
        Assert.NotEqual("loop", flow.Nodes.Single(node => node.Id == first.TargetId).Kind);
        Assert.Single(flow.Nodes, node => node.Label == "Save();");
        var loop = flow.Nodes.Single(node => node.Kind == "loop");
        Assert.Contains(flow.Edges, edge => edge.TargetId == loop.Id && edge.Type == "loopBack");
    }

    [Fact]
    public void CSharpOwnershipAndInterfaceRelationship_AreExplicit()
    {
        var graph = Analyze("interface IStore { void Save(); } class Store : IStore { private int count; public void Save() { count++; } }");
        var owner = graph.Versions.Single(version => version.QualifiedName == "Store");
        var method = graph.Versions.Single(version => version.QualifiedName == "Store.Save");
        Assert.Equal(owner.IdentityId, method.OwnerIdentityId);
        Assert.Contains(graph.Edges, edge => edge.FromIdentityId == owner.IdentityId && edge.Type == "implements");
    }

    [Fact]
    public void EvidenceBundle_ContainsBothRevisionSourcesAndStableInputHash()
    {
        var repository = Guid.NewGuid();
        var comparison = new GitComparison(BaseSha, TargetSha,
            [new ChangedFile("S.cs", null, ChangeKind.Modified, "old", "new", [], "class S { int Run() => 1; }", "class S { int Run() => 2; }")]);
        var graph = new SourceGraphAnalyzer().Analyze(repository, comparison);
        var ids = graph.Changes.Select(change => change.Id).ToArray();
        var first = DiagramEvidenceBuilder.Build(graph, comparison, ids);
        var second = DiagramEvidenceBuilder.Build(graph, comparison, ids.Reverse().ToArray());
        Assert.Equal(first.Hash, second.Hash);
        Assert.Contains(first.Facts, fact => fact.Kind == "source" && fact.Span?.RevisionSha == BaseSha && fact.Content!.Contains("=> 1"));
        Assert.Contains(first.Facts, fact => fact.Kind == "source" && fact.Span?.RevisionSha == TargetSha && fact.Content!.Contains("=> 2"));
        Assert.NotEqual(first.Hash, DiagramEvidenceBuilder.Build(graph, comparison with
        { Files = comparison.Files.Select(file => file with { BeforeContent = null, AfterContent = null }).ToArray() }, ids).Hash);
    }

    [Fact]
    public void SemanticPlan_OnlyMergesConsecutiveBasicBlockOperations()
    {
        var ir = new DiagramIr("flowchart", "flow", [Node("a"), Node("b"), Node("c", "condition")],
            [Edge("ab", "a", "b"), Edge("bc", "b", "c")], [], []);
        var safe = new DiagramPlan("설명", [new("merged", "Header 정보 설정", ["a", "b"]), new("condition", "길이 확인", ["c"])], [], []);
        Assert.Null(InternalLlmClient.ValidatePlan(ir, safe));
        var result = InternalLlmClient.ApplyPlan(ir, safe);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Single(result.Edges);
        Assert.Equal(["a", "b"], result.Nodes[0].SourceFactIds);
        var invalid = safe with { Elements = [new("all", "처리", ["a", "b", "c"])] };
        Assert.Equal("UnsafeAbstraction", InternalLlmClient.ValidatePlan(ir, invalid));
        Assert.Equal("CrossesControlBoundary", InternalLlmClient.ValidatePlan(ir with { Edges = [.. ir.Edges, Edge("ac", "a", "c")] }, safe));
        Assert.Equal("MissingNodeCoverage", InternalLlmClient.ValidatePlan(ir, safe with { Elements = [safe.Elements[0]] }));
    }

    [Fact]
    public void SequenceCompiler_EmitsOneAltForMultipleCallsAndNoInventedReturns()
    {
        var edges = new[] { Edge("a", "x", "y") with { ControlPath = [new("if", "alt", "ready", "then")] },
            Edge("b", "x", "y") with { ControlPath = [new("if", "alt", "ready", "then")] },
            Edge("c", "y", "x") with { ControlPath = [new("if", "alt", "ready", "else")] } };
        var ir = new DiagramIr("sequence", "calls", [Node("x"), Node("y")], edges, [], [], SequenceBlocks: SequenceStructure.FromEdges(edges));
        var output = new MermaidCompiler(new DiagramValidator()).Compile(ir);
        Assert.Single(output.Split('\n'), line => line.TrimStart().StartsWith("alt "));
        Assert.Single(output.Split('\n'), line => line.TrimStart().StartsWith("else "));
        Assert.DoesNotContain("return", output);
        var pruned = SequenceStructure.Prune(ir.SequenceBlocks!, new HashSet<string>(), new HashSet<string>(["x"]));
        Assert.Empty(pruned);
    }

    [Fact]
    public void Overview_DetailRetainsEveryOriginalFlowNodeAndEdge()
    {
        var nodes = Enumerable.Range(0, 30).Select(index => Node("n" + index) with { Group = "S.Run" }).ToArray();
        var edges = Enumerable.Range(1, 29).Select(index => Edge("e" + index, "n" + (index - 1), "n" + index)).ToArray();
        var ir = new DiagramIr("flowchart", "large", nodes, edges, [], []);
        var artifact = new DiagramArtifact(Guid.NewGuid(), ir.Type, 1, ir, "", DateTimeOffset.UtcNow);
        var bundle = new EvidenceBundle("hash", BaseSha, TargetSha, ["change"],
            [new SourceFact("n12", "operation", "changed", ["change"], [], null)], []);
        var document = DiagramDocumentBuilder.Build(artifact, bundle);
        var overview = document.Pages.Single(page => page.Id == document.OverviewPageId);
        var reference = Assert.Single(overview.Diagram.Ir.Nodes);
        var detail = document.Pages.Single(page => page.Id == reference.DetailPageId);
        Assert.Equal(nodes, detail.Diagram.Ir.Nodes);
        Assert.Equal(edges, detail.Diagram.Ir.Edges);
        Assert.Empty(overview.Diagram.Ir.Edges);
        Assert.NotEqual("Unavailable", Assert.Single(document.Coverage).State);
    }

    [Fact]
    public void MissingCfg_IsUnavailableInsteadOfDisguisedCallGraph()
    {
        var graph = new VersionedGraph([], [], [], [], []);
        var result = new DiagramProjectionService().Build("test", graph, new GitComparison(BaseSha, TargetSha, []), ["flowchart"], 1, 1, false);
        Assert.Empty(result.Artifacts);
        Assert.False(Assert.Single(result.Availability).Available);
    }

    [Fact]
    public void LargeFlow_PagesPreserveAllFactsAndCrossPageEdges()
    {
        var nodes = Enumerable.Range(0, 650).Select(index => Node("n" + index)).ToArray();
        var edges = Enumerable.Range(1, 649).Select(index => Edge("e" + index, "n" + (index - 1), "n" + index)).ToArray();
        var ir = new DiagramIr("flowchart", "large", nodes, edges, [], []);
        var document = DiagramDocumentBuilder.Build(new DiagramArtifact(Guid.NewGuid(), ir.Type, 1, ir, "", DateTimeOffset.UtcNow),
            new EvidenceBundle("hash", BaseSha, TargetSha, [], [], []));
        Assert.Equal(650, document.Pages.SelectMany(page => page.Diagram.Ir.Nodes)
            .Where(node => node.Kind != "detailRef").Select(node => node.Id).Distinct().Count());
        Assert.Equal(edges.Select(edge => edge.Id).Order(), document.Pages.SelectMany(page => page.Diagram.Ir.Edges).Select(edge => edge.Id).Distinct().Order());
        Assert.All(document.Pages, page => new DiagramValidator().Validate(page.Diagram.Ir));
        Assert.All(document.Pages.SelectMany(page => page.Diagram.Ir.Nodes).Where(node => node.DetailPageId is not null),
            node => Assert.Contains(document.Pages, page => page.Id == node.DetailPageId));
    }

    [Fact]
    public void SequenceCoverage_RequiresAllActualEventsNotParticipantEvidence()
    {
        var nodes = new[] { Node("a") with { SourceFactIds = ["call1", "call2"], EvidenceIds = ["shared"] }, Node("b") };
        var edges = new[] { Edge("e1", "a", "b") with { SourceFactIds = ["call1"] }, Edge("e2", "a", "b") with { SourceFactIds = ["call2"] } };
        var ir = new DiagramIr("sequence", "calls", nodes, edges, [], [], SequenceBlocks:
            [new("scenario", "scenario", "calls", SequenceStructure.FromEdges(edges))]);
        var bundle = new EvidenceBundle("hash", BaseSha, TargetSha, ["change"],
            [new("call1", "calls", "one", ["change"], ["shared"], null), new("call2", "calls", "two", ["change"], ["shared"], null)], []);
        var document = DiagramDocumentBuilder.Build(new DiagramArtifact(Guid.NewGuid(), ir.Type, 1, ir, "", DateTimeOffset.UtcNow), bundle);
        Assert.Equal("Detail", Assert.Single(document.Coverage).State);
        var partial = DiagramDocumentBuilder.UpdateCoverage(document with { Pages = [document.Pages[0]] }, bundle);
        Assert.Equal("Partial", Assert.Single(partial.Coverage).State);
        Assert.Equal(["call2"], partial.Coverage[0].MissingFactIds);
    }

    [Fact]
    public void SequenceValidation_RejectsMissingDuplicateOrUnknownEventsAndKeepsManualEvents()
    {
        var edge = Edge("e", "a", "b");
        var ir = new DiagramIr("sequence", "calls", [Node("a"), Node("b")], [edge], [], [], SequenceBlocks: []);
        var validator = new DiagramValidator();
        Assert.Equal("InvalidSequenceStructure", validator.GetFailureKind(ir));
        var blocks = SequenceStructure.FromEdges([edge]);
        Assert.Null(validator.GetFailureKind(ir with { SequenceBlocks = blocks }));
        Assert.NotNull(validator.GetFailureKind(ir with { SequenceBlocks = [.. blocks, blocks[0] with { Id = "duplicate" }] }));
        var added = Edge("manual", "b", "a");
        var edited = SequenceStructure.ApplyEdit(blocks, [edge, added], new HashSet<string>(["a", "b"]));
        Assert.Null(validator.GetFailureKind(ir with { Edges = [edge, added], SequenceBlocks = edited }));
        Assert.Equal(2, SequenceStructure.MessageIds(edited).Count());
    }

    [Fact]
    public void SemanticMerge_RetainsLaterChangeWithoutInventingAnExactRange()
    {
        var changed = Node("b") with { Status = "modified", ChangeMarker =
            new(DiagramChangeKind.Modified, DiagramChangePrecision.Exact, "S.cs", 20, 20, ["evidence"]) };
        var ir = new DiagramIr("flowchart", "merge", [Node("a"), changed], [Edge("e", "a", "b")], [], []);
        var result = InternalLlmClient.ApplyPlan(ir, new DiagramPlan("summary", [new("merged", "헤더 설정", ["a", "b"])], [], []));
        var node = Assert.Single(result.Nodes);
        Assert.Equal("modified", node.Status);
        Assert.NotNull(node.ChangeMarker);
        Assert.Equal(DiagramChangePrecision.Symbol, node.ChangeMarker.Precision);
        Assert.Null(node.ChangeMarker.StartLine);
    }

    [Fact]
    public void EvidenceBundle_IncludesExplicitOwnerWhenOnlyMethodChanges()
    {
        var comparison = new GitComparison(BaseSha, TargetSha,
            [new ChangedFile("S.cs", null, ChangeKind.Modified, "old", "new", [], "class S { int Run() => 1; }", "class S { int Run() => 2; }")]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var method = graph.Versions.Single(version => version.QualifiedName == "S.Run" && version.RevisionSha == TargetSha);
        var change = graph.Changes.Single(item => item.AfterSymbolVersionId == method.Id);
        var bundle = DiagramEvidenceBuilder.Build(graph, comparison, [change.Id]);
        var owner = graph.Versions.Single(version => version.IdentityId == method.OwnerIdentityId && version.RevisionSha == TargetSha);
        Assert.Contains(bundle.Facts, fact => fact.Id == owner.Id);
        Assert.Contains(bundle.Facts, fact => fact.Kind == "source" && fact.Span!.RevisionSha == TargetSha && fact.Label == owner.QualifiedName);
    }

    private static DiagramNode Node(string id, string kind = "operation") => new(id, id, kind, "group", "unchanged", Confidence.Exact, [], SourceFactIds: [id]);

    [Fact]
    public void CSharpFor_ContinueRunsIncrementAndDefaultReturnHasNoFallthrough()
    {
        var graph = Analyze("class S { void Run(int n) { for(int i=0; i<3; i++) { if(n==0) continue; n++; } switch(n) { case 1: return; default: return; } n++; } }");
        var flow = Flow(graph, "S.Run");
        var increment = flow.Nodes.Single(node => node.Label == "i++");
        var continued = flow.Nodes.Single(node => node.Kind == "continue");
        Assert.Contains(flow.Edges, edge => edge.SourceId == continued.Id && edge.TargetId == increment.Id);
        Assert.Single(flow.Nodes, node => node.Label == "n++;");
        Assert.Equal(2, flow.Nodes.Count(node => node.Kind == "case"));
    }

    [Fact]
    public void UnsupportedExceptionFlow_IsNotReportedAsStraightLineControl()
    {
        var graph = Analyze("class S { void Run() { try { Save(); } finally { Save(); } } void Save() {} }");
        var method = graph.Versions.Single(version => version.QualifiedName == "S.Run");
        Assert.DoesNotContain(graph.ControlFlows!, flow => flow.IdentityId == method.IdentityId);
    }

    [Fact]
    public void CppSameLineCalls_HaveDistinctBlobAndOffsetEvidence()
    {
        var source = new CppSymbolFact("function:Run()", "Run", "Run", "function", 0, "void Run()", "S.cpp", null, 1, 1, "run",
            [new("Save", "Save", 0, 1, 1, StartOffset: 13, EndOffset: 19), new("Save", "Save", 0, 1, 2, StartOffset: 21, EndOffset: 27)], []);
        var target = new CppSymbolFact("function:Save()", "Save", "Save", "function", 0, "void Save()", "S.cpp", null, 2, 2, "save", [], []);
        var index = new CppSourceIndex("test", [source, target],
            [new(source.SemanticKey, target.SemanticKey, "calls", "Save", Confidence.Exact, "S.cpp", 1, 1),
             new(source.SemanticKey, target.SemanticKey, "calls", "Save", Confidence.Exact, "S.cpp", 1, 2)], [], [], 0, 1, 100, false, []);
        var comparison = new GitComparison(BaseSha, TargetSha,
            [new ChangedFile("S.cpp", null, ChangeKind.Added, null, "new-blob", [], null, "void Run() { Save(); Save(); }\nvoid Save() {}")]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison, index);
        var ids = graph.Edges.Where(edge => edge.Type == "calls").SelectMany(edge => edge.EvidenceIds).ToArray();
        Assert.Equal(2, ids.Distinct().Count());
        var evidence = graph.Evidence.Where(item => ids.Contains(item.Id)).OrderBy(item => item.StartOffset).ToArray();
        Assert.All(evidence, item => Assert.Equal("new-blob", item.BlobOid));
        Assert.Equal(new int?[] { 13, 21 }, evidence.Select(item => item.StartOffset));
    }

    [Fact]
    public void Compiler_RejectsAmbiguousRenderIdentityAndEscapesSequenceStatementSeparators()
    {
        var invalid = new DiagramIr("flowchart", "collision", [Node("a-b"), Node("a_b")], [], [], []);
        Assert.Equal("AmbiguousRenderId", new DiagramValidator().GetFailureKind(invalid));
        var edge = Edge("event", "a", "b") with { Label = "처리; participant injected as <script>" };
        var ir = new DiagramIr("sequence", "safe", [Node("a"), Node("b")], [edge], [], []);
        var dsl = new MermaidCompiler(new DiagramValidator()).Compile(ir);
        Assert.DoesNotContain("; participant", dsl);
        Assert.DoesNotContain("<script>", dsl);
        Assert.Contains("처리； participant", dsl);
    }
    private static DiagramEdge Edge(string id, string source, string target) => new(id, source, target, "control", id, "unchanged", Confidence.Exact, []);
    private static VersionedGraph Analyze(string source) => new SourceGraphAnalyzer().Analyze(Guid.NewGuid(),
        new GitComparison(BaseSha, TargetSha, [new ChangedFile("S.cs", null, ChangeKind.Added, null, "new", [], null, source)]));
    private static MethodControlFlow Flow(VersionedGraph graph, string name) => graph.ControlFlows!.Single(flow =>
        flow.IdentityId == graph.Versions.Single(version => version.QualifiedName == name).IdentityId);
}
