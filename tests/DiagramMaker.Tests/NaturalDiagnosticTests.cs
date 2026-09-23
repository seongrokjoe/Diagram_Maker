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
        Assert.Equal("NaturalNodeInvalid", issue.Code);
        Assert.Equal("node", issue.TargetKind);
        Assert.Equal(0, issue.ItemIndex);
        Assert.Equal("requirementIds", issue.Field);
        Assert.Equal(design.Nodes[0].Id, issue.ItemId);
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