using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamBeats.Modules.Content;

/// <summary>JSON options for stored content: camelCase, nulls left out so the stored body stays small and stable.</summary>
internal static class ContentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
}
