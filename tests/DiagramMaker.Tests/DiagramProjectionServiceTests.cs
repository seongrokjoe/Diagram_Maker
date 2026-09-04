using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class DiagramProjectionServiceTests
{
    [Fact]
    public void CurrentVersions_DeduplicatesIdentityUsingTargetRangeAndStablePathOrder()
    {
        var repositoryId = Guid.NewGuid();
        var baseSha = new string('a', 40);
        var targetSha = new string('b', 40);
        var identity = new SymbolIdentity("method", repositoryId, "cpp", "method", "method");
        var versions = new[]
        {
            new SymbolVersion("base", identity.Id, baseSha, "Base", "void Run()", "Z.cpp", 1, 100, "base"),
            new SymbolVersion("small", identity.Id, targetSha, "Small", "void Run()", "B.cpp", 5, 8, "small"),
            new SymbolVersion("wide-z", identity.Id, targetSha, "Wide Z", "void Run()", "Z.cpp", 5, 20, "wide-z"),
            new SymbolVersion("wide-a", identity.Id, targetSha, "Wide A", "void Run()", "A.cpp", 5, 20, "wide-a")
        };
        var graph = new VersionedGraph([identity], versions, [], [], []);

        var current = DiagramProjectionService.CurrentVersions(graph, new GitComparison(baseSha, targetSha, []));

        Assert.Equal("wide-a", Assert.Single(current).Id);
    }

    [Fact]
    public void Build_ChangedCallLine_AddsExactModifiedMarkerWithoutMarkingContextEdge()
    {
        var repositoryId = Guid.NewGuid();
        var baseSha = new string('a', 40);
        var targetSha = new string('b', 40);
        var identities = new[]
        {
            new SymbolIdentity("source", repositoryId, "csharp", "method", "source"),
            new SymbolIdentity("target", repositoryId, "csharp", "method", "target")
        };
        var versions = new[]
        {
            new SymbolVersion("source-version", "source", targetSha, "Source.Run", "void Run()", "Service.cs", 1, 10, "source"),
            new SymbolVersion("target-version", "target", targetSha, "Target.Save", "void Save()", "Service.cs", 12, 15, "target")
        };
        var edges = new[]
        {
            new GraphEdge("base-edge", "source", "target", "calls", "Save", Confidence.Exact, [], RevisionSha: baseSha, FilePath: "Service.cs", StartLine: 4, EndLine: 4),
            new GraphEdge("target-edge", "source", "target", "calls", "Save", Confidence.Exact, [], RevisionSha: targetSha, FilePath: "Service.cs", StartLine: 5, EndLine: 5),
            new GraphEdge("context-edge", "target", "source", "calls", "Run", Confidence.Exact, [], RevisionSha: targetSha, FilePath: "Service.cs", StartLine: 14, EndLine: 14)
        };
        var graph = new VersionedGraph(identities, versions, edges, [],
            [new SymbolChange("change", SymbolChangeKind.ModifyBody, null, "source-version", Confidence.Exact, [])]);
        var comparison = new GitComparison(baseSha, targetSha,
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new",
                [new DiffHunk(4, 1, 5, 1, "@@", [new DiffChangedRange(4, 1, 5, 1)])])]);

        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["sequence"], 1, 1, false);
        var diagram = Assert.Single(result.Artifacts).Ir;

        var changed = Assert.Single(diagram.Edges, edge => edge.Id == "target-edge");
        Assert.Equal(DiagramChangeKind.Modified, changed.ChangeMarker?.Kind);
        Assert.Equal(DiagramChangePrecision.Exact, changed.ChangeMarker?.Precision);
        Assert.Null(Assert.Single(diagram.Edges, edge => edge.Id == "context-edge").ChangeMarker);
    }

    [Fact]
    public void Build_DefaultImpactScope_ExcludesUnrelatedSymbolsAndIncludesExternalCaller()
    {
        var repositoryId = Guid.NewGuid();
        const string beforeService = "namespace Sample; public class Service { public void Run() { Save(); } private void Save() { } public void Unrelated() { } }";
        const string afterService = "namespace Sample; public class Service { public void Run() { Validate(); Save(); } private void Validate() { } private void Save() { } public void Unrelated() { } }";
        const string caller = "namespace Sample; public class Caller { public void Invoke(Service service) { service.Run(); } }";
        var comparison = new GitComparison(
            new string('a', 40), new string('b', 40),
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new", [new DiffHunk(1, 1, 1, 1, "@@")], beforeService, afterService)],
            [
                new RepositoryFileSnapshot("Caller.cs", new string('a', 40), "caller-old", caller),
                new RepositoryFileSnapshot("Caller.cs", new string('b', 40), "caller-new", caller)
            ]);
        var graph = new SourceGraphAnalyzer().Analyze(repositoryId, comparison);
        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["flowchart"], 1, 1, false);
        var diagram = Assert.Single(result.Artifacts).Ir;

        Assert.Contains(diagram.Nodes, node => node.Label.Contains("Service.Run", StringComparison.Ordinal));
        Assert.Contains(diagram.Nodes, node => node.Label.Contains("Caller.Invoke", StringComparison.Ordinal));
        Assert.DoesNotContain(diagram.Nodes, node => node.Label.Contains("Unrelated", StringComparison.Ordinal));
        Assert.Contains(diagram.Edges, edge => edge.Type == "calls");
    }

    [Fact]
    public void Build_StateType_IsReportedUnavailableWithoutTransitionEvidence()
    {
        var comparison = new GitComparison(
            new string('a', 40), new string('b', 40),
            [new ChangedFile("Service.cs", null, ChangeKind.Modified, "old", "new", [], "class Service { void Run() {} }", "class Service { void Run() { } }")]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);

        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["state"], 1, 1, false);

        var availability = Assert.Single(result.Availability);
        Assert.False(availability.Available);
        Assert.Contains("상태 전이", availability.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LargeImpactScope_NeverCreatesEdgesToTrimmedNodes()
    {
        var repositoryId = Guid.NewGuid();
        var identities = Enumerable.Range(0, 100)
            .Select(index => new SymbolIdentity($"n{index:D3}", repositoryId, "csharp", "method", $"method:N.C.M{index}/0"))
            .ToArray();
        var versions = identities.Select((identity, index) => new SymbolVersion(
            $"v{index:D3}", identity.Id, new string('b', 40), $"N.C.M{index}", $"void M{index}()", "C.cs", index + 1, index + 1, $"h{index}"))
            .ToArray();
        var edges = Enumerable.Range(0, 99).Select(index => new GraphEdge(
            $"e{index:D3}", identities[index].Id, identities[index + 1].Id, "calls", "calls", Confidence.Exact, []))
            .ToArray();
        var changes = identities.Select((identity, index) => new SymbolChange(
            $"c{index:D3}", SymbolChangeKind.ModifyBody, versions[index].Id, versions[index].Id, Confidence.Exact, []))
            .ToArray();
        var graph = new VersionedGraph(identities, versions, edges, [], changes);
        var comparison = new GitComparison(new string('a', 40), new string('b', 40), []);

        var result = new DiagramProjectionService().Build("large", graph, comparison, ["flowchart"], 3, 2, false);
        var diagram = Assert.Single(result.Artifacts).Ir;
        var nodeIds = diagram.Nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(80, diagram.Nodes.Count);
        Assert.All(diagram.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
        _ = new MermaidCompiler(new DiagramValidator()).Compile(diagram);
    }

    [Fact]
    public void Build_SelectedChangeAndPreset_ExcludeOtherChangedRootsAndApplyDirection()
    {
        var repositoryId = Guid.NewGuid();
        var identities = new[]
        {
            new SymbolIdentity("a", repositoryId, "cpp", "function", "A"),
            new SymbolIdentity("b", repositoryId, "cpp", "function", "B")
        };
        var versions = new[]
        {
            new SymbolVersion("va", "a", new string('b', 40), "A", "void A()", "A.cpp", 1, 2, "ha"),
            new SymbolVersion("vb", "b", new string('b', 40), "B", "void B()", "B.cpp", 1, 2, "hb")
        };
        var graph = new VersionedGraph(
            identities,
            versions,
            [new GraphEdge("edge", "a", "b", "calls", "B", Confidence.Exact, [])],
            [],
            [
                new SymbolChange("ca", SymbolChangeKind.ModifyBody, "va", "va", Confidence.Exact, []),
                new SymbolChange("cb", SymbolChangeKind.ModifyBody, "vb", "vb", Confidence.Exact, [])
            ]);
        var comparison = new GitComparison(new string('a', 40), new string('b', 40), []);
        var preset = new DiagramPresetCatalog().Resolve("flowchart", "flow-vertical-overview");

        var result = new DiagramProjectionService().Build(
            "sample", graph, comparison, ["flowchart"], 0, 0, false,
            new HashSet<string>(["ca"], StringComparer.Ordinal), preset,
            new DiagramStyleOverrides(CallerDepth: 0, CalleeDepth: 0));
        var diagram = Assert.Single(result.Artifacts).Ir;

        Assert.Equal("TB", diagram.Direction);
        Assert.Single(diagram.Nodes);
        Assert.Equal("A", diagram.Nodes[0].Label);
        Assert.Empty(diagram.Edges);
        _ = new MermaidCompiler(new DiagramValidator()).Compile(diagram);
    }

    [Fact]
    public void Build_CppFlowchart_ProjectsMethodControlFlowInsteadOfImpactBoxes()
    {
        var repositoryId = Guid.NewGuid();
        var sha = new string('b', 40);
        var identity = new SymbolIdentity("method", repositoryId, "cpp", "method", "function:Service::Run()");
        var version = new SymbolVersion("version", identity.Id, sha, "Service::Run", "void Run()", "Service.cpp", 1, 8, "hash");
        var graph = new VersionedGraph(
            [identity], [version], [], [],
            [new SymbolChange("change", SymbolChangeKind.ModifyBody, version.Id, version.Id, Confidence.Exact, [])],
            [new MethodControlFlow(identity.Id,
                [
                    new ControlFlowNode("start", "entry", "시작", 1, 1, []),
                    new ControlFlowNode("loop", "loop", "index < 2", 2, 6, []),
                    new ControlFlowNode("return", "return", "return", 7, 7, [])
                ],
                [
                    new ControlFlowEdge("start", "loop", "control", ""),
                    new ControlFlowEdge("loop", "loop", "loopBack", "다음 반복"),
                    new ControlFlowEdge("loop", "return", "control", "종료")
                ])]);
        var comparison = new GitComparison(new string('a', 40), sha, []);

        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["flowchart"], 1, 1, false);
        var diagram = Assert.Single(result.Artifacts).Ir;

        Assert.Contains(diagram.Nodes, node => node.Shape == "decision");
        Assert.Contains(diagram.Nodes, node => node.Shape == "return");
        Assert.Contains(diagram.Edges, edge => edge.Type == "loopBack");
        _ = new MermaidCompiler(new DiagramValidator()).Compile(diagram);
    }

    [Fact]
    public void Build_CodeRelation_GroupsMethodsByClassAndMarksIndirectEdge()
    {
        var repositoryId = Guid.NewGuid();
        var sha = new string('b', 40);
        var identities = new[]
        {
            new SymbolIdentity("type-a", repositoryId, "cpp", "class", "type:InterfaceCustom"),
            new SymbolIdentity("method-a", repositoryId, "cpp", "method", "function:InterfaceCustom::Run()"),
            new SymbolIdentity("type-b", repositoryId, "cpp", "class", "type:Opr_Xfer"),
            new SymbolIdentity("method-b", repositoryId, "cpp", "method", "function:Opr_Xfer::runOrgReturn()")
        };
        var versions = new[]
        {
            new SymbolVersion("vta", "type-a", sha, "InterfaceCustom", "class InterfaceCustom", "A.cpp", 1, 10, "ta"),
            new SymbolVersion("vma", "method-a", sha, "InterfaceCustom::Run", "void Run()", "A.cpp", 2, 8, "ma"),
            new SymbolVersion("vtb", "type-b", sha, "Opr_Xfer", "class Opr_Xfer", "B.cpp", 1, 10, "tb"),
            new SymbolVersion("vmb", "method-b", sha, "Opr_Xfer::runOrgReturn", "void runOrgReturn()", "B.cpp", 2, 8, "mb")
        };
        var graph = new VersionedGraph(
            identities, versions,
            [
                new GraphEdge("edge-1", "method-a", "method-b", "calls", "runOrgReturn", Confidence.Inferred, ["call-1"], 1, true, "RunFunction"),
                new GraphEdge("edge-2", "method-a", "method-b", "calls", "runOrgReturn", Confidence.Exact, ["call-2"], 2, true, "RunFunction")
            ],
            [], [new SymbolChange("change", SymbolChangeKind.ModifyBody, "vma", "vma", Confidence.Exact, [])]);
        var comparison = new GitComparison(new string('a', 40), sha, []);
        var preset = new DiagramPresetCatalog().Resolve("code-relation", "code-class-grouped");

        var result = new DiagramProjectionService().Build("sample", graph, comparison, ["code-relation"], 1, 1, false,
            new HashSet<string>(["change"], StringComparer.Ordinal), preset);
        var diagram = Assert.Single(result.Artifacts).Ir;

        Assert.Contains(diagram.Nodes, node => node.Group == "InterfaceCustom");
        Assert.Contains(diagram.Nodes, node => node.Group == "Opr_Xfer");
        var relationship = Assert.Single(diagram.Edges);
        Assert.True(relationship.IsIndirect);
        Assert.Contains("RunFunction", relationship.Label, StringComparison.Ordinal);
        Assert.Equal(["call-1", "call-2"], relationship.EvidenceIds);
        Assert.Equal(Confidence.Exact, relationship.Confidence);
        var dsl = new MermaidCompiler(new DiagramValidator()).Compile(diagram);
        Assert.Contains("-. 간접 API: RunFunction .->", dsl, StringComparison.Ordinal);

        var classDiagram = Assert.Single(new DiagramProjectionService().Build("sample", graph, comparison, ["class"], 1, 1, false,
            new HashSet<string>(["change"], StringComparer.Ordinal),
            new DiagramPresetCatalog().Resolve("class", "class-related")).Artifacts).Ir;
        Assert.Equal("InterfaceCustom", Assert.Single(classDiagram.Nodes, node => node.Id == "type-a").Label);
        Assert.DoesNotContain(classDiagram.Nodes, node => node.Label.Contains("변경:", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RevisionSides_UseTheMatchingVersionsAndChangeColors()
    {
        var repositoryId = Guid.NewGuid();
        var baseSha = new string('a', 40);
        var targetSha = new string('b', 40);
        var identities = new[]
        {
            new SymbolIdentity("source", repositoryId, "cpp", "method", "function:Service::Run()"),
            new SymbolIdentity("target", repositoryId, "cpp", "method", "function:Store::Save()")
        };
        var versions = new[]
        {
            new SymbolVersion("source-base", "source", baseSha, "Service::RunOld", "void Run()", "Service.cpp", 1, 8, "old"),
            new SymbolVersion("source-target", "source", targetSha, "Service::RunNew", "void Run()", "Service.cpp", 1, 8, "new"),
            new SymbolVersion("target-base", "target", baseSha, "Store::Save", "void Save()", "Store.cpp", 1, 4, "same"),
            new SymbolVersion("target-target", "target", targetSha, "Store::Save", "void Save()", "Store.cpp", 1, 4, "same")
        };
        var edges = new[]
        {
            new GraphEdge("base-call", "source", "target", "calls", "Save", Confidence.Exact, [], RevisionSha: baseSha,
                FilePath: "Service.cpp", StartLine: 4, EndLine: 4),
            new GraphEdge("target-call", "source", "target", "calls", "Save", Confidence.Exact, [], RevisionSha: targetSha,
                FilePath: "Service.cpp", StartLine: 5, EndLine: 5)
        };
        var graph = new VersionedGraph(identities, versions, edges, [],
            [new SymbolChange("change", SymbolChangeKind.ModifyBody, "source-base", "source-target", Confidence.Exact, [])]);
        var comparison = new GitComparison(baseSha, targetSha,
            [new ChangedFile("Service.cpp", null, ChangeKind.Modified, "old", "new",
                [new DiffHunk(4, 1, 5, 1, "@@", [new DiffChangedRange(4, 1, 5, 1)])])]);
        var projection = new DiagramProjectionService();

        var baseDiagram = Assert.Single(projection.Build("sample", graph, comparison, ["sequence"], 1, 1, false,
            new HashSet<string>(["change"]), focusOnChanges: true, revisionSide: DiagramRevisionSide.Base).Artifacts).Ir;
        var targetDiagram = Assert.Single(projection.Build("sample", graph, comparison, ["sequence"], 1, 1, false,
            new HashSet<string>(["change"]), focusOnChanges: true, revisionSide: DiagramRevisionSide.Target).Artifacts).Ir;

        Assert.Contains(baseDiagram.Nodes, node => node.Label == "Service::RunOld");
        Assert.DoesNotContain(baseDiagram.Nodes, node => node.Label == "Service::RunNew");
        Assert.Contains(targetDiagram.Nodes, node => node.Label == "Service::RunNew");
        Assert.DoesNotContain(targetDiagram.Nodes, node => node.Label == "Service::RunOld");
        Assert.Equal(DiagramChangeKind.Modified, Assert.Single(baseDiagram.Edges).ChangeMarker?.Kind);
        Assert.Equal(DiagramChangeKind.Modified, Assert.Single(targetDiagram.Edges).ChangeMarker?.Kind);
        Assert.Contains("Base", baseDiagram.Title, StringComparison.Ordinal);
        Assert.Contains("Target", targetDiagram.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SequencePresets_ApplyDifferentEvidenceBoundDepths()
    {
        var repositoryId = Guid.NewGuid();
        var sha = new string('b', 40);
        var identities = new[]
        {
            new SymbolIdentity("caller", repositoryId, "cpp", "method", "function:A::Call()"),
            new SymbolIdentity("changed", repositoryId, "cpp", "method", "function:B::Run()"),
            new SymbolIdentity("callee", repositoryId, "cpp", "method", "function:C::Save()")
        };
        var versions = identities.Select((identity, index) => new SymbolVersion(
            $"v{index}", identity.Id, sha, identity.SemanticKey, "void Method()", $"F{index}.cpp", 1, 2, $"h{index}")).ToArray();
        var graph = new VersionedGraph(
            identities, versions,
            [
                new GraphEdge("e1", "caller", "changed", "calls", "Run", Confidence.Exact, []),
                new GraphEdge("e2", "changed", "callee", "calls", "Save", Confidence.Exact, [])
            ],
            [], [new SymbolChange("change", SymbolChangeKind.ModifyBody, "v1", "v1", Confidence.Exact, [])]);
        var comparison = new GitComparison(new string('a', 40), sha, []);
        var catalog = new DiagramPresetCatalog();
        var projection = new DiagramProjectionService();

        var focused = projection.Build("sample", graph, comparison, ["sequence"], 1, 1, false,
            new HashSet<string>(["change"]), catalog.Resolve("sequence", "sequence-focused"));
        var callerContext = projection.Build("sample", graph, comparison, ["sequence"], 1, 1, false,
            new HashSet<string>(["change"]), catalog.Resolve("sequence", "sequence-caller-context"));

        Assert.Equal(2, Assert.Single(focused.Artifacts).Ir.Nodes.Count);
        Assert.Equal(3, Assert.Single(callerContext.Artifacts).Ir.Nodes.Count);
    }
}
