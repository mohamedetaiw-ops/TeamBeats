namespace TeamBeats.Primitives;

/// <summary>Marker for domain and integration events published through the outbox.</summary>
public interface IIntegrationEvent;

/// <summary>Messages that legitimately have no tenant (for example platform package publication).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class PlatformMessageAttribute : Attribute;
