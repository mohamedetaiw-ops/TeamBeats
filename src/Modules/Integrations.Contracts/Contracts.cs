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

/// <summary>
/// A directory account in canonical form (constitution XVII: no external DTOs leave Integrations). On an incremental
/// read a null value means "not reported", so the previous value is kept; an empty string means the directory cleared it.
/// </summary>
public sealed record DirectoryUserRecord(
    string ExternalId,
    bool Removed,
    bool? AccountEnabled,
    string? DisplayName,
    string? Email,
    string? UserPrincipalName,
    string? JobTitle,
    string? Office,
    string? Phone,
    string? EmployeeId,
    string? PreferredLanguage,
    string? CompanyName = null,
    string? Department = null,
    string? Division = null);

/// <summary>A directory group and the member changes since the last read.</summary>
public sealed record DirectoryGroupRecord(string ExternalId, bool Removed, string? DisplayName, IReadOnlyList<string> MembersAdded, IReadOnlyList<string> MembersRemoved);

/// <summary>
/// Changes since the cursors (a full read when they are null), and the cursors for the next read. Administrative units
/// (Entra ID) are read like groups, for the organization mapping in spec 002 US5.
/// </summary>
public sealed record DirectoryChanges(
    IReadOnlyList<DirectoryUserRecord> Users,
    IReadOnlyList<DirectoryGroupRecord> Groups,
    string UsersCursor,
    string GroupsCursor,
    IReadOnlyList<DirectoryGroupRecord>? AdminUnits = null,
    string? AdminUnitsCursor = null);

public sealed class DirectoryUnavailableException(string reason) : Exception(reason)
{
    /// <summary>Short reason for the run log and audit, for example <c>unreachable</c> or <c>consent_missing</c>.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Reads a connected directory for directory sync (spec 003 User Story 6). Read only.</summary>
public interface IDirectorySource
{
    /// <summary>The OIDC issuer of the connected directory, so synced and signed-in identities meet (spec 003 FR-003).</summary>
    string IssuerFor(string externalTenantId);

    Task<DirectoryChanges> ReadChangesAsync(Guid connectionId, string? usersCursor, string? groupsCursor, string? adminUnitsCursor = null, CancellationToken ct = default);
}

/// <summary>An employee connected Outlook for this session (spec 018 section 12). No token content.</summary>
public sealed record OutlookConnected(Guid UserId, IReadOnlyList<string> Features) : IIntegrationEvent;

/// <summary>An employee disconnected Outlook, or TeamBeats dropped the delegated access (spec 018 FR-013).</summary>
public sealed record OutlookDisconnected(Guid UserId, string Reason) : IIntegrationEvent;

/// <summary>
/// The state that comes with every mail, calendar and free and busy answer (spec 018 plan "Ports"). Components map each
/// one to a shared state of spec 008 FR-006.
/// </summary>
public static class M365States
{
    public const string Ok = "ok";
    public const string NotConnected = "not_connected";
    public const string Reconnect = "reconnect";
    public const string AdminSetup = "admin_setup";
    public const string NoMailbox = "no_mailbox";
    public const string TurnedOff = "turned_off";
    public const string ConditionalAccess = "conditional_access";
    public const string Degraded = "degraded";
    public const string NotInPlan = "not_in_plan";
}
