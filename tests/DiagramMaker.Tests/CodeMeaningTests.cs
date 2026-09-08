using System.Text.Json;
using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class CodeMeaningTests
{
    private const string Before = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string After = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Example = """
        using System;
        using System.IO;
        using System.Text;
        using System.Collections.Generic;
        namespace SCTC_CONFIG.Tests;
        class AlarmCsvLineTests
        {
            string _tempRoot = "fixture";
            void Export_WritesNoDifferencesMessage_WhenRunAndDefaultAreSame()
            {
                string alarmDir = Path.Combine(_tempRoot, "Alarm");
                Directory.CreateDirectory(alarmDir);
                const string header = "AlarmID,AlarmName,Severity,Category,Module,SubModule,Description,Action,Recovery,Note";
                const string sameRow = "A001,OverTemperature,CRITICAL,Hardware,Sensor,TempSensor,Description,DisplayOnly:1,SetupStop:1,DisplayOnly:1";
                var panel = new AlarmPanelViewModel();
                panel.Items.Add(new AlarmFileItemViewModel(new AlarmFileModel
                {
                    FilePath = Path.Combine(alarmDir, "AlarmA.csv"),
                    FileName = "AlarmA.csv",
                    Lines = new List<string> { header, sameRow },
                    HeaderColumnCount = 10,
                    DataLineIndices = new List<int> { 1 },
                    InitialIsDisplayOnly = true
                }));
                var service = new AlarmStopModeDiffExportService();
                var result = service.Export(panel, new DateTime(2026, 8, 4, 12, 34, 56));
                string report = File.ReadAllText(result.FilePath, Encoding.UTF8);
                Assert.Equal(0, result.DifferenceCount);
                Assert.Equal("File,AlarmID,Line,StopMode_Run,StopMode_Default,KName", report);
            }
        }
        """;

    [Fact]
    public void ExportExample_PreservesOuterActionInitializersEncodingAndDistinctAssertions()
    {
        var (graph, comparison) = Analyze(Example);
        var flow = Assert.Single(graph.ControlFlows!);
        var export = Assert.Single(flow.Nodes, node => node.Context?.Target == "Export");
        Assert.Equal("service", export.Context!.Receiver);
        Assert.Equal("result", export.Context.AssignedTo);
        Assert.Contains("new DateTime(2026, 8, 4, 12, 34, 56)", export.Context.Arguments);
        Assert.Equal("call", export.Context.Purpose);
        Assert.Equal("AlarmStopModeDiffExportService", Assert.Single(flow.Nodes, node => node.Context?.AssignedTo == "service").Context!.CreatedType);
        var setup = Assert.Single(flow.Nodes, node => node.Context?.Target == "Add");
        Assert.Equal("prepare", setup.Context!.Purpose);
        Assert.Contains(setup.Context.Initializers, value => value.Contains("InitialIsDisplayOnly = true"));
        Assert.Contains(setup.Context.Definitions!, value => value.Name == "sameRow" && value.Statement.Contains("DisplayOnly:1"));
        Assert.Contains(setup.Context.Definitions!, value => value.Name == "header");
        var projected = Assert.Single(new DiagramProjectionService().Build("test", graph, comparison, ["flowchart"], 1, 1, false).Artifacts);
        Assert.Contains(projected.Ir.Nodes, node => node.Id == export.Id && node.Label.Contains("Export") && !node.Label.StartsWith("DateTime"));
        Assert.Contains(projected.Ir.Nodes, node => node.Label.Contains("파일 읽기") && node.Label.Contains("Encoding.UTF8"));
        Assert.Contains(projected.Ir.Nodes, node => node.Label.Contains("DifferenceCount = 0인지 검증"));
        Assert.Contains(projected.Ir.Nodes, node => node.Label.Contains("StopMode_Default,KName") && node.Label.Contains("검증"));
        var evidence = graph.Evidence.Single(item => item.Id == export.EvidenceIds[0]);
        Assert.Equal(export.Context.Span.StartOffset, evidence.StartOffset);
        Assert.Equal("blob", evidence.BlobOid);
        Assert.Equal(export.Context.Statement, Example[evidence.StartOffset!.Value..evidence.EndOffset!.Value]);
    }

    [Fact]
    public void FieldReads_PreserveFourArgumentsAssignmentsOrderAndSurvivingGuard()
    {
        const string source = """
            namespace SCTC_CONFIG;
            class AlarmCsvLine { public string GetField(int index) => "value"; }
            class AlarmStopModeDiffExportService {
                void Export(AlarmCsvLine parsedLine) {
                    foreach (var row in new[] { 1, 2 }) {
                        string runValue = parsedLine.GetField(7).Trim();
                        string defaultValue = parsedLine.GetField(9).Trim();
                        if (runValue == defaultValue) continue;
                        string alarmId = parsedLine.GetField(0).Trim();
                        string kName = parsedLine.GetField(1).Trim();
                    }
                }
            }
            """;
        var (graph, comparison) = Analyze(source);
        var calls = graph.Edges.Where(edge => edge.Label == "GetField").OrderBy(edge => edge.SequenceIndex).ToArray();
        Assert.Equal(4, calls.Length);
        Assert.Equal(new[] { "runValue", "defaultValue", "alarmId", "kName" }, calls.Select(edge => edge.Context!.AssignedTo));
        Assert.Equal(new[] { "7", "9", "0", "1" }, calls.Select(edge => Assert.Single(edge.Context!.Arguments)));
        Assert.Equal(4, calls.Select(edge => edge.Context!.Span.StartOffset).Distinct().Count());
        Assert.All(calls.Take(2), edge => Assert.DoesNotContain(edge.ControlPath!, scope => scope.Kind == "alt"));
        Assert.All(calls.Skip(2), edge => Assert.Contains(edge.ControlPath!, scope => scope.Label == "runValue == defaultValue" && scope.Branch == "else"));
        var diagram = Assert.Single(new DiagramProjectionService().Build("test", graph, comparison, ["sequence"], 1, 1, false).Artifacts).Ir;
        Assert.Contains(diagram.Nodes, node => node.Label == "AlarmCsvLine" && node.QualifiedName == "SCTC_CONFIG.AlarmCsvLine");
        Assert.Contains(diagram.Nodes, node => node.Label == "AlarmStopModeDiffExportService");
        Assert.Equal(new[] { "runValue", "defaultValue", "alarmId", "kName" }, diagram.Edges.Select(edge => edge.Context!.AssignedTo));
        Assert.All(diagram.Edges, edge => Assert.Contains(edge.Context!.AssignedTo!, edge.Label));
        Assert.Equal(diagram.Edges.Select(edge => edge.Id), SequenceStructure.MessageIds(diagram.SequenceBlocks!));
        var dsl = new MermaidCompiler(new DiagramValidator()).Compile(diagram);
        Assert.Contains("runValue == defaultValue", dsl);
        Assert.Contains(diagram.Notes, note => note.Contains("실제 실행 기록이 아닙니다"));
    }

    [Theory]
    [InlineData("return;")]
    [InlineData("throw new System.Exception();")]
    public void EarlyExit_ConditionsLaterCallsWithoutTreatingNestedLoopExitAsMethodExit(string transfer)
    {
        var (graph, _) = Analyze($"class S {{ void Run(bool stop) {{ if(stop) {{ {transfer} }} Save(); }} void Save() {{}} }}");
        var call = Assert.Single(graph.Edges, edge => edge.Label == "Save");
        Assert.Contains(call.ControlPath!, scope => scope.Label == "stop" && scope.Branch == "else");
        var (nested, _) = Analyze("class S { void Run(bool stop) { if(stop) { while(stop) { break; } } Save(); } void Save() {} }");
        Assert.Empty(Assert.Single(nested.Edges, edge => edge.Label == "Save").ControlPath!);
        var (unreachable, _) = Analyze("class S { void Run() { return; Save(); } void Save() {} }");
        Assert.DoesNotContain(unreachable.Edges, edge => edge.Label == "Save");
    }

    [Fact]
    public void SemanticMerge_AllowsPreparationButKeepsExportAndEveryAssertionSeparate()
    {
        var (graph, comparison) = Analyze(Example);
        var ir = Assert.Single(new DiagramProjectionService().Build("test", graph, comparison, ["flowchart"], 1, 1, false).Artifacts).Ir;
        var preparation = ir.Nodes.Where(node => node.Context?.Purpose == "prepare").ToArray();
        var plan = new DiagramPlan("테스트 항목을 내보내고 결과를 검증합니다.", ir.Nodes.Select(node => new SemanticElement(node.Id, "코드 기반 동작", [node.Id])).ToArray(), [], []);
        var folder = preparation.Take(2).Select(node => node.Id).ToArray();
        Assert.Null(InternalLlmClient.ValidatePlan(ir, Merge(plan, folder)));
        var export = ir.Nodes.Single(node => node.Context?.Target == "Export");
        var read = ir.Nodes.Single(node => node.Context?.Target == "ReadAllText");
        Assert.Equal("MergesCoreActionOrAssertion", InternalLlmClient.ValidatePlan(ir, Merge(plan, [export.Id, read.Id])));
        var assertions = ir.Nodes.Where(node => node.Context?.Purpose == "assertion").Select(node => node.Id).ToArray();
        Assert.Equal(2, assertions.Length);
        Assert.Equal("MergesCoreActionOrAssertion", InternalLlmClient.ValidatePlan(ir, Merge(plan, assertions)));
        var generic = plan with { Elements = plan.Elements.Select(element => element.NodeIds.Contains(assertions[0]) ? element with { Summary = "Equal 호출" } : element).ToArray() };
        Assert.Equal("GenericActionLabel", InternalLlmClient.ValidatePlan(ir, generic));
    }

    [Fact]
    public void ChangeExplanation_RequiresRelatedPageElementsAndBothRevisions()
    {
        var old = Example.Replace("Assert.Equal(\"File,AlarmID,Line,StopMode_Run,StopMode_Default,KName\", report);",
            "Assert.Contains(\"DifferenceCount: 0\", report);\n        Assert.Contains(\"차이 없음\", report);", StringComparison.Ordinal);
        var comparison = new GitComparison(Before, After, [new ChangedFile("Test.cs", null, ChangeKind.Modified, "old", "new", [], old, Example)]);
        var graph = new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison);
        var method = graph.Versions.Single(version => version.RevisionSha == After && version.QualifiedName.EndsWith(".Export_WritesNoDifferencesMessage_WhenRunAndDefaultAreSame"));
        var change = graph.Changes.Single(value => value.AfterSymbolVersionId == method.Id);
        var bundle = DiagramEvidenceBuilder.Build(graph, comparison, [change.Id]);
        var ir = Assert.Single(new DiagramProjectionService().Build("test", graph, comparison, ["flowchart"], 1, 1, false, new HashSet<string>([change.Id])).Artifacts).Ir;
        var node = ir.Nodes.Single(value => value.Context?.Target == "Equal" && value.Context.Arguments[0].StartsWith('"') && value.Context.Span.RevisionSha == After);
        var sources = bundle.Facts.Where(fact => fact.Kind == "source" && fact.ChangeIds.Contains(change.Id)).ToArray();
        var explanation = new PageChangeExplanation(change.Id,
            "문자열 포함 검증을 CSV 헤더 전체 일치 검증으로 변경했습니다. 차이 건수 0 검증은 유지됩니다.",
            sources.Select(fact => fact.Id).ToArray(), [node.Id], []);
        var plan = new DiagramPlan("CSV 내보내기 결과 검증", [], [], [], [explanation]);
        Assert.Null(InternalLlmClient.ValidateChanges(ir, plan, bundle.Facts));
        Assert.Equal("MissingRevisionEvidence", InternalLlmClient.ValidateChanges(ir, plan with { Changes = [explanation with
        { FactIds = sources.Where(fact => fact.Span!.RevisionSha == After).Select(fact => fact.Id).ToArray() }] }, bundle.Facts));
        Assert.Equal("InvalidChangeEvidence", InternalLlmClient.ValidateChanges(ir, plan with { Changes = [explanation with { FactIds = ["invented"] }] }, bundle.Facts));
        Assert.Equal("UnrelatedChangeElement", InternalLlmClient.ValidateChanges(ir, plan with { Changes = [explanation with { NodeIds = ["foreign"] }] }, bundle.Facts));
        Assert.Equal("MissingChangeCoverage", InternalLlmClient.ValidateChanges(ir, plan with { Changes = [] }, bundle.Facts));
        foreach (var revision in new[] { Before, After })
        {
            var lines = sources.Where(fact => fact.Span!.RevisionSha == revision).SelectMany(fact => Enumerable.Range(fact.Span!.StartLine, fact.Span.EndLine - fact.Span.StartLine + 1)).ToArray();
            Assert.Equal(lines.Length, lines.Distinct().Count());
        }
        Assert.Contains(sources, fact => fact.Span!.RevisionSha == Before && fact.Content!.Contains("Assert.Contains"));
        Assert.Contains(sources, fact => fact.Span!.RevisionSha == After && fact.Content!.Contains("Assert.Equal(0, result.DifferenceCount)"));
    }

    [Fact]
    public void ShortNames_DisambiguateSameNamedTypesAndKeepOriginalIdentities()
    {
        var (graph, comparison) = Analyze("namespace A { class Store { public void Save() {} } } namespace B { class Store { public void Save() {} } } class Caller { void Run(A.Store a, B.Store b) { a.Save(); b.Save(); } }");
        var ir = Assert.Single(new DiagramProjectionService().Build("test", graph, comparison, ["sequence"], 1, 1, false).Artifacts).Ir;
        Assert.Contains(ir.Nodes, node => node.Label == "A.Store" && node.QualifiedName == "A.Store");
        Assert.Contains(ir.Nodes, node => node.Label == "B.Store" && node.QualifiedName == "B.Store");
        Assert.Equal(ir.Nodes.Count, ir.Nodes.Select(node => node.Id).Distinct().Count());
    }

    [Fact]
    public void LegacyArtifact_DeserializesWithoutExplanationAndFallbackNeverClaimsMeaning()
    {
        var artifact = JsonSerializer.Deserialize<DiagramArtifact>("""{"id":"00000000-0000-0000-0000-000000000001","type":"flowchart","version":1,"ir":{"type":"flowchart","title":"old","nodes":[],"edges":[],"notes":[],"provenance":[]},"mermaidDsl":"flowchart LR","createdAt":"2026-01-01T00:00:00Z"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(artifact.Explanation);
        var result = DiagramExplanationBuilder.Fallback(artifact.Ir, new("hash", Before, After, [], [], []), "Disabled", ["LLM 비활성화"]);
        Assert.Equal("Disabled", result.Status);
        Assert.Empty(result.Changes);
        Assert.Contains("완료되지 않았습니다", result.Summary);
    }

    private static DiagramPlan Merge(DiagramPlan plan, string[] ids) => plan with { Elements =
        plan.Elements.Where(element => !element.NodeIds.Any(ids.Contains)).Append(new SemanticElement("merged", "준비 단계", ids)).ToArray() };
    private static (VersionedGraph, GitComparison) Analyze(string source)
    {
        var comparison = new GitComparison(Before, After, [new ChangedFile("Test.cs", null, ChangeKind.Added, null, "blob", [], null, source)]);
        return (new SourceGraphAnalyzer().Analyze(Guid.NewGuid(), comparison), comparison);
    }
}
