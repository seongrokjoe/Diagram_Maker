using System.Text.Json;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalDesignTests
{
    internal const string Prompt = "문이 열려 있으면 장비 운전을 차단한다.";
    internal static NaturalRequirements Requirements() => new("장비 운전", ["장비"],
        [new("r1", Prompt, "interlock", "explicit", "", SourceRangeIds: NaturalRequirementEvidence.Prepare(Prompt).Select(r => r.Id).ToArray())],
        Scenarios: [new("s1", "장비 운전", ["r1"], NaturalRequirementEvidence.Prepare(Prompt).Select(r => r.Id).ToArray())], Questions: []);
    internal static NaturalDesign Design(string type)
    {
        string Key(string value) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value));
        NaturalDesignNode Node(string id, string kind, NaturalMember[]? members = null) =>
            new(Key(id), id, kind, "", members ?? [], [], ["r1"], false);
        NaturalDesignEdge Edge(string id, string from, string to, string label, string guard = "", ControlScope[]? controls = null) =>
            new(id, Key(from), Key(to), type == "state" ? "transition" : type == "sequence" ? "message" : "flow", label,
                "", guard, "", controls ?? [], ["r1"], false);
        return type switch
        {
            "class" => new("장비 설계", [Node("장비", "class", [new("doorOpen", "field", "private", "bool", [], []),
                new("Run", "method", "public", "void", [], ["문이 닫혀 있어야 한다"])])], [], []),
            "state" => new("장비 상태", [Node("시작", "initial"), Node("대기", "state"), Node("운전", "state"), Node("종료", "final")],
                [Edge("e1", "시작", "대기", "시작"), Edge("e2", "대기", "운전", "운전 요청", "문이 닫힘"), Edge("e3", "운전", "종료", "정지")], []),
            "sequence" => new("장비 호출", [Node("작업자", "participant"), Node("장비", "participant")],
                [Edge("e1", "작업자", "장비", "운전 요청", controls: [new("check", "alt", "문 상태", "닫힘")]),
                 Edge("e2", "장비", "작업자", "운전 차단", controls: [new("check", "alt", "문 상태", "열림")])], []),
            _ => new("장비 흐름", [Node("문이 닫혔는가", "decision"), Node("운전", "operation"), Node("차단", "terminal")],
                [Edge("e1", "문이 닫혔는가", "운전", "예"), Edge("e2", "문이 닫혔는가", "차단", "아니요")], [])
        };
    }

    [Theory]
    [InlineData("class")]
    [InlineData("flowchart")]
    [InlineData("sequence")]
    [InlineData("state")]
    public async Task DesignsAndIndependentlyReviewsEveryRequirementBeforeCompiling(string type)
    {
        var transport = new Model();
        var client = Client(transport);
        var result = await client.GenerateDesignedNaturalAsync(Prompt, type, false,
            new DiagramPresetCatalog().Resolve(type, "balanced"), null, null, CancellationToken.None);
        Assert.Equal(new[] { "requirements", "requirements-review", "design", "review" }, transport.Purposes);
        Assert.Equal("Reviewed", result!.Quality!.Status);
        Assert.Equal(new[] { "r1" }, result.Quality.ReviewedRequirementIds);
        Assert.NotEmpty(result.Quality.ElementRequirements!);
        var dsl = new MermaidCompiler(new()).Compile(result.Diagram);
        if (type == "class") { Assert.Contains("Run", dsl); Assert.Contains("doorOpen", dsl); }
        if (type == "state") { Assert.Contains("[*] -->", dsl); Assert.Contains("--> [*]", dsl); }
        if (type == "sequence") { Assert.Contains("alt ", dsl); Assert.Contains("else ", dsl); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedDesignConsumesAtMostTwoRepairsAndNeverReturnsUnreviewedSuccess(bool alwaysReject)
    {
        var model = new Model { Reject = alwaysReject ? 3 : 1 };
        var task = Client(model).GenerateDesignedNaturalAsync(Prompt, "flowchart", false,
            new DiagramPresetCatalog().Resolve("flowchart", "balanced"), null, null, CancellationToken.None);
        if (alwaysReject) Assert.Equal("NATURAL_DESIGN_REJECTED", (await Assert.ThrowsAsync<LlmClientException>(() => task)).Code);
        else Assert.True((await task)!.Quality!.RepairUsed);
        Assert.Equal(alwaysReject ? new[] { "requirements", "requirements-review", "design", "review", "repair", "review", "repair", "review" } :
            new[] { "requirements", "requirements-review", "design", "review", "repair", "review" }, model.Purposes);
    }

    [Fact]
    public void CoverageAndInterlockMustBeStructureAndAssumptionsMustBeExplicit()
    {
        var requirements = Requirements();
        Assert.Equal("NaturalRequirementEvidenceInvalid", NaturalDesignValidation.Requirements(requirements, "unrelated request"));
        var design = Design("flowchart");
        Assert.Equal("NaturalInterlockMissing", NaturalDesignValidation.Design(design with { Edges = [] }, "flowchart", requirements));
        Assert.Equal("NaturalReviewInvalid", NaturalDesignValidation.Review(new(true, [], []), requirements));
        var assumption = new NaturalRequirement("r2", "제어기를 제안합니다", "entity", "assumption", "");
        requirements = requirements with { Requirements = [.. requirements.Requirements, assumption] };
        var extra = new NaturalDesignNode("controller", "제어기", "component", "", [], [], ["r2"], false);
        Assert.Equal("NaturalNodeInvalid", NaturalDesignValidation.Design(design with { Nodes = [.. design.Nodes, extra] }, "flowchart", requirements));
        var valid = design with { Nodes = [.. design.Nodes, extra with { Assumption = true }] };
        Assert.Null(NaturalDesignValidation.Design(valid, "flowchart", requirements));
        Assert.Contains("설계 가정", NaturalDesignValidation.Normalize(valid, "flowchart").Nodes.Last().Label);
    }

    [Fact]
    public async Task TargetedRepairPreservesReviewedNodesOutsideTheFailedRequirement()
    {
        var model = new Model { Reject = 1, TargetedRepair = true };
        var result = await Client(model).GenerateDesignedNaturalAsync(Prompt, "flowchart", false,
            new DiagramPresetCatalog().Resolve("flowchart", "balanced"), null, Requirements(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains(result!.Diagram.Nodes, node => node.Label == "문이 닫혔는가");
        Assert.DoesNotContain(result.Diagram.Nodes, node => node.Label == "임의 변경");
    }

    [Theory]
    [InlineData("List<T> 값과 x < 0 && y > 0 비교", false)]
    [InlineData("<script>alert(1)</script>", true)]
    [InlineData("<svg onload='x'>", true)]
    [InlineData("[이동](javascript:alert(1))", true)]
    [InlineData("%%{init: {}}%%", true)]
    public void AcceptsCodeExpressionsAndRejectsExecutablePresentation(string text, bool unsafeText) =>
        Assert.Equal(unsafeText, SharedSemanticValidation.UnsafeText(text));

    [Fact]
    public void RemovesHarmlessInlineFormattingWithoutRemovingCodeOrConditions() =>
        Assert.Equal("List<T> 값을 검사하고 x < 0 조건을 유지합니다", SharedSemanticValidation.PlainText("`List<T>` 값을 **검사**하고 `x < 0` 조건을 유지합니다"));

    [Fact]
    public void RequirementEvidenceUsesExactUtf16RangesAndAssignsEveryRequirementOnce()
    {
        const string prompt = "사용자가 🙂 요청을 시작한다.\r\n\r\n관리자는 요청을 승인한다.";
        var source = new NaturalRequirements("승인", ["사용자", "관리자"],
        [
            new("r1", "요청 시작", "action", "explicit", "사용자가 🙂 요청을 시작한다."),
            new("r2", "요청 승인", "action", "explicit", "관리자는 요청을 승인한다."),
            new("r3", "감사 로그", "entity", "assumption", "")
        ]);

        var result = NaturalRequirementEvidence.Attach(prompt, source);

        Assert.Equal(2, result.SourceRanges!.Count);
        Assert.Equal(2, result.Scenarios!.Count);
        Assert.All(result.SourceRanges, range => Assert.Equal(range.Text, prompt[range.StartOffset..range.EndOffset]));
        Assert.Equal("사용자가 🙂 요청을 시작한다.", result.SourceRanges[0].Text);
        Assert.Equal("관리자는 요청을 승인한다.", result.SourceRanges[1].Text);
        Assert.Equal(result.Requirements.Count,
            result.Scenarios.SelectMany(scenario => scenario.RequirementIds).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Requirements, requirement => Assert.False(string.IsNullOrWhiteSpace(requirement.ScenarioId)));
        Assert.Null(result.Requirements.Single(requirement => requirement.Id == "r3").SourceRangeId);
    }

    private static InternalLlmClient Client(Model model) => new(Options.Create(new LlmOptions { Enabled = true }), new(), new(), model, new(model));
    private sealed class Model : ILlmCompletionTransport
    {
        public bool IsEnabled => true;
        public int Reject { get; set; }
        public bool TargetedRepair { get; set; }
        public List<string> Purposes { get; } = [];
        public Task<VllmCompletionResult> CompleteAsync(VllmCompletionRequest request, CancellationToken ct)
        {
            Purposes.Add(request.Purpose!);
            using var json = JsonDocument.Parse(request.UserPrompt);
            object result = request.Purpose switch
            {
                "requirements" => NaturalExtraction.FromRequirements(Requirements()),
                "requirements-review" => new NaturalSourceReview(
                    json.RootElement.GetProperty("sourceRanges").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray(), []),
                "review" => Reject-- > 0 ? new NaturalDesignReview(false, ["r1"], ["문 열림 차단을 확인하세요"],
                    TargetedRepair ? [new("e2", "label", "BranchLabelInvalid", "차단 분기를 수정하세요")] : []) : new NaturalDesignReview(true, ["r1"], [], []),
                _ => Design(json.RootElement.GetProperty("type").GetString()!)
            };
            if (TargetedRepair && request.Purpose == "repair" && result is NaturalDesign design)
                result = design with { Nodes = design.Nodes.Select((node, index) => index == 0 ? node with { Label = "임의 변경" } : node).ToArray() };
            return Task.FromResult(new VllmCompletionResult(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "stop", 1, true, false, 0, request.MaxOutputTokens, 100, 100, 200));
        }
    }
}
