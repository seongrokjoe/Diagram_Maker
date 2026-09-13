using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DiagramMaker.Services;

// Server text is untrusted and stays in memory. Only these fixed categories may
// leave the transport; a proxy can echo source, credentials or its address.
internal static class LlmRequestCompatibility
{
    public static string Classify(int status, string body)
    {
        if (status is 401 or 403) return "authentication";
        if (status == 429) return "capacity";
        if (status >= 500 && status != 501) return "server";
        if (status is not (400 or 404 or 422 or 501)) return "unknown";
        bool Has(string pattern) => Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        try
        {
            if (Has("context.length|max_model_len|maximum context|too many tokens|input.{0,100}tokens.{0,100}exceed")) return "context";
            if (Has("model.{0,100}(not found|does not exist|not served|unknown)|unknown model")) return "model";
            if (Has("max_tokens|max_completion_tokens") && Has("too large|greater than|must be|exceed|at most")) return "output-limit";
            if (Has("json[ _]schema|grammar|xgrammar|guidance") &&
                Has("features.{0,100}not supported|unsupported.{0,100}(constraint|feature)|failed to (transform|compile)|uniqueItems|minItems|maxItems|minLength|maxLength"))
                return "schema-constraint";
            if (Has("structured_outputs|response_format|guided_json|json_schema") &&
                Has("unsupported|not supported|unrecognized|unknown|not permitted|extra inputs|not implemented")) return "output-field";
            if (Has("chat_template|enable_thinking") && Has("unsupported|not supported|invalid|not permitted|extra inputs")) return "template";
        }
        catch (RegexMatchTimeoutException) { /* Unknown is safer than guessing a retry. */ }
        return "unknown";
    }

    // Only shared annotation/review callers opt in. Their local validators enforce
    // these constraints even if the serving grammar cannot compile them.
    public static JsonElement Relax(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        Visit(node);
        return JsonSerializer.SerializeToElement(node);
        static void Visit(JsonNode? value)
        {
            if (value is JsonObject obj)
            {
                foreach (var key in new[] { "uniqueItems", "minItems", "maxItems", "minLength", "maxLength" }) obj.Remove(key);
                foreach (var child in obj.ToArray()) Visit(child.Value);
            }
            else if (value is JsonArray array) foreach (var child in array) Visit(child);
        }
    }

    public static string Action(string? category) => category switch
    {
        "authentication" => "check-access",
        "model" => "check-model",
        "context" or "output-limit" => "check-token-limits",
        "output-field" or "schema-constraint" or "template" => "check-server-contract",
        "server" or "capacity" => "retry-later",
        _ => "check-server-error"
    };
}
