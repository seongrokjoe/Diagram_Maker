using System.Text;
using System.Text.Json;
using DiagramMaker.Configuration;

namespace DiagramMaker.Services;

// Conservative fallback shared by batching, structured repair and transport.
// Exact message token counts, when available, come from the approved server.
internal static class LlmRequestBudget
{
    public static (int Characters, int Tokens) Measure(string system, string prompt, JsonElement? schema)
    {
        var schemaText = schema?.GetRawText() ?? "";
        return (system.Length + prompt.Length + schemaText.Length + 64,
            Encoding.UTF8.GetByteCount(system) + Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(schemaText) + 320);
    }

    public static int InputLimit(LlmOptions options, int output, int? requested = null) =>
        Math.Min(requested ?? options.MaxInputTokens, Math.Min(options.MaxInputTokens, options.MaxContextTokens - output - 1024));
}
