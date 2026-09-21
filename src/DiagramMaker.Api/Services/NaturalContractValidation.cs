using System.Text.Json;
using DiagramMaker.Domain;

namespace DiagramMaker.Services;

// Enforce the original constraints even when a serving grammar needs a reduced
// schema. Paths contain schema property names only, never model-provided keys.
internal static class NaturalContractValidation
{
    public static SharedValidationProblem? Check(string json, JsonElement schema)
    {
        using var doc = JsonDocument.Parse(json);
        return Visit(doc.RootElement, schema, "response", null);
    }

    private static SharedValidationProblem? Visit(JsonElement value, JsonElement schema, string field, int? index)
    {
        SharedValidationProblem Error(string code, int? actual = null, int? limit = null) =>
            new(code, new(1, value.ValueKind == JsonValueKind.Null ? 0 : 1, Field: field, ItemIndex: index,
                ActualLength: actual, AllowedLength: limit, IssueCodes: [code]));
        if (schema.TryGetProperty("type", out var type))
        {
            var valid = type.GetString() switch {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid) return Error("NaturalFieldTypeInvalid");
        }
        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(choice => choice.GetRawText() == value.GetRawText())) return Error("NaturalFieldEnumInvalid");
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("maxLength", out var maxLength) &&
            value.GetString()!.Length > maxLength.GetInt32()) return Error("NaturalFieldTooLong", value.GetString()!.Length, maxLength.GetInt32());
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _))
                        return new("NaturalFieldMissing", new(1, 0, 1, Field: field + "." + name.GetString(), ItemIndex: index));
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (value.TryGetProperty(property.Name, out var child) && Visit(child, property.Value, field + "." + property.Name, index) is { } issue)
                        return issue;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("maxItems", out var maxItems) && value.GetArrayLength() > maxItems.GetInt32())
                return Error("NaturalTooManyItems", value.GetArrayLength(), maxItems.GetInt32());
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var i = 0;
                foreach (var child in value.EnumerateArray())
                    if (Visit(child, itemSchema, field, i++) is { } issue) return issue;
            }
        }
        return null;
    }
}
