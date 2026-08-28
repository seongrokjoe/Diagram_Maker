using System.Text.Json;

namespace DiagramMaker.Services;

public sealed record StructuredCompletionResult<T>(T Value, VllmCompletionResult Completion, bool RepairUsed);

public sealed class StructuredLlmCompletion(VllmClient client)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<StructuredCompletionResult<T>> CompleteAsync<T>(
        string systemPrompt, string userPrompt, JsonElement schema, int maxOutputTokens,
        bool enableThinking, Func<T, string?> validator, CancellationToken cancellationToken)
    {
        var first = await client.CompleteAsync(new VllmCompletionRequest(
            systemPrompt, userPrompt, maxOutputTokens, enableThinking, schema), cancellationToken);
        ThrowIfTruncated(first, initialFailureKind: null, repairAttempted: false);
        var firstAttempt = Deserialize(first.Content, validator);
        if (firstAttempt.Value is not null)
            return new StructuredCompletionResult<T>(firstAttempt.Value, first, RepairUsed: false);

        var repairSystem = systemPrompt +
            $"\nThe previous response failed the required JSON contract ({firstAttempt.FailureKind}). " +
            "Return exactly one JSON object matching the schema, without markdown or explanation. " +
            "Use unique node IDs and make every edge sourceId and targetId exactly match a returned node ID.";
        var repaired = await client.CompleteAsync(new VllmCompletionRequest(
            repairSystem, userPrompt, maxOutputTokens, enableThinking, schema), cancellationToken);
        ThrowIfTruncated(repaired, firstAttempt.FailureKind, repairAttempted: true);
        var repairedAttempt = Deserialize(repaired.Content, validator);
        if (repairedAttempt.Value is null)
            throw new LlmClientException(
                "LLM_SCHEMA_INVALID",
                $"The internal LLM did not return a valid structured result after repair ({repairedAttempt.FailureKind}).",
                failureKind: repairedAttempt.FailureKind,
                initialFailureKind: firstAttempt.FailureKind,
                repairAttempted: true);

        var merged = repaired with
        {
            StructuredOutputFallbackUsed = first.StructuredOutputFallbackUsed || repaired.StructuredOutputFallbackUsed
        };
        return new StructuredCompletionResult<T>(repairedAttempt.Value, merged, RepairUsed: true);
    }

    private static void ThrowIfTruncated(VllmCompletionResult result, string? initialFailureKind, bool repairAttempted)
    {
        if (result.FinishReason.Equals("length", StringComparison.OrdinalIgnoreCase))
            throw new LlmClientException(
                "LLM_RESPONSE_TRUNCATED",
                "The internal LLM stopped because the output limit was reached.",
                failureKind: "Truncated",
                initialFailureKind: initialFailureKind,
                repairAttempted: repairAttempted,
                requestedMaxOutputTokens: result.RequestedMaxOutputTokens,
                promptTokens: result.PromptTokens,
                completionTokens: result.CompletionTokens,
                totalTokens: result.TotalTokens);
    }

    private static StructuredAttempt<T> Deserialize<T>(string content, Func<T, string?> validator)
    {
        var normalized = NormalizeJson(content);
        if (normalized.Json is null) return new StructuredAttempt<T>(default, normalized.FailureKind);

        try
        {
            var value = JsonSerializer.Deserialize<T>(normalized.Json, JsonOptions);
            if (value is null) return new StructuredAttempt<T>(default, "Deserialization");
            var failureKind = validator(value);
            return failureKind is null
                ? new StructuredAttempt<T>(value, null)
                : new StructuredAttempt<T>(default, failureKind);
        }
        catch (JsonException)
        {
            return new StructuredAttempt<T>(default, "Deserialization");
        }
        catch (NotSupportedException)
        {
            return new StructuredAttempt<T>(default, "Deserialization");
        }
    }

    private static NormalizedJson NormalizeJson(string content)
    {
        var value = content.Trim();
        if (value.Length == 0) return new NormalizedJson(null, "EmptyContent");

        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            if (!value.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                return new NormalizedJson(null, "MixedContent");
            var firstLine = value.IndexOf('\n');
            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine < 0 || lastFence <= firstLine || !string.IsNullOrWhiteSpace(value[(lastFence + 3)..]))
                return new NormalizedJson(null, "MixedContent");
            value = value[(firstLine + 1)..lastFence].Trim();
            if (value.Contains("```", StringComparison.Ordinal))
                return new NormalizedJson(null, "MixedContent");
        }
        else if (value.Contains("```", StringComparison.Ordinal) ||
                 (!value.StartsWith('{') && value.Contains('{')))
        {
            return new NormalizedJson(null, "MixedContent");
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? new NormalizedJson(value, null)
                : new NormalizedJson(null, "WrongRoot");
        }
        catch (JsonException)
        {
            return new NormalizedJson(null, "MalformedJson");
        }
    }

    private sealed record StructuredAttempt<T>(T? Value, string? FailureKind);
    private sealed record NormalizedJson(string? Json, string? FailureKind);
}
