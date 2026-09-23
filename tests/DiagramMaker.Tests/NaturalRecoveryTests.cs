using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Configuration;
using DiagramMaker.Domain;
using DiagramMaker.Services;
using Microsoft.Extensions.Options;

namespace DiagramMaker.Tests;

public sealed class NaturalRecoveryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Prompt = "요청한다. 결과를 확인한다.";

    [Fact]
    public async Task AllAssumptionsCanBeCorrectedWithTheSameIds()
    {
        using var model = new Model((kind, attempt, context, value) => {
            if (kind == "requirements" && attempt == 1)
                foreach (var item in value["requirements"]!.AsArray()) {
                    item!["origin"] = "assumption";
                    item["sourceRangeIds"] = new JsonArray();
                }
            if (kind == "requirements" && attempt == 2)
                Assert.Contains("assumption", context["rejectedResponse"]!.GetValue<string>());
        });
        var result = await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct);
        Assert.All(result!.Requirements, item => Assert.Equal("explicit", item.Origin));
        Assert.Equal(new[] { "requirements", "requirements", "requirements-review" }, model.Kinds);
    }

    [Fact]
    public async Task ReviewContractRepairsDoNotConsumeNextCandidateReview()
    {
        using var model = new Model((kind, attempt, context, value) => {
            if (kind == "requirements") value["requirements"]![0]!["text"] = attempt == 1 ? "잘못된 처리" : "수정된 처리";
            if (kind == "requirements-review" && attempt == 1) value.AsObject().Remove("reviewedSourceRangeIds");
            if (kind == "requirements-review" && attempt == 2) value["reviewedSourceRangeIds"] = null;
            if (kind == "requirements-review" && attempt == 3) {

                value["issues"] = Issues(context["sourceRanges"]![0]!["id"]!.GetValue<string>());
            }
        });
        var result = await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct);
        Assert.Equal("수정된 처리", result!.Requirements.Single(item => item.Id == "r1").Text);
        Assert.Equal(2, model.Kinds.Count(kind => kind == "requirements"));
        Assert.Equal(4, model.Kinds.Count(kind => kind == "requirements-review"));
    }

    [Fact]
    public async Task RepeatedBadEvidenceStopsWithSpecificRootCauseAndNoProgressReason()
    {
        using var model = new Model((kind, _, _, value) => {
            if (kind == "requirements") value["requirements"]![0]!["sourceRangeIds"] = new JsonArray("unknown");
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
        Assert.Equal("NATURAL_REQUIREMENTS_INVALID", error.Code);
        Assert.Equal("NaturalRepairNoProgress", error.FailureKind);
        Assert.Equal(2, execution.Progress.TransportRequests);
        Assert.Equal(2, execution.Progress.CompletedUnits);
        Assert.Equal(2, execution.Diagnostics.Count(d => d.Kind == "Request" && d.HttpStatus == 200));
        Assert.Equal(2, execution.Diagnostics.Count(d => d.Kind == "Validation"));
        Assert.All(execution.Diagnostics.Where(d => d.ErrorCode is not null), d => Assert.Equal("Exhausted", d.RecoveryState));
        var terminal = Assert.Single(execution.Diagnostics, d => d.Kind == "Terminal");
        Assert.Equal("StartNewRun", terminal.NextAction);
        Assert.Equal("NaturalRepairNoProgress", terminal.ValidationCode);
        var report = LlmDiagnosticReport.Text("Failed", error.Code, execution.Progress, execution.Diagnostics);
        Assert.Contains("HTTP attempts: 2", report);
        Assert.Contains("NaturalEvidenceUnknown", report);
        Assert.Contains("completed checkpoints: 2", report);
        Assert.Contains("Final failure:", report);
        Assert.Contains("category: not-applicable", report);
        Assert.DoesNotContain("category: unknown", report);
        Assert.DoesNotContain(Prompt, report);
    }

    [Theory]
    [InlineData("missing-field", "NaturalFieldMissing")]
    [InlineData("null-field", "NaturalFieldTypeInvalid")]
    [InlineData("missing-id", "NaturalReviewMissingIds")]
    [InlineData("duplicate-id", "NaturalReviewDuplicateIds")]
    [InlineData("unknown-id", "NaturalReviewUnknownIds")]
    [InlineData("unknown-target", "NaturalReviewTargetUnknown")]
    [InlineData("unsupported-code", "NaturalFieldEnumInvalid")]
    public async Task BadReviewRepairsOnlyReviewAndClosesRecovery(string mode, string expected)
    {
        using var model = new Model((kind, attempt, context, value) => {
            if (kind != "requirements-review" || attempt != 1) return;
            switch (mode)
            {
                case "missing-field": value.AsObject().Remove("reviewedSourceRangeIds"); break;
                case "null-field": value["issues"] = null; break;
                case "missing-id": value["reviewedSourceRangeIds"]!.AsArray().RemoveAt(0); break;
                case "duplicate-id": value["reviewedSourceRangeIds"]![1] = value["reviewedSourceRangeIds"]![0]!.DeepClone(); break;
                case "unknown-id": value["reviewedSourceRangeIds"]![0] = "unknown"; break;
                case "unknown-target":  value["issues"] = Issues("unknown"); break;
                case "unsupported-code": value["issues"] = Issues("r1"); value["issues"]![0]!["code"] = "private-review-code"; break;
            }
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        Assert.NotNull(await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
        Assert.Equal(new[] { "requirements", "requirements-review", "requirements-review" }, model.Kinds);
        Assert.Contains(execution.Diagnostics, d => d.ValidationCode == expected && d.RecoveryState == "Recovered");
        Assert.DoesNotContain(execution.Diagnostics, d => d.RecoveryState == "Retrying");
    }

    [Fact]
    public async Task ExhaustedReviewAndResumeNeverReextractOrResetRepairBudget()
    {
        using var model = new Model((kind, _, _, value) => { if (kind == "requirements-review") value["reviewedSourceRangeIds"] = new JsonArray(); });
        IReadOnlyList<SemanticCheckpoint> saved;
        IReadOnlyList<LlmDiagnostic> diagnostics;
        SemanticProgress progress;
        using (var execution = new SemanticExecution(model.Options, null, Ct))
        {
            var error = await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
            Assert.Equal("NATURAL_REQUIREMENTS_REVIEW_INVALID", error.Code);
            saved = execution.Checkpoints; diagnostics = execution.Diagnostics; progress = execution.Progress;
        }
        Assert.Equal(3, model.Kinds.Count);
        using (var resumed = new SemanticExecution(model.Options, saved, Ct, savedDiagnostics: diagnostics, savedProgress: progress))
        {
            await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
            Assert.Equal(0, resumed.Progress.AttemptTransportRequests);
            Assert.DoesNotContain(resumed.Diagnostics, d => d.RecoveryState == "Retrying");
        }
        Assert.Equal(3, model.Kinds.Count);
    }

    [Fact]
    public async Task SourceRangeIssueUnlocksAffectedRequirementAndProtectsOtherAcceptedItems()
    {
        using var model = new Model((kind, attempt, context, value) => {
            if (kind == "requirements") {
                value["requirements"]![0]!["text"] = attempt == 1 ? "잘못된 처리" : "수정된 처리";
                value["requirements"]![1]!["text"] = attempt == 1 ? "정상 항목" : "덮어쓰면 안 됨";
            }
            if (kind == "requirements-review" && attempt == 1) {

                value["issues"] = Issues(context["sourceRanges"]![0]!["id"]!.GetValue<string>());
            }
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var result = await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct);
        Assert.Equal("수정된 처리", result!.Requirements.Single(r => r.Id == "r1").Text);
        Assert.Equal("정상 항목", result.Requirements.Single(r => r.Id == "r2").Text);
        Assert.Equal(4, model.Kinds.Count);
        Assert.DoesNotContain(JsonSerializer.Serialize(execution.Diagnostics), "private-review-code");
        Assert.Contains(execution.Diagnostics, d => d.ValidationCode == "NaturalRequirementOmitted" && d.RecoveryState == "Recovered");
    }

    [Fact]
    public async Task ScenarioFailureIsRecordedAndDoesNotMisreportSemanticReview()
    {
        using var model = new Model((kind, _, _, value) => {
            if (kind == "requirements") value["scenarios"]![0]!["requirementIds"] = new JsonArray("outside");
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var result = await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct);
        Assert.Null(NaturalDesignValidation.Plan(result!));
        Assert.Contains(execution.Diagnostics, d => d.Purpose == "scenario-validation" && d.ValidationCode == "NaturalScenarioUnknownRequirement");
        Assert.Equal(new[] { "requirements", "scenario-mapping", "requirements-review" }, model.Kinds);
    }

    [Fact]
    public async Task RenamedAcceptedItemIsRejectedAndOriginalIdsSurviveRepair()
    {
        using var model = new Model((kind, attempt, _, value) => {
            if (kind != "requirements") return;
            if (attempt == 1) value["requirements"]![1]!["sourceRangeIds"] = new JsonArray("unknown");
            if (attempt == 2) {
                value["requirements"]![0]!["id"] = "renamed";
                value["scenarios"]![0]!["requirementIds"]![0] = "renamed";
            }
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var result = await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct);
        Assert.Equal(new[] { "r1", "r2" }, result!.Requirements.Select(r => r.Id));
        Assert.Contains(execution.Diagnostics, d => d.ValidationCode == "NaturalAcceptedIdsChanged" && d.RecoveryState == "Recovered");
    }

    [Fact]
    public async Task ServingSchemaCompatibilityStillEnforcesOriginalLengthLocally()
    {
        using var model = new Model((kind, _, _, value) => { if (kind == "requirements") value["requirements"]![0]!["text"] = new string('가', 501); }, rejectGrammar: true);
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
        Assert.Equal("NaturalRepairNoProgress", error.FailureKind);
        Assert.Contains(execution.Diagnostics, d => d.SchemaRelaxed && d.ValidationDetails?.ActualLength == 501 && d.ValidationDetails.AllowedLength == 500);
        Assert.Equal(3, execution.Progress.TransportRequests);
    }

    [Fact]
    public void LegacyDiagnosticsAreNotPretendedToHaveHttpStatus()
    {
        var report = LlmDiagnosticReport.Text("Failed", "OLD", null,
            [new("old", "stage", "unit", "Failed", DateTimeOffset.UtcNow, ErrorCode: "OLD")]);
        Assert.Contains("HTTP: not-recorded", report);
        Assert.DoesNotContain("HTTP: 200", report);
    }

    [Fact]
    public async Task UnchangedRepairReusesRejectionAndResumeCannotRestartBudget()
    {
        using var model = new Model((kind, _, context, value) => {
            if (kind == "requirements-review") value["issues"] = Issues(context["sourceRanges"]![0]!["id"]!.GetValue<string>());
        });
        IReadOnlyList<SemanticCheckpoint> saved;
        using (var execution = new SemanticExecution(model.Options, null, Ct)) {
            var error = await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
            Assert.Equal("NaturalRepairNoProgress", error.FailureKind);
            Assert.Equal(2, model.Kinds.Count(k => k == "requirements"));
            Assert.Equal(1, model.Kinds.Count(k => k == "requirements-review"));
            Assert.Equal(1, execution.Diagnostics.Last(d => d.Extraction is not null).Extraction!.ReviewAttempts);
            saved = execution.Checkpoints;
        }
        using (var resumed = new SemanticExecution(model.Options, saved, Ct))
            await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
        Assert.Equal(3, model.Kinds.Count);
    }

    [Fact]
    public async Task DistinctCandidatesShareTenReviewRepairs()
    {
        using var model = new Model((kind, attempt, _, value) => {
            if (kind == "requirements") value["requirements"]![0]!["text"] = "조건 후보 " + attempt;
            else if (attempt % 3 != 0) value["unexpected"] = attempt;
            else { value["issues"] = Issues("r1"); value["issues"]![0]!["code"] = attempt % 2 == 0 ? "NaturalConditionChanged" : "NaturalUnsupportedClaim"; }
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct));
        Assert.Equal("FormatRepairBudgetExhausted", error.FailureKind);
        Assert.Equal(22, model.Kinds.Count);
        Assert.Equal(6, model.Kinds.Count(k => k == "requirements"));
        Assert.Equal(16, model.Kinds.Count(k => k == "requirements-review"));
    }

    [Theory]
    [InlineData("NaturalRequirementOmitted")]
    [InlineData("NaturalConditionChanged")]
    [InlineData("NaturalUnsupportedClaim")]
    [InlineData("NaturalEvidenceMismatch")]
    [InlineData("NaturalEntityMismatch")]
    [InlineData("NaturalScenarioMismatch")]
    public async Task SourceGroundedReviewReasonsRemainSpecificAndRepairable(string code)
    {
        using var model = new Model((kind, attempt, _, value) => {
            if (kind == "requirements") value["requirements"]![0]!["text"] = attempt == 1 ? "오류 조건" : "수정 조건";
            if (kind == "requirements-review" && attempt == 1) {
                value["issues"] = Issues("r1"); value["issues"]![0]!["code"] = code;
            }
        });
        using var execution = new SemanticExecution(model.Options, null, Ct);
        var result = await model.Client.ExtractNaturalRequirementsAsync(Prompt, false, Ct);
        Assert.Equal(code == "NaturalScenarioMismatch" ? "오류 조건" : "수정 조건", result!.Requirements.Single(r => r.Id == "r1").Text);
        Assert.Contains(execution.Diagnostics, d => d.ValidationCode == code && d.RecoveryState == "Recovered");
        var report = LlmDiagnosticReport.Text("Completed", null, execution.Progress, execution.Diagnostics);
        Assert.Contains(code, report);
        Assert.Contains(code == "NaturalScenarioMismatch" ? "replaced=0" : "replaced=1", report);
        Assert.DoesNotContain("오류 조건", report);
        Assert.DoesNotContain("수정 조건", report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedInternalInputsUseSourceOnlyContractAndReviewFidelity(bool table)
    {
        var prompt = table ? NaturalDiagramSelfTest.TablePrompt : NaturalDiagramSelfTest.ShortPrompt;
        using var model = new Model((kind, _, context, value) => {
            Assert.NotEmpty(context["sourceRanges"]!.AsArray());
            if (kind == "requirements") {
                Assert.All(value["requirements"]!.AsArray(), item => {
                    Assert.Null(item!["origin"]); Assert.Null(item["sourceQuote"]);
                });
            }
        });
        var result = await model.Client.ExtractNaturalRequirementsAsync(prompt, false, Ct);
        Assert.All(result!.Requirements, r => {
            Assert.Equal("explicit", r.Origin); Assert.NotEmpty(r.SourceRangeIds!);
            Assert.Contains(r.SourceQuote, prompt);
        });
    }

    [Fact]
    public void ReviewCannotDemandUnstatedDesignWithUngroundedIssue()
    {
        var source = NaturalRequirementEvidence.Prepare(Prompt);
        var requirements = new NaturalRequirements("흐름", [], [new("r1", "요청", "behavior", "explicit", "", SourceRangeIds: [source[0].Id])]);
        var review = new NaturalSourceReview(source.Select(r => r.Id).ToArray(),
            [new("NaturalUnsupportedClaim", "text", ["r1"], [], "원문에 없는 메서드를 추가하라")]);
        Assert.Equal("NaturalReviewIssuesInvalid", NaturalDesignValidation.RequirementsReview(review, source, requirements)!.Code);
    }

    [Fact]
    public void SelfTestSummaryKeepsSpecificReasonAndBoundedMetrics()
    {
        var metrics = new NaturalExtractionMetrics(2, 2, 2, 1, 1, 0, 2, 4);
        var result = NaturalDiagramSelfTest.DescribeCase(new("short-approval", "Failed", 0, 0, "NATURAL_REQUIREMENTS_REJECTED"), [
            new("v", "natural-validation", "unit", "Failed", DateTimeOffset.UtcNow, ErrorCode: "NATURAL_ITEM_INVALID",
                ValidationCode: "NaturalConditionChanged", Kind: "Validation"),
            new("t", "natural-requirements", "unit", "Failed", DateTimeOffset.UtcNow, ErrorCode: "NATURAL_REQUIREMENTS_REJECTED",
                ValidationCode: "NaturalConditionChanged", Purpose: "requirements-result", Kind: "Terminal", Extraction: metrics)]);
        Assert.Equal("NaturalConditionChanged", result.ValidationCode);
        Assert.Equal("natural-validation", result.FailureStage);
        Assert.Equal(1, result.IssueCounts!["NaturalConditionChanged"]);
        Assert.Equal(1, result.Extraction!.Replaced);
    }

    private static JsonArray Issues(string target) => new(JsonSerializer.SerializeToNode(new {
        field = "text", code = target.StartsWith("source-", StringComparison.Ordinal) ? "NaturalRequirementOmitted" : "NaturalConditionChanged",
        requirementIds = target.StartsWith("source-", StringComparison.Ordinal) ? Array.Empty<string>() : new[] { target },
        sourceRangeIds = new[] { target.StartsWith("source-", StringComparison.Ordinal) ? target : NaturalRequirementEvidence.Prepare(Prompt)[0].Id }, instruction = "수정하세요" }));

    private sealed class Model : HttpMessageHandler
    {
        public LlmOptions Options { get; } = new() { Enabled = true, Endpoint = "http://127.0.0.1:19099/v1/chat/completions",
            AllowedOrigin = "http://127.0.0.1:19099", UseServerTokenization = false, MaxTransientRetries = 0 };
        public InternalLlmClient Client { get; }
        public List<string> Kinds { get; } = [];
        private readonly Action<string, int, JsonNode, JsonNode> mutate;
        private readonly bool rejectGrammar;
        private readonly VllmClient transport;
        private bool grammarRejected;
        public Model(Action<string, int, JsonNode, JsonNode> mutate, bool rejectGrammar = false)
        {
            this.mutate = mutate; this.rejectGrammar = rejectGrammar;
            transport = new(Options, handler: this);
            Client = new(Microsoft.Extensions.Options.Options.Create(Options), new(), new(), transport, new(transport));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            if (rejectGrammar && !grammarRejected) {
                grammarRejected = true;
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("The provided JSON schema contains features not supported by xgrammar.") };
            }
            var context = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
            if (context["originalRequest"] is { } original)
            {
                var restored = JsonNode.Parse(original.GetValue<string>())!;
                restored["rejectedResponse"] = context["rejectedResponse"]!.DeepClone(); context = restored;
            }
            var properties = body["structured_outputs"]!["json"]!["properties"]!;
            var review = properties["reviewedSourceRangeIds"] is not null;
            var mapping = !review && properties["requirements"] is null;
            var kind = review ? "requirements-review" : mapping ? "scenario-mapping" : "requirements";
            Kinds.Add(kind);
            var source = (context["sourceRanges"] ?? context["requirements"]!["sourceRanges"])!.AsArray();
            var ids = source.Select(r => r!["id"]!.GetValue<string>()).ToArray();
            object result = review ? new { reviewedSourceRangeIds = ids, issues = Array.Empty<NaturalSourceIssue>() } :
                new { title = "요청 흐름", entities = Array.Empty<string>(), requirements = ids.Select((id, i) => new {
                    id = "r" + (i + 1), text = "처리 " + (i + 1), kind = "behavior", sourceRangeIds = new[] { id } }),
                    scenarios = new[] { new { id = "s1", title = "흐름", requirementIds = ids.Select((_, i) => "r" + (i + 1)) } }, questions = Array.Empty<NaturalQuestion>() };
            var value = JsonSerializer.SerializeToNode(result)!;
            if (mapping) { value.AsObject().Remove("title"); value.AsObject().Remove("entities"); value.AsObject().Remove("requirements"); }
            mutate(kind, Kinds.Count(k => k == kind), context, value);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                choices = new[] { new { message = new { content = value.ToJsonString() }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 100, completion_tokens = 100, total_tokens = 200 } })) };
        }
        // VllmClient owns this handler; tests dispose it once through that client.
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }
}
