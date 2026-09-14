using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class ExecutionSequenceTests
{
    private static readonly string[] Calls = ["IsInitDataRequestStatus()", "Sleep(10)", "SendUnitIntervalTime()",
        "SetUnitMode()", "RequestUpdateVersion()", "Sleep(100)", "RequestUpdateFirmwareVersion()"];
    private const string Source = """
        bool SendInitDataRequest() {
            bool bRtn = false;
            if (IsInitDataRequestStatus() != true) return bRtn;
            Sleep(10);
            if (SendUnitIntervalTime() != enumFunctionResult.Success) return bRtn;
            if (SetUnitMode() != enumFunctionResult.Success) return bRtn;
            if (RequestUpdateVersion() != enumFunctionResult.Success) return bRtn;
            Sleep(100);
            if (RequestUpdateFirmwareVersion() != enumFunctionResunt.Success) return bRtn;
            bRtn = true;
            return bRtn;
        }
        """;
    private static DiagramIr Diagram(string source)
    {
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [new("a", "csharp", "a", source)]);
        var service = new CodeBlockProjectionService(new());
        var group = new CodeBlockGroupSelection("g", "g", ["a"]);
        Assert.True(service.Availability(graph, group).Single(a => a.Type == "sequence").Available);
        return service.Build(graph, [], group, new("s", "sequence", "balanced")).First().Diagram;
    }
    [Fact]
    public void CallReturnAndReferenceArgumentsKeepTheirSourceEvidenceWithoutDuplicateAssignments()
    {
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [new("a", "csharp", "a",
            "class Work { int Run(){int buffer=0; int result=Read(ref buffer); return result;} int Read(ref int output){output=2; return 1;} }")]);
        var owner = graph.Symbols.Single(s => s.Name.Split('.').Last() == "Run");
        var diagram = ExecutionSequenceProjection.Build(owner, graph, "TB");
        var call = Assert.Single(diagram.Edges, e => e.Type == "message");
        Assert.Equal("Read", call.Call!.Target);
        Assert.Equal(new[] { "ref buffer" }, call.Call.Arguments);
        Assert.Equal("result", call.Call.AssignedTo);
        Assert.Equal("int", call.Call.ReturnType);
        var output = Assert.Single(call.Call.Outputs);
        Assert.Equal("code", output.Basis);
        Assert.NotEmpty(output.EvidenceIds);
        var response = Assert.Single(diagram.Edges, e => e.Type == "response");
        Assert.Contains("result ← 반환값", response.Label);
        Assert.True(response.SourceFactIds!.Count > call.SourceFactIds!.Count);
        Assert.DoesNotContain(CodeBlockPlanValidation.ControlBlocks(diagram.SequenceBlocks!), b => b.Label.Contains("result=Read"));
    }

    [Fact]
    public void BundledOutputContractIsExplicitAndCannotOverrideAnInputDefinition()
    {
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [new("a", "csharp", "a",
            "void Run(){bool result=GetCommState(device, config);}")]) with { ApiContractBlockIds = ["a"] };
        var owner = graph.Symbols.Single(s => s.Name == "Run");
        var diagram = ExecutionSequenceProjection.Build(owner, graph, "TB");
        var edge = Assert.Single(diagram.Edges, e => e.Type == "message");
        Assert.Equal("api-contract", edge.Call!.Basis);
        Assert.StartsWith("성공 시", Assert.Single(edge.Call.Outputs).Description);
        Assert.Empty(edge.Call.Outputs[0].EvidenceIds);
        Assert.Contains("nf-winbase-getcommstate", edge.Call.ContractUrl!);
        var shared = new SharedSemanticProjection(true, []);
        shared.Add(new("s", diagram, new("s", "sequence", "balanced")));
        Assert.Contains(shared.Items.Values.Where(i => i.Kind == "message").SelectMany(i => i.Details), d => d.Contains("api-contract"));
        var noHeader = ExecutionSequenceProjection.Build(owner, graph with { ApiContractBlockIds = [] }, "TB");
        Assert.Equal("unresolved", noHeader.Edges.Single(e => e.Type == "message").Call!.Basis);
        var ownDefinition = owner with { Id = "own-api", Name = "GetCommState" };
        var shadowed = ExecutionSequenceProjection.Build(owner, graph with { Symbols = [owner, ownDefinition] }, "TB");
        Assert.Equal("unresolved", shadowed.Edges.Single(e => e.Type == "message").Call!.Basis);
    }
    [Fact]
    public void EachGuardCallIsEvaluatedOnceAndEachExitHasTheCorrectValue()
    {
        var diagram = Diagram(Source);
        Assert.Equal(Calls, diagram.Edges.Where(e => e.Type == "message").Select(e => e.OriginalExpression));
        Assert.Equal(5, diagram.Edges.Count(e => e.Type == "response"));
        Assert.Equal(new[] { "false", "false", "false", "false", "false", "true" }, diagram.Edges.Where(e => e.Type == "return").Select(e => e.ReturnValue));
        var blocks = diagram.SequenceBlocks!.Single().Children;
        Assert.Equal(5, blocks.Count(b => b.Kind == "break"));
        Assert.DoesNotContain(blocks, b => b.Kind == "alt");
        Assert.All(blocks.Where(b => b.Kind == "break"), b => { Assert.NotEmpty(b.EvidenceIds!); Assert.NotNull(b.OriginalExpression); });
        for (var failed = 0; failed < 6; failed++)
        {
            var trace = new List<string>();
            string? returned = null;
            var guard = 0;
            bool Run(IEnumerable<SequenceBlock> items)
            {
                foreach (var block in items)
                {
                    if (block.Kind == "break") { if (guard++ == failed) { Run(block.Children); return true; } }
                    else if (block.EdgeId is { } id)
                    {
                        var edge = diagram.Edges.Single(e => e.Id == id);
                        if (edge.Type == "message") trace.Add(edge.OriginalExpression!);
                        if (edge.Type == "return") { returned = edge.ReturnValue; return true; }
                    }
                }
                return false;
            }
            Run(blocks);
            Assert.Equal(Calls.Take(new[] { 1, 3, 4, 5, 7, 7 }[failed]), trace);
            Assert.Equal(failed == 5 ? "true" : "false", returned);
        }
        var mermaid = new MermaidCompiler(new()).Compile(diagram);
        Assert.Contains("break r1 != true", mermaid);
        Assert.Contains("enumFunctionResunt.Success", mermaid);
        Assert.DoesNotContain("alt ", mermaid);
    }
    [Theory]
    [InlineData("bool Run(){ bool result = false; Mutate(ref result); return result; }", "unknown")]
    [InlineData("bool Run(){ bool result = false; if(Ready())result=true; return result; }", "unknown")]
    [InlineData("bool Run(){ bool a=false; bool b=a; Ping(); return b; }", "false")]
    [InlineData("bool Run(){ bool a=false; if(Ready())a=true; else a=true; return a; }", "true")]
    [InlineData("bool Run(){ bool a=false; ref bool alias=ref a; alias=true; Ping(); return a; }", "unknown")]
    [InlineData("class A{bool flag; bool Run(){flag=false; Ping();return flag;}}", "unknown")]
    public void ReturnsUseOnlyConservativeLocalValues(string source, string expected)
    {
        var diagram = Diagram(source);
        Assert.Equal(expected, diagram.Edges.Last(e => e.Type == "return").ReturnValue);
    }
    [Theory]
    [InlineData("First() && Second()")]
    [InlineData("First() || Second()")]
    [InlineData("Ready() ? First() : Second()")]
    public void IfDecisionAndConditionEvaluationHaveDistinctFactsAndCompile(string condition)
    {
        var source = $"bool Run(){{if({condition})return false;Save();return true;}}";
        var graph = new SourceGraphAnalyzer().AnalyzeCSharpCodeBlocks(Guid.NewGuid(), [new("a", "csharp", "a", source)]);
        var events = Assert.Single(graph.Symbols).Execution!;
        var facts = ExecutionSequenceProjection.Flatten(events).ToArray();
        Assert.Equal(facts.Length, facts.Select(f => f.Id).Distinct().Count());
        Assert.Equal(condition, events[0].Expression);
        Assert.Equal(condition, source[events[0].StartOffset..events[0].EndOffset]);
        var steps = ExecutionMeaningValidation.Steps(events);
        var plan = new ExecutionMeaningPlan("조건을 평가하고 결과를 반환합니다", "implementation", steps,
            steps.Select(s => new ExecutionMeaningUnit("실행 사실을 보존합니다", "원문 조건과 호출을 보존합니다", [s.Id])).ToArray());
        Assert.Null(ExecutionMeaningValidation.Check(new("a", "Run", new("snapshot", "hash", "a", 1, 1), source, events), plan));
        var diagram = Diagram(source);
        Assert.Equal(new[] { "false", "true" }, diagram.Edges.Where(e => e.Type == "return").Select(e => e.ReturnValue));
        _ = new MermaidCompiler(new()).Compile(diagram);
    }

    [Fact]
    public void RepeatedNamesAndNestedCallsAreDistinctObservedEvents()
    {
        var diagram = Diagram("void Run(){ Save(); Save(); Outer(First(), Second()); }");
        Assert.Equal(new[] { "Save()", "Save()", "First()", "Second()", "Outer(First(), Second())" },
            diagram.Edges.Where(e => e.Type == "message").Select(e => e.OriginalExpression));
        Assert.Equal(diagram.Edges.Count, diagram.Edges.Select(e => e.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("void Run(){do { Work(); break; } while(Check()); After();}", "Work()|After()")]
    [InlineData("void Run(){for(;Check();Update()){Work();break;} After();}", "Check()|Work()|After()")]
    [InlineData("void Run(){do { Work(); continue; } while(Check()); After();}", "Work()|Check()|After()")]
    [InlineData("void Run(){for(;Check();Update()){Work();continue;} After();}", "Check()|Work()|Update()|After()")]
    public void LoopTransfersKeepOnlyReachableConditionAndUpdateCalls(string source, string expected)
    {
        var diagram = Diagram(source);
        Assert.Equal(expected.Split('|'), diagram.Edges.Where(e => e.Type == "message").Select(e => e.OriginalExpression));
        Assert.Null(SequenceStructure.Validate(diagram.SequenceBlocks!, diagram.Edges.Select(e => e.Id).ToHashSet(), diagram.Nodes.Select(n => n.Id).ToHashSet()));
        _ = new MermaidCompiler(new()).Compile(diagram);
    }

    [Fact]
    public void PreTestLoopAndShortCircuitKeepCallsInsideTheirEvaluationBranches()
    {
        var diagram = Diagram("void Run(){while(Ready() && Check()){if(Skip())continue; Work();} After();}");
        IEnumerable<SequenceBlock> All(IEnumerable<SequenceBlock> blocks) => blocks.SelectMany(b => new[] { b }.Concat(All(b.Children)));
        var loop = Assert.Single(All(diagram.SequenceBlocks!), b => b.Kind == "loop");
        var condition = Assert.Single(loop.Children, b => b.Kind == "alt" && b.OriginalExpression == "Ready() && Check()");
        Assert.Contains(All(condition.Children[0].Children), b => b.Kind == "opt" && b.Label.Contains("본문을 계속"));
        var transfers = All(loop.Children).Where(b => b.OriginalExpression == "continue;").ToArray();
        Assert.All(transfers, b => Assert.Equal(loop.Id, b.TerminationTarget));
        var shortCircuit = Assert.Single(loop.Children, b => b.Kind == "alt" && b.OriginalExpression == "Ready()");
        Assert.Contains(All(shortCircuit.Children[0].Children), b => b.EdgeId is { } id && diagram.Edges.Any(e => e.Id == id && e.OriginalExpression == "Check()"));
        Assert.Empty(shortCircuit.Children[1].Children);
        _ = new MermaidCompiler(new()).Compile(diagram);
    }
}
