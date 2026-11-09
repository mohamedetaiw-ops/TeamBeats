using System.Text.Json;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// Checks model-produced tool arguments against the tool's parameter schema (spec 027 FR-008, US3 scenario 2). Tool
/// schemas use a small subset of JSON Schema: objects with <c>properties</c>, <c>required</c> and
/// <c>additionalProperties: false</c>; strings with <c>minLength</c>, <c>maxLength</c> and <c>enum</c>; integers and
/// numbers with <c>minimum</c> and <c>maximum</c>; booleans; arrays with <c>items</c> and <c>maxItems</c>. Anything the
/// subset does not describe is refused, so a schema can only narrow what a tool accepts.
/// </summary>
public static class ToolSchema
{
    public static bool Valid(JsonElement schema, JsonElement value, int depth = 0)
    {
        if (depth > 8)
        {
            return false;
        }
        if (schema.TryGetProperty("enum", out var options) && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            return false;
        }
        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
                var properties = schema.TryGetProperty("properties", out var p) ? p : default;
                if (schema.TryGetProperty("required", out var required) &&
                    required.EnumerateArray().Any(r => !value.TryGetProperty(r.GetString()!, out var v) || v.ValueKind == JsonValueKind.Null))
                {
                    return false;
                }
                foreach (var field in value.EnumerateObject())
                {
                    if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(field.Name, out var fieldSchema))
                    {
                        // Unknown fields are refused, whatever additionalProperties says.
                        return false;
                    }
                    if (!Valid(fieldSchema, field.Value, depth + 1))
                    {
                        return false;
                    }
                }
                return true;
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                var length = value.GetString()!.Length;
                return (!schema.TryGetProperty("minLength", out var min) || length >= min.GetInt32()) &&
                    (!schema.TryGetProperty("maxLength", out var max) || length <= max.GetInt32());
            case "integer" or "number":
                if (value.ValueKind != JsonValueKind.Number || (type == "integer" && !value.TryGetInt64(out _)))
                {
                    return false;
                }
                var number = value.GetDouble();
                return (!schema.TryGetProperty("minimum", out var lo) || number >= lo.GetDouble()) &&
                    (!schema.TryGetProperty("maximum", out var hi) || number <= hi.GetDouble());
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "array":
                if (value.ValueKind != JsonValueKind.Array || (schema.TryGetProperty("maxItems", out var most) && value.GetArrayLength() > most.GetInt32()))
                {
                    return false;
                }
                return !schema.TryGetProperty("items", out var items) || value.EnumerateArray().All(i => Valid(items, i, depth + 1));
            case null:
                // An enum without a type (checked above) is enough.
                return schema.TryGetProperty("enum", out _);
            default:
                return false;
        }
    }
}
