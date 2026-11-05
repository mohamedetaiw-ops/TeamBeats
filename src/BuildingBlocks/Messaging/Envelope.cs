using System.Reflection;
using System.Text.Json;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Messaging;

/// <summary>
/// What travels on the broker. Every message carries tenant, correlation and message IDs (ADR-0006);
/// consumers re-establish tenant context from <see cref="TenantId"/>, never from the payload.
/// </summary>
public sealed record Envelope(
    Guid MessageId,
    string MessageType,
    Guid? TenantId,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    string Payload,
    int Attempt = 0)
{
    public static string TypeName(Type eventType) => eventType.Name;

    public static bool IsPlatformMessage(Type eventType) => eventType.GetCustomAttribute<PlatformMessageAttribute>() is not null;

    public T Deserialize<T>() => JsonSerializer.Deserialize<T>(Payload, JsonDefaults.Options)
        ?? throw new InvalidOperationException($"Empty payload for {MessageType}.");
}
