using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Contracts;

/// <summary>Connection kinds (spec 006 FR-011). One connection per external system instance.</summary>
public static class ConnectionKinds
{
    public const string Microsoft365 = "microsoft365";

    /// <summary>On-premises Active Directory read by the customer's sync agent over LDAPS (spec 017 US6).</summary>
    public const string OnPremAd = "onprem_ad";
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
/// A signed inbound webhook that passed its checks (spec 017 US5, FR-014), mapped to a product event or a process
/// message (spec 024 FR-009). <paramref name="Target"/> is <c>event</c> or <c>message</c>; <paramref name="Name"/> is the
/// configured event or message name; <paramref name="CorrelationKey"/> is set for messages. Only the declared fields
/// are carried, as text.
/// </summary>
public sealed record InboundWebhookReceived(
    Guid ConnectionId,
    string DeliveryId,
    string Target,
    string Name,
    string? CorrelationKey,
    IReadOnlyDictionary<string, string?> Fields) : IIntegrationEvent;

/// <summary>
/// A directory account in canonical form (constitution XVII: no external DTOs leave Integrations). On an incremental
/// read a null value means "not reported", so the previous value is kept; an empty string means the directory cleared it.
/// <see cref="ManagerId"/> is the manager's directory ID; <see cref="HireDate"/> is the ISO date the directory reports (spec 010).
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
    string? Division = null,
    string? WorkPhone = null,
    string? MobilePhone = null,
    string? ManagerId = null,
    string? HireDate = null);

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

// Canonical mail, calendar and free and busy ports (spec 018 FR-024). They act as the session's membership only and take
// no user ID for mail and calendar. Provider types stay inside each adapter, so another provider can be added later.

/// <summary>What to read from the Inbox. <see cref="View"/> is <c>focused</c> or <c>all</c>.</summary>
public sealed record MailQuery(string View, int Top, bool Refresh);

public sealed record MailMessage(
    string Id,
    string Sender,
    string? SenderAddress,
    string Subject,
    DateTimeOffset ReceivedAt,
    bool IsRead,
    string Importance,
    bool HasAttachments,
    string? Preview,
    string WebLink);

/// <summary>The Inbox summary. <see cref="AsOf"/> is when the data was read from the provider (older when degraded).</summary>
public sealed record MailSummary(string State, DateTimeOffset? AsOf, int? UnreadCount, IReadOnlyList<MailMessage> Messages, string? ComposeLink);

public interface IMailbox
{
    Task<MailSummary> GetInboxAsync(MailQuery query, CancellationToken ct = default);
}

/// <summary>
/// One occurrence (series are expanded). <see cref="ShowAs"/> is free, tentative, busy, oof, working_elsewhere or unknown;
/// <see cref="Response"/> is none, organizer, accepted, tentative, declined or not_responded.
/// </summary>
public sealed record CalendarOccurrence(
    string Id,
    string? Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string ShowAs,
    bool IsPrivate,
    bool IsCancelled,
    string Response,
    string? Location,
    string? OnlineJoinUrl,
    string WebLink);

public sealed record CalendarQuery(DateTimeOffset From, DateTimeOffset To, bool Refresh);

public sealed record CalendarResult(string State, DateTimeOffset? AsOf, IReadOnlyList<CalendarOccurrence> Occurrences);

public interface ICalendar
{
    Task<CalendarResult> GetEventsAsync(CalendarQuery query, CancellationToken ct = default);
}

/// <summary>A status block only: free, busy, tentative, oof or working_elsewhere. Never a subject, location or attendee.</summary>
public sealed record AvailabilityBlock(string Status, DateTimeOffset Start, DateTimeOffset End);

/// <summary><see cref="Shared"/> is false when the colleague's mailbox does not share free and busy with the viewer.</summary>
public sealed record AvailabilityResult(string State, bool Shared, IReadOnlyList<AvailabilityBlock> Blocks);

public interface IAvailability
{
    /// <summary>Free and busy of one colleague, read as the signed-in employee, so never more than Outlook shows them.</summary>
    Task<AvailabilityResult> GetAsync(string colleagueAddress, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

// Weather for the tool box (spec 008 FR-050). A canonical port, so the provider can be swapped by configuration; provider
// types stay inside the adapter. Weather is public data and needs no connection or capability.

/// <summary>The state that comes with every weather answer; the component maps it to a shared state of spec 008 FR-006.</summary>
public static class WeatherStates
{
    public const string Ok = "ok";
    public const string NotFound = "not_found";
    /// <summary>The provider did not answer; the last good report is returned with its time.</summary>
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
}

/// <summary>Weather conditions the component can name, grouped from the provider's codes.</summary>
public static class WeatherConditions
{
    public const string Clear = "clear";
    public const string PartlyCloudy = "partly_cloudy";
    public const string Cloudy = "cloudy";
    public const string Fog = "fog";
    public const string Drizzle = "drizzle";
    public const string Rain = "rain";
    public const string Snow = "snow";
    public const string Thunderstorm = "thunderstorm";
    public const string Unknown = "unknown";
}

public sealed record WeatherPlace(string Name, string? Country, double Latitude, double Longitude);

/// <summary>Current weather and today's range, in degrees Celsius; the component formats units for the viewer.</summary>
public sealed record WeatherReport(WeatherPlace Place, double TemperatureC, string Condition, double? HighC, double? LowC, DateTimeOffset ObservedAt);

/// <summary><see cref="AsOf"/> is when the report was read from the provider (older when degraded).</summary>
public sealed record WeatherResult(string State, DateTimeOffset? AsOf, WeatherReport? Report);

public interface IWeather
{
    /// <summary>Today's weather for a place name, with place names in <paramref name="locale"/> where the provider has them.</summary>
    Task<WeatherResult> TodayAsync(string location, string locale, bool refresh = false, CancellationToken ct = default);
}

/// <summary>What a connector action does to the external system (spec 017 FR-008). Writes get the stricter defaults of spec 027.</summary>
public static class ConnectorSideEffects
{
    public const string Read = "read";
    public const string Write = "write";
    public const string ExternalEffect = "external_effect";
}

/// <summary>How a connector call ended (spec 017 US2 and US3).</summary>
public static class ConnectorOutcomes
{
    /// <summary>The external system did what was asked.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The system refused for a business reason the action declares; the caller can branch on the code. Not retried.</summary>
    public const string BusinessError = "business_error";

    /// <summary>Technical failure after the retries, or a refused credential. The run is a dead letter that can be replayed.</summary>
    public const string Failed = "failed";

    /// <summary>A write that may have acted and cannot be retried safely (FR-010). The operator decides.</summary>
    public const string Unknown = "unknown";

    /// <summary>Not attempted: the connection is disabled, missing, outside the package, or the action is unknown.</summary>
    public const string Refused = "refused";

    /// <summary>Accepted as a durable job; <see cref="ConnectorActionCompleted"/> follows.</summary>
    public const string Queued = "queued";

    /// <summary>A call with the same idempotency key is still running.</summary>
    public const string InProgress = "in_progress";
}

/// <summary>
/// One call of a connector action by name (spec 017 FR-009). <paramref name="Input"/> holds only the fields the caller
/// mapped. The same <paramref name="IdempotencyKey"/> on the same connection and action never runs the action twice.
/// </summary>
public sealed record ConnectorCall(Guid ConnectionId, string Action, System.Text.Json.JsonElement Input, string IdempotencyKey, string? CorrelationId = null);

/// <summary>
/// The outcome of a call. <paramref name="ErrorClass"/> is a plain class such as <c>not_responding</c> or
/// <c>credential_refused</c>; <paramref name="ErrorCode"/> is the action's business error. <paramref name="Repeated"/>
/// is true when the key had already run: the outcome and reference are the stored ones and the output is not kept.
/// </summary>
public sealed record ConnectorCallResult(
    Guid? RunId,
    string Outcome,
    System.Text.Json.JsonElement? Output = null,
    string? ExternalReference = null,
    string? ErrorClass = null,
    string? ErrorCode = null,
    bool Repeated = false);

/// <summary>
/// The connector runtime (spec 017 plan) for workflows (016, 024) and agents (027). Tenant comes from context.
/// <see cref="InvokeAsync"/> runs inside the caller's request: 3 attempts within 10 seconds. <see cref="EnqueueAsync"/>
/// runs as a durable job: 5 attempts over about 30 minutes, then <see cref="ConnectorActionCompleted"/>.
/// </summary>
public interface IConnectorRuntime
{
    Task<ConnectorCallResult> InvokeAsync(ConnectorCall call, CancellationToken ct = default);

    Task<ConnectorCallResult> EnqueueAsync(ConnectorCall call, CancellationToken ct = default);
}

/// <summary>A durable connector job finished, for good or not (spec 017 section 12). No payload content.</summary>
public sealed record ConnectorActionCompleted(
    Guid RunId,
    Guid ConnectionId,
    string Action,
    string Outcome,
    string? ExternalReference,
    string? ErrorClass,
    string? ErrorCode,
    string? CorrelationId) : IIntegrationEvent;

/// <summary>
/// The on-premises sync agent protocol (spec 017 US6, FR-016). The agent holds an ECDSA P-256 key pair made at
/// enrolment; TeamBeats keeps only the public key, bound to one tenant and one connection. Every agent request names the
/// agent and is signed over <c>{METHOD}\n{path}\n{timestamp}\n{hex SHA-256 of body}</c> with the private key (IEEE P1363
/// signature, Base64). Requests go outbound from the customer network to the tenant's own host, so no inbound port is
/// opened and the proof works behind TLS-terminating proxies.
/// </summary>
public static class AgentProtocol
{
    public const string AgentHeader = "X-TeamBeats-Agent";
    public const string TimestampHeader = "X-TeamBeats-Agent-Timestamp";
    public const string SignatureHeader = "X-TeamBeats-Agent-Signature";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static string Canonical(string method, string pathAndQuery, long timestamp, ReadOnlySpan<byte> body) =>
        $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body))}";

    public static string Sign(System.Security.Cryptography.ECDsa key, string method, string pathAndQuery, long timestamp, ReadOnlySpan<byte> body) =>
        Convert.ToBase64String(key.SignData(System.Text.Encoding.UTF8.GetBytes(Canonical(method, pathAndQuery, timestamp, body)),
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>The SHA-256 thumbprint (lowercase hex) of a public key in SubjectPublicKeyInfo form.</summary>
    public static string Thumbprint(byte[] subjectPublicKeyInfo) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(subjectPublicKeyInfo));
}

/// <summary>
/// A job an agent pulled: <paramref name="Kind"/> is <c>directory_sync</c>; settings are the connection's, without
/// secrets. The cursors are where the read starts (the <c>uSNChanged</c> values the agent reported last time); both null
/// mean a full read.
/// </summary>
public sealed record AgentJobView(Guid Id, string Kind, System.Text.Json.JsonElement Settings, DateTimeOffset LeaseUntil, string? UsersCursor = null,
    string? GroupsCursor = null);

/// <summary>A batch of canonical directory records an agent returns for a job, at most 1,000 of each per call.</summary>
public sealed record AgentRecordBatch(IReadOnlyList<DirectoryUserRecord>? Users, IReadOnlyList<DirectoryGroupRecord>? Groups);

/// <summary>The end of a job: <paramref name="Ok"/>, or the plain error class of the failure (spec 017 section 10).</summary>
public sealed record AgentJobResult(bool Ok, string? ErrorClass, string? UsersCursor = null, string? GroupsCursor = null);

/// <summary>
/// Directory reads through the on-premises sync agent, for spec 003 directory sync. A read is asynchronous: Identity
/// asks for one, the agent does it, and <see cref="AgentJobFinished"/> says when the result can be read. Integrations
/// keeps the read position, so every succeeded job, also one an administrator started, carries on from the last.
/// </summary>
public interface IAgentDirectory
{
    /// <summary>True when the connection is an enabled on-premises AD connection in the package.</summary>
    Task<bool> IsUsableAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Queues a read that starts where the last succeeded read ended (a full read the first time). Returns the job, or
    /// null when a read is already waiting or running.
    /// </summary>
    Task<Guid?> RequestReadAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>The changes a succeeded job returned, with the cursors for the next read; null for any other job.</summary>
    Task<DirectoryChanges?> ReadResultAsync(Guid jobId, CancellationToken ct = default);
}

/// <summary>An agent job that finished, for the module that asked for it (spec 003 reads the records it returned).</summary>
public sealed record AgentJobFinished(Guid JobId, Guid ConnectionId, string Kind, bool Ok, string? ErrorClass, int Users, int Groups) : IIntegrationEvent;
