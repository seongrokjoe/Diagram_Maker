using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;

namespace DiagramMaker.Tests;

public sealed class NaturalDiagnosticTests
{
    [Fact]
    public void ScenarioFindingsIdentifyUnknownAndUnassignedRequirementsWithoutRejectingSharedIds()
    {
        const string prompt = "장비가 요청을 받는다. 문이 열리면 시작을 차단한다.";
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        var value = new NaturalRequirements("장비", ["장비"],
            [new("r1", "요청을 받는다", "behavior", "explicit", "", SourceRangeIds: [ranges[0].Id]),
             new("r2", "문이 열리면 차단", "interlock", "explicit", "", SourceRangeIds: [ranges[1].Id])],
            ranges, [new("s1", "첫 흐름", ["r1", "unknown"], [ranges[0].Id])], []);
        var findings = NaturalDesignValidation.PlanFindings(value);
        Assert.Contains(findings, issue => issue.Code == "NaturalScenarioUnknownRequirement" &&
            issue.TargetKind == "scenario" && issue.ItemId == "s1" && issue.Field == "requirementIds");
        Assert.Contains(findings, issue => issue.Code == "NaturalScenarioRequirementMissing" && issue.ItemId == "r2");
        var shared = value with { Scenarios = [new("s1", "첫 흐름", ["r1", "r2"], []),
            new("s2", "둘째 흐름", ["r2"], [])] };
        Assert.Empty(NaturalDesignValidation.PlanFindings(NaturalRequirementEvidence.DeriveScenarioRanges(shared)));
    }

    [Fact]
    public void NodeFindingNamesTheActualBrokenFieldAndIndex()
    {
        var design = NaturalDesignTests.Design("sequence");
        design = design with { Nodes = [design.Nodes[0] with { RequirementIds = [] }, .. design.Nodes.Skip(1)] };
        var issue = Assert.Single(NaturalDesignValidation.DesignIssues(design, "sequence", NaturalDesignTests.Requirements()));
        Assert.Equal("NaturalNodeRequirementIdsMissing", issue.Code);
        Assert.Equal("node", issue.TargetKind);
        Assert.Equal(0, issue.ItemIndex);
        Assert.Equal("requirementIds", issue.Field);
        Assert.Equal(design.Nodes[0].Id, issue.ItemId);
    }

    [Fact]
    public void FoupFailureBranchesRepairOnlyMissingReferencesAndPreserveTheirContent()
    {
        var requirements = new NaturalRequirements("FOUP 이동", ["FOUP", "BPort", "STR", "Shelf"],
            [new("r1", "BPort가 감지되지 않으면 이동을 중단한다", "error", "explicit", ""),
             new("r2", "STR Pick이 실패하면 이동을 중단한다", "error", "explicit", ""),
             new("r3", "Shelf가 감지되지 않으면 이동을 중단한다", "error", "explicit", "")]);
        NaturalDesignNode Participant(string id) => new(id, id, "participant", "", [], [], [], false);
        NaturalDesignEdge Failure(string id, string source, string target, string label) =>
            new(id, source, target, "message", label, "", "", "", [], [], false);
        var design = new NaturalDesign("FOUP 실패", [Participant("FOUP"), Participant("BPort"), Participant("STR"), Participant("Shelf")],
            [Failure("bport", "FOUP", "BPort", "BPort 미검출"),
             Failure("pick", "BPort", "STR", "STR Pick 실패"),
             Failure("shelf", "STR", "Shelf", "Shelf 미검출")], []);
        var issues = NaturalDesignValidation.DesignIssues(design, "sequence", requirements);
        Assert.Contains(issues, issue => issue.Code == "NaturalEdgeRequirementIdsMissing" && issue.ItemId == "pick");
        var bindings = new NaturalReferenceRepair([
            new("node", "FOUP", ["r1"]), new("node", "BPort", ["r1", "r2"]),
            new("node", "STR", ["r2", "r3"]), new("node", "Shelf", ["r3"]),
            new("edge", "bport", ["r1"]), new("edge", "pick", ["r2"]), new("edge", "shelf", ["r3"])
        ]);
        Assert.Null(NaturalReferenceRecovery.Check(bindings, issues, requirements, design));
        var repaired = NaturalReferenceRecovery.Apply(design, bindings);
        Assert.Empty(NaturalDesignValidation.DesignIssues(repaired, "sequence", requirements));
        Assert.Equal(design.Edges.Select(edge => edge.Label), repaired.Edges.Select(edge => edge.Label));
        Assert.Equal(design.Edges.Select(edge => edge.Id), repaired.Edges.Select(edge => edge.Id));
        Assert.Equal("NaturalReferenceRepairInvalid", NaturalReferenceRecovery.Check(bindings with {
            Bindings = [.. bindings.Bindings.Take(6), new("edge", "shelf", ["outside"])]
        }, issues, requirements, design));
    }

    [Fact]
    public void MissingReferenceComparisonShowsAnExistingElementWithAnEmptyList()
    {
        var requirements = NaturalDesignTests.Requirements() with {
            SourceRanges = NaturalRequirementEvidence.Prepare(NaturalDesignTests.Prompt)
        };
        var design = NaturalDesignTests.Design("sequence") with {
            Edges = [NaturalDesignTests.Design("sequence").Edges[0] with { RequirementIds = [] },
                .. NaturalDesignTests.Design("sequence").Edges.Skip(1)]
        };
        var issue = Assert.Single(NaturalDesignValidation.DesignIssues(design, "sequence", requirements));
        var comparison = NaturalIssueComparisonBuilder.Build("diagnostic", issue, requirements, design);
        Assert.True(comparison.TargetExists);
        Assert.Equal("empty-reference-list", comparison.ObservedState);
        Assert.Equal("", comparison.Observed);
        Assert.Empty(comparison.SourceExcerpts);
    }

    [Fact]
    public void ParticipantReferencesComeOnlyFromExplicitIncidentConnections()
    {
        var requirements = new NaturalRequirements("FOUP 이동", ["FOUP", "STR", "Shelf"],
            [new("r1", "FOUP에서 STR로 이동한다", "behavior", "explicit", ""),
             new("r2", "선반 사용을 제안한다", "entity", "assumption", "")]);
        NaturalDesignNode Participant(string id) => new(id, id, "participant", "", [], [], [], false);
        var design = new NaturalDesign("FOUP 호출", [Participant("FOUP"), Participant("STR"), Participant("Shelf")],
            [new("move", "FOUP", "STR", "message", "이동", "", "", "", [], ["r1"], false),
             new("proposal", "STR", "Shelf", "message", "선반 제안", "", "", "", [], ["r2"], true)], []);

        var completed = NaturalReferenceRecovery.CompleteParticipants(design, "sequence", requirements);

        Assert.Equal(new[] { "r1" }, completed.Nodes[0].RequirementIds);
        Assert.Equal(new[] { "r1" }, completed.Nodes[1].RequirementIds);
        Assert.Empty(completed.Nodes[2].RequirementIds);
        Assert.Equal(design.Edges, completed.Edges);
        Assert.Contains(NaturalDesignValidation.DesignIssues(completed, "sequence", requirements),
            issue => issue.ItemId == "Shelf" && issue.Code == "NaturalNodeRequirementIdsMissing");
    }

    [Fact]
    public void ChangedConditionRequiresAnExactSourceQuoteAndExistingTargetField()
    {
        var requirements = NaturalDesignTests.Requirements();
        var design = NaturalDesignTests.Design("sequence");
        var issue = new NaturalIssue("e1", "controlPath", "NaturalConditionChanged", "원문의 조건을 보존하세요.",
            ["r1"], SourceQuote: NaturalDesignTests.Prompt, RelatedElementIds: [design.Nodes[0].Id]);
        var review = new NaturalScenarioReview(["r1"], [issue]);
        var targets = design.Nodes.Select(node => node.Id).Concat(design.Edges.Select(edge => edge.Id)).ToHashSet();
        Assert.Null(NaturalScenarioReviewValidation.Check(review, requirements, targets, design));
        Assert.Equal("NaturalReviewEvidenceInvalid", NaturalScenarioReviewValidation.Check(review with {
            Issues = [issue with { SourceQuote = "문이 닫혀 있으면" }] }, requirements, targets, design)!.Code);
        Assert.Equal("NaturalReviewFieldInvalid", NaturalScenarioReviewValidation.Check(review with {
            Issues = [issue with { Field = "members" }] }, requirements, targets, design)!.Code);
    }

    [Fact]
    public void ChangedConditionAcceptsTheExactQuoteSeenByTheMaskedModel()
    {
        const string prompt = "token=private_value 문이 열려 있으면 장비를 차단한다.";
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        var requirements = new NaturalRequirements("장비", ["장비"],
            [new("r1", prompt, "interlock", "explicit", "", SourceRangeIds: [ranges[0].Id])], ranges);
        var design = NaturalDesignTests.Design("sequence");
        var visibleQuote = new SecretMasker().Mask(ranges[0].Text);
        Assert.NotEqual(ranges[0].Text, visibleQuote);
        var issue = new NaturalIssue("e1", "controlPath", "NaturalConditionChanged", "조건을 보존하세요.",
            ["r1"], SourceQuote: visibleQuote, RelatedElementIds: []);
        var targets = design.Nodes.Select(node => node.Id).Concat(design.Edges.Select(edge => edge.Id)).ToHashSet();
        Assert.Null(NaturalScenarioReviewValidation.Check(new(["r1"], [issue]), requirements, targets, design));
    }
    [Fact]
    public async Task PrivateComparisonMasksSecretsAndDoesNotCountAsCompletedWork()
    {
        const string prompt = "token=private_value 문이 열려 있으면 장비를 차단한다.";
        var ranges = NaturalRequirementEvidence.Prepare(prompt);
        var requirements = new NaturalRequirements("장비", ["장비"],
            [new("r1", prompt, "interlock", "explicit", "", SourceRangeIds: [ranges[0].Id])], ranges);
        var design = NaturalDesignTests.Design("sequence");
        var issue = new NaturalIssue("e1", "controlPath", "NaturalConditionChanged", "token=private_value 수정하세요.",
            ["r1"], SourceQuote: prompt, RelatedElementIds: []);
        var comparison = NaturalIssueComparisonBuilder.Build("diagnostic", issue, requirements, design);
        Assert.DoesNotContain("private_value", System.Text.Json.JsonSerializer.Serialize(comparison));
        Assert.Contains("[REDACTED]", comparison.SourceExcerpts[0].Text);
        using var execution = new SemanticExecution(new LlmOptions(), null, CancellationToken.None);
        await execution.SaveNaturalComparisonAsync(comparison);
        Assert.Contains(execution.Checkpoints, item => item.Stage == "natural-comparison");
        Assert.Equal(0, execution.Progress.CompletedUnits);
        Assert.Empty(execution.Diagnostics);
    }
}
