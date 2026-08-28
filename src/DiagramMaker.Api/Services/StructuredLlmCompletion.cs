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
        bool enableThinking, Func<T, bool> validator, CancellationToken cancellationToken)
    {
        var first = await client.CompleteAsync(new VllmCompletionRequest(
            systemPrompt, userPrompt, maxOutputTokens, enableThinking, schema), cancellationToken);
        ThrowIfTruncated(first);
        if (TryDeserialize(first.Content, validator, out T? value))
            return new StructuredCompletionResult<T>(value!, first, RepairUsed: false);

        var repairSystem = systemPrompt +
            "\nThe previous response failed the required JSON contract. Return exactly one JSON object matching the schema, without markdown or explanation.";
        var repaired = await client.CompleteAsync(new VllmCompletionRequest(
            repairSystem, userPrompt, maxOutputTokens, enableThinking, schema), cancellationToken);
        ThrowIfTruncated(repaired);
        if (!TryDeserialize(repaired.Content, validator, out value))
            throw new LlmClientException("LLM_SCHEMA_INVALID", "The internal LLM did not return a valid structured result.");

        var merged = repaired with
        {
            StructuredOutputFallbackUsed = first.StructuredOutputFallbackUsed || repaired.StructuredOutputFallbackUsed
        };
        return new StructuredCompletionResult<T>(value!, merged, RepairUsed: true);
    }

    private static void ThrowIfTruncated(VllmCompletionResult result)
    {
        if (result.FinishReason.Equals("length", StringComparison.OrdinalIgnoreCase))
            throw new LlmClientException("LLM_RESPONSE_TRUNCATED", "The internal LLM stopped because the output limit was reached.");
    }

    private static bool TryDeserialize<T>(string content, Func<T, bool> validator, out T? value)
    {
        value = default;
        try
        {
            value = JsonSerializer.Deserialize<T>(NormalizeJson(content), JsonOptions);
            return value is not null && validator(value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeJson(string content)
    {
        var value = content.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
        var firstLine = value.IndexOf('\n');
        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine && string.IsNullOrWhiteSpace(value[(lastFence + 3)..])
            ? value[(firstLine + 1)..lastFence].Trim()
            : value;
    }
}
