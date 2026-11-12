using System.Text.Json;
using System.Text.Json.Serialization;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Learning;

/// <summary>JSON options for stored learning data: camelCase, nulls left out.</summary>
internal static class LearningJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static Jsonb Jsonb<T>(T value) => new(JsonSerializer.Serialize(value, Options));
}
