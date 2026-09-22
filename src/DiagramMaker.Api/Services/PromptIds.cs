using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiagramMaker.Services;

internal sealed class PromptIds(IReadOnlySet<string>? responseIds = null)
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> originals = new(StringComparer.Ordinal);
    private static bool Reference(string key) => key.Equals("id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Ids", StringComparison.OrdinalIgnoreCase);
    private static bool Link(string key) => key is "source" or "target";
    public string Encode(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            Visit(root, "", (value, key) => { if (Reference(key)) reserved.Add(value); return value; });
            var index = names.Count;
            Visit(root, "", (value, key) => {
                if (!Reference(key) || value.Length < 16 && responseIds?.Contains(value) != true) return value;
                if (!names.TryGetValue(value, out var alias))
                {
                    var prefix = responseIds?.Contains(value) == true ? "item" : "ref";
                    do { alias = prefix + (++index); } while (reserved.Contains(alias) || originals.ContainsKey(alias));
                    names[value] = alias; originals[alias] = value;
                }
                return alias;
            });
            // Link fields reference declared IDs; a prose field named source must
            // never create an alias of its own.
            Visit(root, "", (value, key) => Link(key) ? names.GetValueOrDefault(value, value) : value);
            return root?.ToJsonString(PromptJson.Options) ?? json;
        }
        catch (JsonException) { return json; }
    }
    public JsonElement BindSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        Bind(node, "");
        if (responseIds is not null)
            node["properties"]!["items"]!["items"]!["properties"]!["id"]!["enum"] =
                JsonSerializer.SerializeToNode(responseIds.Select(id => names.GetValueOrDefault(id, id)).Order(StringComparer.Ordinal));
        return JsonSerializer.SerializeToElement(node);

        void Bind(JsonNode value, string field)
        {
            if (value is not JsonObject obj) return;
            if ((Reference(field) || Link(field)) && obj["enum"] is JsonArray choices)
                for (var i = 0; i < choices.Count; i++)
                    if (choices[i] is JsonValue choice && choice.TryGetValue<string>(out var text))
                        choices[i] = names.GetValueOrDefault(text, text);
            if (obj["properties"] is JsonObject properties)
                foreach (var (name, child) in properties)
                    if (child is not null) Bind(child, name);
            if (obj["items"] is { } items) Bind(items, field);
        }
    }
    public int CountNonTargetIds(IEnumerable<string> ids) => ids.Count(id =>
        responseIds?.Contains(id) != true && names.ContainsKey(id));
    public SharedValidationProblem? CheckResponseMembership(string json, bool review)
    {
        if (responseIds is null) return null;
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var expected = responseIds.Select(id => names.GetValueOrDefault(id, id)).ToHashSet();
        var returned = items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object &&
            i.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String).Select(i => i.GetProperty("id").GetString()!).ToArray();
        var unknown = returned.Where(id => !expected.Contains(id)).ToArray();
        if (unknown.Length == 0) return null;
        var nonTarget = unknown.Count(id => originals.TryGetValue(id, out var original) && !responseIds.Contains(original));
        return new(review ? "SharedReviewUnknownIds" : "SharedUnknownIds", new(expected.Count, items.GetArrayLength(),
            expected.Except(returned).Count(), returned.Length - returned.Distinct().Count(), unknown.Length,
            NonTargetItems: nonTarget, UnknownAliases: unknown.Length - nonTarget));
    }
    public string Restore(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            Visit(root, "", (value, key) => Reference(key) || Link(key) ? originals.GetValueOrDefault(value, value) : value);
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
