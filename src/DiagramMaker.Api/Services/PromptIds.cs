using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiagramMaker.Services;

internal sealed class PromptIds
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> originals = new(StringComparer.Ordinal);
    private static bool Reference(string key) => key.Equals("id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Ids", StringComparison.OrdinalIgnoreCase);
    public string Encode(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            Visit(root, "", (value, key) => { if (Reference(key)) reserved.Add(value); return value; });
            var index = names.Count;
            Visit(root, "", (value, key) => {
                if (!Reference(key) || value.Length < 16) return value;
                if (!names.TryGetValue(value, out var alias))
                {
                    do { alias = "ref" + (++index); } while (reserved.Contains(alias) || originals.ContainsKey(alias));
                    names[value] = alias; originals[alias] = value;
                }
                return alias;
            });
            return root?.ToJsonString(PromptJson.Options) ?? json;
        }
        catch (JsonException) { return json; }
    }
    public string Restore(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            Visit(root, "", (value, key) => Reference(key) ? originals.GetValueOrDefault(value, value) : value);
            return root?.ToJsonString(PromptJson.Options) ?? json;
        }
        catch (JsonException) { return json; }
    }
    private static void Visit(JsonNode? node, string key, Func<string, string, string> replace)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj.ToArray())
                if (child is JsonValue value && value.TryGetValue<string>(out var text)) obj[name] = replace(text, name);
                else Visit(child, name, replace);
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var text)) array[i] = replace(text, key);
                else Visit(array[i], key, replace);
    }
}

internal static class PromptJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
}
