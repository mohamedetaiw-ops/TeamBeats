using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Engagement;

/// <summary>JSON options for stored engagement data: camelCase, nulls left out so stored bodies stay small and stable.</summary>
internal static class EngagementJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static Jsonb Jsonb<T>(T value) => new(JsonSerializer.Serialize(value, Options));
}
