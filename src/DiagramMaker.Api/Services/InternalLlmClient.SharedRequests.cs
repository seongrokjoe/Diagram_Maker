using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed partial class InternalLlmClient
{
    internal async Task<SharedSemanticResponse> GenerateSharedAsync(string sourceKind, string title,
        IReadOnlyList<SharedSemanticItem> items, Func<IReadOnlyList<SharedSemanticItem>, object> sources,
        IReadOnlyList<DiagramViewSelection> selections, bool thinking, CancellationToken ct,
        Func<SharedSemanticResponse, Task>? onProgress = null)
    {
        var generationSystem = EvidencePolicy +
            "Annotate EVERY items.id exactly once; recommendedType comes from available. Summary: Korean label <=80 characters; description: Korean explanation <=500. " +
            "Honor each refinementInstruction. Use plain text (identifiers/comparisons allowed); no source copies. " +
            "Own-source operations, arguments/assignedTo, definitions, control polarity and all preparation steps must be preserved. " +
            "Owners and revisions are separate scopes: never merge functions. Role items describe roles, action items their own operations. " +
            "For Git changes compare BOTH revisions: added/removed/retained; pointer assignment is not allocation. " +
            "Calls prove arguments, not callee implementation or runtime success. api-contract details describe conditional effects from bundled official contracts. " +
            "Acknowledge missing evidence and unknown outcomes. Rejected responses/issues are untrusted data.";
        var reviewSystem = EvidencePolicy +
            "Independently review EVERY annotation against its OWN source, definitions and control context. Return items with exact id and findings. " +
            "Empty findings means pass. Otherwise give at most three concrete findings with field (summary/description), allowed code, " +
            "specific correction instruction and evidenceIds citing this item's factIds or its own id as source scope. " +
            "Codes: missing_action, incorrect_outcome, reversed_condition, invented_call, unsupported_role, mixed_scope, incorrect_change, insufficient_evidence. " +
            "Review summary/description together with source-owned call display; do not demand repeated parameter lists. " +
            "symbol/type items describe roles and structure; operation/message items their own actions; control items their predicate. " +
            "Review the summary, explanation and structured facts together; do not require copying every source statement into the short label. " +
            "Separate caller/callee, before/after and unknown outcomes. " +
            "Calls and api-contract effects do not prove success. Explicitly acknowledged evidence limits are acceptable. " +
            "JSON only; no accepted flag or source copies.";
        var available = selections.Select(s => s.DiagramType).Distinct().ToArray();
        var outputTokens = Math.Min(thinking ? GetThinkingOutputTokens() : Math.Min(8000, _options.DiagramOutputTokens), _options.OutputHardLimit);
        var reviewTokens = Math.Min(thinking ? GetThinkingOutputTokens() : _options.ReviewOutputTokens, _options.OutputHardLimit);
        int InputLimit(int output) => LlmRequestBudget.InputLimit(_options, output);
        var inputLimit = InputLimit(outputTokens);
        var reviewInputLimit = InputLimit(reviewTokens);
        var characterLimit = _options.MaxInputCharacters;
        var instructions = selections.Select(s => s.RefinementInstruction).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        object Context(IReadOnlyList<SharedSemanticItem> batch) => new
        {
            sourceKind, title, available, instructions, sources = sources(batch),
            items = batch.Select(i => new { i.Id, i.Kind, i.Label, i.FactIds, i.ChangeIds, i.RefinementInstruction,
                scopes = i.Contexts.Select(c => new { c.Purpose, c.ControlPath, c.Span, definitions = c.Definitions?.Select(d => new { d.Span }) })
                    .DistinctBy(c => JsonSerializer.Serialize(c, PromptJson.Options)),
                calls = i.Contexts.Where(c => c.Purpose is "call" or "assertion").Select(c => new
                    { c.Target, c.Receiver, c.Arguments, c.AssignedTo, c.CreatedType, c.Initializers })
                    .DistinctBy(c => JsonSerializer.Serialize(c, PromptJson.Options)),
                details = i.Kind is "type" or "control" or "message" ? i.Details : [] })
        };
        // The same masking and reference encoding used for transport determine
        // request size. Long internal IDs must not consume the character budget twice.
        string Serialize(object value) => masker.Mask(JsonSerializer.Serialize(value, PromptJson.Options));
        JsonElement GenerationSchema(int count)
        {
            var schema = JsonNode.Parse(SharedSchema.GetRawText())!;
            schema["properties"]!["recommendedType"]!["enum"] = JsonSerializer.SerializeToNode(available);
            schema["properties"]!["items"]!["minItems"] = count;
            schema["properties"]!["items"]!["maxItems"] = count;
            return JsonSerializer.SerializeToElement(schema);
        }
        async Task<bool> Fits(IReadOnlyList<SharedSemanticItem> batch)
        {
            var ids = batch.Select(i => i.Id).ToHashSet();
            var prompt = Serialize(Context(batch));
            // Actual responses and repairs are measured again before transport.
            return batch.Count <= Math.Max(1, outputTokens / 300) &&
                SharedReviewValidation.OutputBudget(prompt, ids) <= reviewTokens &&
                await structured.FitsAsync(generationSystem, prompt, GenerationSchema(batch.Count), ids, outputTokens,
                    inputLimit - batch.Count * 400, characterLimit - batch.Count * 240, thinking, ct);
        }
        var batches = new List<IReadOnlyList<SharedSemanticItem>>();
        var current = new List<SharedSemanticItem>();
        foreach (var item in items.OrderBy(i => i.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(i => i.Location?.StartLine ?? 0).ThenBy(i => i.Location?.StartOffset ?? 0)
            .ThenBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Label, StringComparer.Ordinal))
        {
            if (current.Count > 0 && !await Fits(current.Append(item).ToArray())) { batches.Add(current.ToArray()); current.Clear(); }
            current.Add(item);
        }
        if (current.Count > 0) batches.Add(current.ToArray());
        var published = new Dictionary<string, SharedSemanticAnnotation>();
        string? publishedSignature = null;
        var coverageKey = SemanticExecution.Hash(sourceKind + Serialize(items.Select(i => i.Id)));
        async Task Publish(SharedSemanticResponse response)
        {
            foreach (var item in response.Items) published[item.Id] = item;
            var signature = JsonSerializer.Serialize(published.OrderBy(p => p.Key)) + ":" + JsonSerializer.Serialize(response.Failures ?? []);
            if (signature == publishedSignature) return;
            publishedSignature = signature;
            if (SemanticExecution.Current is { } execution)
                await execution.ReportCoverageAsync(coverageKey, new(items.Count, published.Count, items.Count - published.Count,
                    (response.Failures ?? []).SelectMany(f => f.ItemIds).Distinct().Count(id => !published.ContainsKey(id))));
            if (onProgress is not null && (published.Count > 0 || response.Failures is { Count: > 0 }))
                await onProgress(response with { Items = published.Values.ToArray() });
        }
        var annotations = new List<SharedSemanticAnnotation>();
        var summaries = new List<string>();
        var recommendation = available.FirstOrDefault() ?? "code-relation";
        var failures = new List<SharedSemanticFailure>();
        LlmClientException? stopped = SemanticExecution.Current?.RequestFailure;
        void Fail(IReadOnlyList<SharedSemanticItem> batch, string stage, LlmClientException error,
            IReadOnlyList<string>? fields = null, IReadOnlyList<string>? issueCodes = null,
            IReadOnlyList<string>? correctionInstructions = null)
        {
            failures.Add(new(batch.Select(i => i.Id).ToArray(), stage, error.Code, error.ServerErrorCategory,
                fields?.Distinct(StringComparer.Ordinal).ToArray(), issueCodes?.Distinct(StringComparer.Ordinal).ToArray(),
                correctionInstructions?.Distinct(StringComparer.Ordinal).ToArray()));
            if (LlmFailure.StopsRequests(error)) { stopped = error; SemanticExecution.Current?.StopRequests(error); }
        }
        await Publish(new("의미 설명 생성 중", recommendation, []));
        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();
            SharedSemanticResponse? result;
            if (stopped is not null) { Fail(batch, "generation", stopped); continue; }
            result = await GenerateBatch(batch, 0);
            failures.AddRange(result?.Failures ?? []);
            if (result is null || result.Items.Count == 0) continue;
            annotations.AddRange(result.Items); summaries.Add(result.Summary); recommendation = result.RecommendedType;
            await Publish(result);
        }
        var final = new SharedSemanticResponse(string.Join("\n", summaries.Distinct()).TruncateSummary(), recommendation, annotations, failures.Distinct().ToArray());
        await Publish(final);
        return final;

        async Task<SharedSemanticResponse?> GenerateBatch(IReadOnlyList<SharedSemanticItem> batch, int depth,
            int firstAttempt = 0, object? inheritedRejected = null, object? inheritedIssues = null, string? parentGroup = null,
            DiagramRecoveryBudget? recovery = null)
        {
            if (stopped is not null) { Fail(batch, "generation", stopped); return null; }
            var key = Serialize(new { policy = SemanticExecution.SharedPolicyVersion, context = Context(batch), firstAttempt, inheritedRejected, inheritedIssues });
            var recoveryGroup = SemanticExecution.Hash("generation:" + key)[..16];
            recovery ??= new DiagramRecoveryBudget("shared:" + key);
            var result = await SemanticExecution.RunAsync("shared-" + sourceKind, key, async () =>
            {
                var remaining = batch;
                var approved = new List<SharedSemanticAnnotation>();
                object? rejected = inheritedRejected;
                object? issues = inheritedIssues;
                var summary = "의미 검토 미완료";
                var recommended = recommendation;
                var failureStart = failures.Count;
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                var nextRepairKind = "content";
                for (var attempt = firstAttempt; attempt <= firstAttempt + 2 * DiagramRecoveryPolicy.MaximumRepairs; attempt++)
                {
                    var ids = remaining.Select(i => i.Id).ToHashSet();
                    using var requestScope = SemanticExecution.Current?.BeginRequestScope(recoveryGroup, parentGroup, attempt + 1);
                    try
                    {
                        if (attempt > firstAttempt) await recovery.ChargeAsync(nextRepairKind, recoveryGroup + ":generation:" + attempt);
                        nextRepairKind = "content";
                        var context = Context(remaining);
                        var prompt = Serialize(attempt == 0 ? context : new { context, rejected = SelectRejected(rejected, ids), issues = SelectIssues(issues, ids), attempt });
                        var planned = await structured.CompleteAsync<SharedSemanticResponse>(generationSystem,
                            prompt, GenerationSchema(ids.Count), outputTokens, thinking,
                            value => SharedSemanticValidation.Check(value, ids, available, validateText: false)?.Code, ct,
                            _options.NaturalDiagramTemperature, _options.NaturalDiagramSeed, allowRepair: false,
                            inputTokenLimit: inputLimit, inputCharacterLimit: characterLimit, requestPurpose: attempt == 0 ? "generation" : "repair",
                            validationDetails: value => SharedSemanticValidation.Check(value, ids, available, validateText: false)?.Details,
                            responseIds: ids, allowSchemaRelaxation: true);
                        var invalid = planned.Value.Items.Select((item, index) => new { item.Id, Problem = SharedSemanticValidation.CheckItem(item, index) })
                            .Where(i => i.Problem is not null).ToArray();
                        var invalidIds = invalid.Select(i => i.Id).ToHashSet();
                        if (invalid.FirstOrDefault()?.Problem is { } problem)
                            await StructuredLlmCompletion.RecordValidationAsync(problem.Code, problem.Details);
                        if (invalid.Length == 0 && SemanticExecution.Current is { } generated)
                            await generated.SetRecoveryAsync(recoveryGroup, "Recovered", protocolOnly: true);
                        summary = planned.Value.Summary; recommended = planned.Value.RecommendedType;
                        var valid = remaining.Where(i => !invalidIds.Contains(i.Id)).ToArray();
                        var review = valid.Length == 0 ? new SharedReviewOutcome([], [], []) :
                            await ReviewBatch(valid, planned.Value with { Items = planned.Value.Items.Where(i => !invalidIds.Contains(i.Id)).ToArray() }, depth, recoveryGroup, recovery: recovery);
                        approved.AddRange(review.Approved);
                        if (review.Rejected.Count == 0 && invalid.Length == 0 || stopped is not null) break;
                        var rejectedIds = review.Rejected.Select(i => i.Id).Concat(invalidIds).ToHashSet();
                        remaining = remaining.Where(i => rejectedIds.Contains(i.Id)).ToArray();
                        var candidate = SemanticExecution.Hash(Serialize(planned.Value.Items.Where(i => rejectedIds.Contains(i.Id)).OrderBy(i => i.Id)));
                        if (!candidates.Add(candidate) || !await recovery.ObserveAsync(recoveryGroup + ":content:" + attempt, candidate,
                            review.Rejected.SelectMany(i => i.Findings?.Select(f => i.Id + ":" + f.Field + ":" + f.Code) ?? i.Issues.Select(code => i.Id + ":" + code))
                                .Concat(invalid.Select(i => i.Id + ":" + i.Problem!.Code)), attempt > firstAttempt))
                        {
                            var reasonCodes = review.Rejected.SelectMany(r => r.Issues).Distinct().ToArray();
                            Fail(remaining, "semantic-review", new("LLM_REPAIR_NO_PROGRESS", "Rejected items did not change."),
                                issueCodes: reasonCodes, correctionInstructions: reasonCodes.Select(SharedReviewValidation.RepairInstruction).ToArray());
                            if (SemanticExecution.Current is { } unchanged)
                                await unchanged.RecordAsync(new(Guid.NewGuid().ToString("N"), unchanged.Stage, unchanged.UnitId,
                                    "Failed", DateTimeOffset.UtcNow, ErrorCode: "LLM_REPAIR_NO_PROGRESS", ValidationCode: "SharedRepairNoProgress",
                                    Purpose: "repair", Kind: "Terminal", NextAction: "RegenerateFailedUnits", RecoveryState: "Exhausted"));
                            break;
                        }
                        rejected = planned.Value; issues = new { review = SharedReviewValidation.RepairIssues(review.Rejected),
                            fields = invalid.Select(i => new { i.Id, code = i.Problem!.Code, details = i.Problem.Details,
                                instruction = SharedSemanticValidation.RepairInstruction(i.Problem.Code) }) };
                        if (recovery.ContentUsed >= DiagramRecoveryPolicy.MaximumRepairs)
                        {
                            var issueCodes = review.Rejected.SelectMany(i => i.Issues).Distinct(StringComparer.Ordinal).ToArray();
                            var fields = invalid.Select(i => i.Problem!.Details.Field).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                            var corrections = invalid.Select(i => SharedSemanticValidation.RepairInstruction(i.Problem!.Code))
                                .Concat(issueCodes.Select(SharedReviewValidation.RepairInstruction)).Distinct(StringComparer.Ordinal).ToArray();
                            Fail(remaining, invalid.Length > 0 ? "plan-validation" : "semantic-review",
                                new(invalid.Length > 0 ? "LLM_SCHEMA_INVALID" : "LLM_SEMANTIC_REVIEW", "Annotation repair exhausted."),
                                fields, issueCodes, corrections);
                            break;
                        }
                    }
                    catch (LlmClientException error) when (SharedLimit(error))
                    {
                        if (remaining.Count > 1 && depth < 12)
                        {
                            if (SemanticExecution.Current is { } execution) await execution.MarkSplitAsync();
                            var half = (remaining.Count + 1) / 2;
                            foreach (var part in new[] { remaining.Take(half).ToArray(), remaining.Skip(half).ToArray() })
                            {
                                var child = await GenerateBatch(part, depth + 1, attempt,
                                    SelectRejected(rejected, part.Select(i => i.Id).ToHashSet()), issues, recoveryGroup, recovery);
                                if (child is not null) { approved.AddRange(child.Items); summary = child.Summary; recommended = child.RecommendedType; }
                            }
                        }
                        else Fail(remaining, "generation", error);
                        break;
                    }
                    catch (LlmClientException error) when (error.Code == "LLM_SCHEMA_INVALID")
                    {
                        nextRepairKind = "format";
                        if (!await recovery.ObserveAsync(recoveryGroup + ":generation-format:" + attempt,
                            error.RejectedContent ?? "", [error.FailureKind ?? error.Code], attempt > firstAttempt))
                        { Fail(remaining, "plan-validation", new("LLM_REPAIR_NO_PROGRESS", "The generation contract did not improve.", failureKind: error.FailureKind)); break; }
                        rejected = ParseRejected(error.RejectedContent);
                        var recovered = SharedSemanticValidation.RecoverItems(error.RejectedContent, ids);
                        if (recovered.Count > 0)
                        {
                            var recoveredIds = recovered.Select(i => i.Id).ToHashSet();
                            var verified = await ReviewBatch(remaining.Where(i => recoveredIds.Contains(i.Id)).ToArray(),
                                new(summary, recommended, recovered), depth, recoveryGroup, recovery: recovery);
                            approved.AddRange(verified.Approved);
                            var done = verified.Approved.Select(i => i.Id).Concat(verified.FailedIds).ToHashSet();
                            remaining = remaining.Where(i => !done.Contains(i.Id)).ToArray();
                            if (remaining.Count == 0 || stopped is not null) break;
                            issues = new { code = error.FailureKind, details = error.ValidationDetails,
                                review = SharedReviewValidation.RepairIssues(verified.Rejected),
                                instruction = SharedSemanticValidation.RepairInstruction(error.FailureKind ?? "") };
                        }
                        else
                        issues = new { code = error.FailureKind, details = error.ValidationDetails,
                            instruction = SharedSemanticValidation.RepairInstruction(error.FailureKind ?? "") };
                        if (recovery.FormatUsed >= DiagramRecoveryPolicy.MaximumRepairs)
                        {
                            Fail(remaining, "plan-validation", error,
                                error.ValidationDetails?.Field is { } field ? [field] : null,
                                correctionInstructions: [SharedSemanticValidation.RepairInstruction(error.FailureKind ?? "")]);
                            break;
                        }
                    }
                    catch (LlmClientException error) { Fail(remaining, "generation", error); break; }
                }
                return new SharedSemanticResponse(summary, recommended, approved, failures.Skip(failureStart).ToArray());
            }, value => value.Items.Count > 0);
            if (SemanticExecution.Current is { } finished)
                await finished.SetRecoveryAsync(recoveryGroup, result?.Items.Count == batch.Count ? "Recovered" :
                    stopped is not null ? "RequiresAction" : "Exhausted", descendants: true);
            return result;
        }

        async Task<SharedReviewOutcome> ReviewBatch(IReadOnlyList<SharedSemanticItem> batch, SharedSemanticResponse proposed,
            int depth, string parentGroup, int correctionUsed = 0, DiagramRecoveryBudget? recovery = null)
        {
            var prompt = Serialize(new { context = Context(batch), proposed });
            var key = Serialize(new { policy = SemanticExecution.SharedPolicyVersion, prompt, correctionUsed });
            var group = SemanticExecution.Hash("review:" + key)[..16];
            recovery ??= new DiagramRecoveryBudget("shared-review:" + parentGroup);
            var ids = batch.Select(i => i.Id).ToHashSet();
            var outcome = (await SemanticExecution.RunAsync("shared-review", key, async () =>
            {
                if (stopped is not null) { Fail(batch, "semantic-review", stopped); return new SharedReviewOutcome([], [], ids.ToArray()); }
                using var budgetScope = SemanticExecution.Current?.BeginRequestScope(group, parentGroup, correctionUsed + 1);
                var needed = SharedReviewValidation.OutputBudget(prompt, ids);
                if (needed > reviewTokens)
                {
                    if (batch.Count > 1 && depth < 12) return await Split(correctionUsed);
                    if (SemanticExecution.Current is { } execution)
                        await execution.RecordAsync(new(Guid.NewGuid().ToString("N"), execution.Stage, execution.UnitId,
                            "Failed", DateTimeOffset.UtcNow, OutputLimit: reviewTokens, ErrorCode: "LLM_OUTPUT_BUDGET",
                            Purpose: "review", RequiredOutputTokens: needed));
                    Fail(batch, "semantic-review", new("LLM_OUTPUT_BUDGET", "Review budget is insufficient."));
                    return new SharedReviewOutcome([], [], ids.ToArray());
                }
                string? reviewFailure = null;
                string? rejectedReview = null;
                LlmValidationDetails? reviewDetails = null;
                for (var correction = correctionUsed; correction < DiagramRecoveryPolicy.MaximumAttempts; correction++)
                {
                    using var requestScope = SemanticExecution.Current?.BeginRequestScope(group, parentGroup, correction + 1);
                    try
                    {
                        if (correction > correctionUsed)
                        {
                            if (!await recovery.ObserveAsync(group + ":format:" + correction, rejectedReview ?? reviewFailure ?? "",
                                [(reviewFailure ?? "SharedReviewFieldsInvalid") + ":" + reviewDetails?.Field], correction > correctionUsed + 1))
                                throw new LlmClientException("LLM_REPAIR_NO_PROGRESS", "The review contract did not improve.", failureKind: reviewFailure);
                            await recovery.ChargeAsync("format", group + ":review:" + correction);
                        }
                        var reviewPrompt = correction == 0 ? prompt : Serialize(new { context = Context(batch), proposed,
                            reviewFailure, reviewDetails, rejectedReview, correctionAttempt = correction,
                            correction = "Correct the indicated contract field. Return every id once with findings=[], or concrete grounded findings. No accepted flag." });
                        var review = await structured.CompleteAsync<GroundedSharedReview>(reviewSystem,
                            reviewPrompt, SharedReviewValidation.GroundedSchema(ids.Count), reviewTokens, thinking,
                            value => SharedReviewValidation.Check(value, batch)?.Code, ct, allowRepair: false,
                            inputTokenLimit: reviewInputLimit, inputCharacterLimit: characterLimit, requestPurpose: "review",
                            validationDetails: value => SharedReviewValidation.Check(value, batch)?.Details, responseIds: ids, allowSchemaRelaxation: true, validateSchema: true);
                        if (SemanticExecution.Current is { } validated)
                            await validated.SetRecoveryAsync(group, "Recovered", protocolOnly: true);
                        var rejected = review.Value.Items.Where(i => i.Findings.Count > 0)
                            .Select(i => new SharedItemReview(i.Id, i.Findings.Select(f => f.Code).Distinct().ToArray(), i.Findings)).ToArray();
                        var rejectedIds = rejected.Select(i => i.Id).ToHashSet();
                        return new SharedReviewOutcome(proposed.Items.Where(i => !rejectedIds.Contains(i.Id)).ToArray(), rejected, []);
                    }
                    catch (LlmClientException error) when (SharedLimit(error) || error.Code == "LLM_SCHEMA_INVALID")
                    {
                        reviewFailure = error.FailureKind ?? error.Code;
                        rejectedReview = error.RejectedContent;
                        reviewDetails = error.ValidationDetails;
                        // A review protocol failure never consumes an annotation repair.
                        if (error.Code == "LLM_SCHEMA_INVALID" && correction < DiagramRecoveryPolicy.MaximumRepairs ||
                            error.Code == "LLM_RESPONSE_TRUNCATED" && batch.Count == 1 && correction < DiagramRecoveryPolicy.MaximumRepairs) continue;
                        if (SharedLimit(error) && batch.Count > 1 && depth < 12) return await Split(correction);
                        Fail(batch, "semantic-review", error);
                        return new SharedReviewOutcome([], [], ids.ToArray());
                    }
                    catch (LlmClientException error)
                    { Fail(batch, "semantic-review", error); return new SharedReviewOutcome([], [], ids.ToArray()); }
                }
                return new SharedReviewOutcome([], [], ids.ToArray());

                async Task<SharedReviewOutcome> Split(int used)
                {
                    if (SemanticExecution.Current is { } execution) await execution.MarkSplitAsync();
                    var approved = new List<SharedSemanticAnnotation>();
                    var rejected = new List<SharedItemReview>(); var failed = new List<string>();
                    var half = (batch.Count + 1) / 2;
                    foreach (var part in new[] { batch.Take(half).ToArray(), batch.Skip(half).ToArray() })
                    {
                        var partIds = part.Select(i => i.Id).ToHashSet();
                        var result = await ReviewBatch(part, proposed with { Items = proposed.Items.Where(i => partIds.Contains(i.Id)).ToArray() }, depth + 1, group, used, recovery);
                        approved.AddRange(result.Approved); rejected.AddRange(result.Rejected); failed.AddRange(result.FailedIds);
                    }
                    return new SharedReviewOutcome(approved, rejected, failed);
                }
            }, _ => true))!;
            if (SemanticExecution.Current is { } finished)
                await finished.SetRecoveryAsync(group, outcome.FailedIds.Count > 0 ? stopped is not null ? "RequiresAction" : "Exhausted" :
                    outcome.Rejected.Count > 0 ? "Retrying" : "Recovered");
            await Publish(proposed with { Items = outcome.Approved, Failures = failures.ToArray() });
            return outcome;
        }
    }

    private static bool SharedLimit(LlmClientException error) => error.Code is
        "LLM_INPUT_CHARACTERS" or "LLM_INPUT_LIMIT" or "LLM_CONTEXT_LIMIT" or "LLM_RESPONSE_TRUNCATED";

    private static object? ParseRejected(string? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException) { return new { invalidJson = true }; }
    }

    private static object? SelectRejected(object? value, IReadOnlySet<string> ids)
    {
        if (value is null) return null;
        var node = JsonSerializer.SerializeToNode(value, PromptJson.Options);
        if (node is not JsonObject obj) return new { invalidJson = true };
        // Keep only the contract fields and annotations for this repair partition.
        // The full rejected response remains in the private checkpoint, not a log.
        var selected = new JsonObject();
        foreach (var name in new[] { "summary", "recommendedType" }) selected[name] = obj[name]?.DeepClone();
        selected["items"] = new JsonArray((obj["items"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(i => i["id"] is JsonValue id && id.TryGetValue<string>(out var text) && ids.Contains(text))
            .Select(i => (JsonNode?)new JsonObject { ["id"] = i["id"]?.DeepClone(), ["summary"] = i["summary"]?.DeepClone(),
                ["description"] = i["description"]?.DeepClone() }).ToArray());
        return selected;
    }

    private static object? SelectIssues(object? value, IReadOnlySet<string> ids)
    {
        if (value is null) return null;
        var node = JsonSerializer.SerializeToNode(value, PromptJson.Options);
        if (node is not JsonObject obj) return node;
        foreach (var name in new[] { "review", "fields" })
            if (obj[name] is JsonArray entries)
                obj[name] = new JsonArray(entries.OfType<JsonObject>()
                    .Where(entry => entry["id"] is JsonValue id && id.TryGetValue<string>(out var text) && ids.Contains(text))
                    .Select(entry => (JsonNode?)entry.DeepClone()).ToArray());
        return obj;
    }
}

internal sealed record SharedReviewOutcome(IReadOnlyList<SharedSemanticAnnotation> Approved,
    IReadOnlyList<SharedItemReview> Rejected, IReadOnlyList<string> FailedIds);
