using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Contracts;

/// <summary>Connection kinds (spec 006 FR-011). One connection per external system instance.</summary>
public static class ConnectionKinds
{
    public const string Microsoft365 = "microsoft365";
}

/// <summary>Features of a Microsoft 365 connection. Each declares its own Microsoft permissions and capability key.</summary>
public static class Microsoft365Features
{
    public const string DirectorySync = "directory_sync";
    public const string Mail = "mail";
    public const string Calendar = "calendar";
    public const string FreeBusy = "free_busy";
}

/// <summary>Connection status (spec 006 FR-014).</summary>
public static class ConnectionStatuses
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string NeedsConsent = "needs_consent";
    public const string CredentialExpiring = "credential_expiring";
    public const string Disabled = "disabled";
}

/// <summary>What a feature owner needs to know about the connection that serves it. No secrets.</summary>
public sealed record ConnectionFeatureInfo(Guid ConnectionId, string Kind, string ExternalTenantId, string Feature, bool Usable, string Status);

/// <summary>Read side for feature owners (spec 003 directory sync, spec 018 mail and calendar). Tenant comes from context.</summary>
public interface IConnectionFeatures
{
    /// <summary>The connection that has this feature turned on, or null. <c>Usable</c> is false while disabled or awaiting consent.</summary>
    Task<ConnectionFeatureInfo?> FindAsync(string kind, string feature, CancellationToken ct = default);
}

/// <summary>Feature owners describe what depends on a connection before it is deleted (spec 006 US5 scenario 8).</summary>
public interface IConnectionDependents
{
    Task<IReadOnlyList<ConnectionDependent>> DescribeAsync(Guid connectionId, CancellationToken ct = default);
}

/// <summary>A resource key and a count, for example <c>integrations.dependent.synced_users</c> and 3400.</summary>
public sealed record ConnectionDependent(string LabelKey, long Count);

// Events (spec 006 section 12)
public sealed record ConnectionCreated(Guid ConnectionId, string Kind) : IIntegrationEvent;
public sealed record ConnectionFeatureChanged(Guid ConnectionId, string Feature, bool Enabled) : IIntegrationEvent;
public sealed record ConnectorUnavailable(Guid ConnectionId, string Status) : IIntegrationEvent;
public sealed record ConnectorRestored(Guid ConnectionId) : IIntegrationEvent;
public sealed record ConnectionDeleted(Guid ConnectionId, string Kind) : IIntegrationEvent;
