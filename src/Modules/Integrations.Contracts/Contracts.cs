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

/// <summary>A connected external system as other modules may name it: no secrets, no external identifiers.</summary>
public sealed record ConnectedSystem(Guid ConnectionId, string Kind, string DisplayName);

/// <summary>
/// The external systems the tenant has connected and enabled, for checklists such as offboarding access removal
/// (spec 022 FR-017). Tenant comes from context.
/// </summary>
public interface IConnectedSystems
{
    Task<IReadOnlyList<ConnectedSystem>> ListAsync(CancellationToken ct = default);
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

/// <summary>
/// A directory group and the member changes since the last read. When <paramref name="MembersComplete"/> is true,
/// <paramref name="MembersAdded"/> is the whole membership and replaces what was known (the on-premises agent reads every
/// member of a changed group).
/// </summary>
public sealed record DirectoryGroupRecord(string ExternalId, bool Removed, string? DisplayName, IReadOnlyList<string> MembersAdded, IReadOnlyList<string> MembersRemoved,
    bool MembersComplete = false);

/// <summary>
/// Changes since the cursors (a full read when they are null), and the cursors for the next read. Administrative units
/// (Entra ID) are read like groups, for the organization mapping in spec 002 US5. <paramref name="Complete"/> means the
/// read lists every user and group the directory has (Google Workspace has no change feed), so anything not listed was removed.
/// </summary>
public sealed record DirectoryChanges(
    IReadOnlyList<DirectoryUserRecord> Users,
    IReadOnlyList<DirectoryGroupRecord> Groups,
    string UsersCursor,
    string GroupsCursor,
    IReadOnlyList<DirectoryGroupRecord>? AdminUnits = null,
    string? AdminUnitsCursor = null,
    bool Complete = false);

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

/// <summary>Directories read inline by a connector pack connection.</summary>
public static class PackDirectorySources
{
    /// <summary>Google Workspace users and groups through the Admin SDK (spec 017 T019).</summary>
    public const string Google = "google";
}

/// <summary>
/// Reads the directory of a connector pack connection for directory sync (spec 003 US6, spec 017 T019). Read only. Every
/// read is complete; the user and group cursors are only the time of the read.
/// </summary>
public interface IPackDirectory
{
    /// <summary>The directory a connection reads (<see cref="PackDirectorySources"/>) when it is enabled, in the package and has directory on; otherwise null.</summary>
    Task<string?> SourceOfAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>The OIDC issuer the directory's IDs are the subjects of, so synced and signed-in identities meet (spec 003 FR-003).</summary>
    string IssuerFor(Guid connectionId);

    /// <summary>Throws <see cref="DirectoryUnavailableException"/> when the directory cannot be read; nothing is then changed.</summary>
    Task<DirectoryChanges> ReadChangesAsync(Guid connectionId, string? usersCursor, string? groupsCursor, CancellationToken ct = default);
}

/// <summary>A container of a document repository: a Google shared drive or, later, a SharePoint document library (ADR-0016).</summary>
public sealed record RepositoryContainer(string Id, string Name);

/// <summary>Who a permission is for: <c>user</c> or <c>group</c> with an email, <c>domain</c> with a domain, or <c>anyone</c>.</summary>
public sealed record RepositoryPrincipal(string Type, string? Email, string? Domain);

/// <summary>A permission in canonical form; <paramref name="Role"/> is <c>read</c>, <c>write</c> or <c>owner</c>. Mirrored permissions may only narrow access.</summary>
public sealed record RepositoryPermission(RepositoryPrincipal Principal, string Role);

/// <summary>
/// A file or folder of a document repository. <paramref name="Version"/> changes with each change of content or
/// metadata. <paramref name="Permissions"/> is filled only when one item is read.
/// </summary>
public sealed record RepositoryItem(
    string Id,
    string ContainerId,
    string? ParentId,
    string Name,
    bool Folder,
    string? MediaType,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Version,
    string? WebLink,
    IReadOnlyList<RepositoryPermission>? Permissions = null);

public sealed record RepositoryPage(IReadOnlyList<RepositoryItem> Items, string? NextCursor);

/// <summary>A change since a cursor: the item as it is now, or <paramref name="Removed"/> when it was deleted, trashed or moved out of the container.</summary>
public sealed record RepositoryChange(string ItemId, bool Removed, RepositoryItem? Item);

/// <summary>Changes since the cursor and the cursor for the next read; <paramref name="More"/> means another page is waiting.</summary>
public sealed record RepositoryChanges(IReadOnlyList<RepositoryChange> Changes, string Cursor, bool More);

/// <summary>The content of an item opened for a user. Disposing it closes the connection to the repository.</summary>
public sealed class RepositoryContent(Stream content, string mediaType, string name, long? length, IDisposable owner) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public string MediaType { get; } = mediaType;
    public string Name { get; } = name;
    public long? Length { get; } = length;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        owner.Dispose();
    }
}

public sealed class RepositoryUnavailableException(string reason) : Exception(reason)
{
    /// <summary>Short reason for degraded states (spec 011 FR-031), for example <c>unreachable</c> or <c>consent_missing</c>.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The document repository contract of ADR-0016 (spec 017 FR-019, INT-004): the extension point through which
/// <c>Documents</c> reads document systems without their types. Listing, reading and changes run with the connection's
/// own access and only inside the containers the connection allows; opening content runs as the user, so the
/// repository's own permissions decide. Throws <see cref="RepositoryUnavailableException"/> when the repository cannot be read.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>The containers the connection allows, or an empty list when the connection is not usable for documents.</summary>
    Task<IReadOnlyList<RepositoryContainer>> ContainersAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>One page of a folder's items; the container's root when <paramref name="folderId"/> is null.</summary>
    Task<RepositoryPage> ListAsync(Guid connectionId, string containerId, string? folderId, string? cursor, CancellationToken ct = default);

    /// <summary>One item with its permissions; null when it is not in an allowed container or does not exist.</summary>
    Task<RepositoryItem?> GetAsync(Guid connectionId, string itemId, CancellationToken ct = default);

    /// <summary>Changes in a container since <paramref name="cursor"/>; with no cursor, no changes and the cursor of now.</summary>
    Task<RepositoryChanges> ChangesAsync(Guid connectionId, string containerId, string? cursor, CancellationToken ct = default);

    /// <summary>The content as <paramref name="userEmail"/> may read it; null when that user may not, or the item is not in an allowed container.</summary>
    Task<RepositoryContent?> OpenAsync(Guid connectionId, string itemId, string userEmail, CancellationToken ct = default);
}

/// <summary>
/// A KPI reading in canonical form (spec 017 FR-022): the metric, its value and unit, the period it covers, the
/// location code it is for (null for the whole organization) and when the source system measured it.
/// </summary>
public sealed record KpiReading(
    Guid ConnectionId,
    string Feed,
    string Metric,
    decimal Value,
    string Unit,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string? Location,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceivedAt);

public sealed record KpiQuery(string? Metric, string? Location, DateTimeOffset? From, DateTimeOffset? To, int Limit = 100);

/// <summary>
/// The KPI feed contract (spec 017 FR-022) for location dashboards (R3) and analytics. Readings arrive through signed
/// inbound webhooks whose target is <c>kpi</c>; the latest source time wins for the same metric, location and period.
/// </summary>
public interface IKpiFeed
{
    /// <summary>Readings of the current tenant, newest period first. <see cref="KpiQuery.From"/> and <see cref="KpiQuery.To"/> bound the period start.</summary>
    Task<IReadOnlyList<KpiReading>> ReadAsync(KpiQuery query, CancellationToken ct = default);
}

/// <summary>A reading was stored or replaced by a newer measurement.</summary>
public sealed record KpiReadingReceived(Guid ConnectionId, string Feed, string Metric, string? Location, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd)
    : IIntegrationEvent;

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
    string WebLink,
    string? ICalUId = null,
    string Source = CalendarSources.Outlook);

/// <summary>The meeting sources behind the calendar ports (spec 037 FR-011).</summary>
public static class CalendarSources
{
    public const string Outlook = "outlook";
    public const string Google = "google";
}

/// <summary>What one meeting source answered for the signed-in employee: its state (M365States values), data age and occurrences.</summary>
public sealed record MeetingSourceResult(string Source, string State, DateTimeOffset? AsOf, IReadOnlyList<CalendarOccurrence> Occurrences);

/// <summary>
/// Every meeting source the tenant has for the signed-in employee (spec 037 FR-011, FR-012): Outlook through
/// <see cref="ICalendar"/> while <c>integration.microsoft365</c> is on, and Google Calendar while the Google Workspace
/// connection has its calendar feature on. A source the tenant does not have is left out; a failing source comes back
/// with its state and never throws.
/// </summary>
public interface IMeetingCalendars
{
    Task<IReadOnlyList<MeetingSourceResult>> ReadAsync(CalendarQuery query, CancellationToken ct = default);
}

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

/// <summary>
/// A field a connector action takes (spec 017 FR-008): <paramref name="Type"/> is <c>string</c>, <c>number</c>,
/// <c>integer</c> or <c>boolean</c>.
/// </summary>
public sealed record ConnectorInput(string Name, string Type, bool Required);

/// <summary>
/// A connector action as a route designer or the engine sees it (spec 017 FR-008, FR-009): what it does, whether a
/// retry is safe, its business errors and its inputs. <paramref name="Inputs"/> is null when the action declares none,
/// and such an action cannot be a step.
/// </summary>
public sealed record ConnectorActionInfo(
    Guid ConnectionId,
    string ConnectionName,
    string Kind,
    string Action,
    string SideEffect,
    bool Idempotent,
    IReadOnlyList<string> BusinessErrors,
    IReadOnlyList<ConnectorInput>? Inputs,
    bool Enabled);

/// <summary>The connector actions of the tenant's connections, for route steps (specs 016, 024). Tenant comes from context; no secrets.</summary>
public interface IConnectorActions
{
    Task<IReadOnlyList<ConnectorActionInfo>> ListAsync(CancellationToken ct = default);

    Task<ConnectorActionInfo?> FindAsync(Guid connectionId, string action, CancellationToken ct = default);
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

/// <summary>What happened to a Slack message (spec 017 US8). <c>UserGone</c>: the person left the workspace and the link was dropped.</summary>
public enum SlackSendOutcome
{
    Sent,
    Transient,
    UserGone,
    NotLinked,
    Unavailable,
    Failed,
}

/// <summary>Whether Slack can be used in this tenant now, and whether the person has linked their Slack account.</summary>
public sealed record SlackStatus(bool Available, bool Linked, DateTimeOffset? LinkedAt);

/// <summary>A public Slack channel a link can be shared to.</summary>
public sealed record SlackChannel(string Id, string Name, bool Member);

/// <summary>
/// Slack for product modules (spec 017 US8, spec 015 FR-019): notifications as direct messages to people who linked
/// their account, and links shared to a channel. Tenant comes from context; the enabled Slack connection of the tenant
/// is used. Message text is plain: Slack markup in it is escaped. Links are paths on the tenant's own host, such as
/// /content/read/{id}; the absolute address is made here.
/// </summary>
public interface ISlackMessaging
{
    Task<SlackStatus> StatusAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Links the person by their work email in the Slack workspace. Throws a validation error when Slack has no such member.</summary>
    Task<SlackStatus> LinkAsync(Guid userId, string email, CancellationToken ct = default);

    Task UnlinkAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Sends a direct message. <paramref name="key"/> is the idempotency key: a key that already succeeded is not sent again.</summary>
    Task<SlackSendOutcome> SendDirectAsync(Guid userId, string text, string? link, string key, CancellationToken ct = default);

    Task<IReadOnlyList<SlackChannel>> ChannelsAsync(CancellationToken ct = default);

    Task<SlackSendOutcome> ShareAsync(string channelId, string text, string link, string key, CancellationToken ct = default);
}

/// <summary>Directions of a clock punch in the time and attendance contract.</summary>
public static class ClockDirections
{
    public const string In = "in";
    public const string Out = "out";

    /// <summary>The source does not say; spec 022 pairs punches by time.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// One canonical clock event (spec 017 US7, the time and attendance contract): who punched, when, which way, and the
/// source's own punch ID, which makes an import idempotent per punch (spec 022 edge cases). <paramref name="Sequence"/>
/// orders every punch of the tenant, so a consumer reads on from the last sequence it saw.
/// </summary>
public sealed record ClockPunch(long Sequence, Guid ConnectionId, string PunchId, Guid UserId, DateTimeOffset At, string Direction, string? Device);

/// <summary>Clock data imported from time systems, for spec 022 attendance.</summary>
public interface IClockData
{
    /// <summary>Punches after <paramref name="afterSequence"/>, oldest first, at most <paramref name="limit"/> (1 to 5000).</summary>
    Task<IReadOnlyList<ClockPunch>> ReadAsync(long afterSequence, int limit, CancellationToken ct = default);
}

/// <summary>
/// A clock data file was imported (spec 017 US7 scenario 1). New punches are read through <see cref="IClockData"/>;
/// <paramref name="From"/> and <paramref name="To"/> are the earliest and latest punch time in the file.
/// </summary>
public sealed record ClockDataImported(Guid ConnectionId, Guid FileId, int Imported, int Duplicates, int Unmatched, DateTimeOffset? From, DateTimeOffset? To)
    : IIntegrationEvent;

/// <summary>Payment statuses of the finance hand-off contract, mapped from the finance system's own words.</summary>
public static class FinancePaymentStatuses
{
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string Paid = "paid";
    public const string Rejected = "rejected";

    public static readonly IReadOnlyList<string> All = [Submitted, Approved, Paid, Rejected];
}

/// <summary>Where a hand-off stands: waiting to be sent, sent (with a reference), failed, or unknown after a timeout.</summary>
public static class FinanceHandOffStates
{
    public const string Unavailable = "unavailable";
    public const string Queued = "queued";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
}

/// <summary>One line of a claim in the finance hand-off contract. Amounts are in the claim currency; the original is kept when it differed.</summary>
public sealed record FinanceLine(string Category, DateOnly Date, decimal Amount, string? Description = null, decimal? OriginalAmount = null, string? OriginalCurrency = null);

/// <summary>
/// The canonical document handed to finance (spec 017 US7 scenario 2, spec 022 FR-025): an approved claim of one
/// person. <paramref name="SourceType"/> and <paramref name="SourceId"/> name the claim in the calling module; a claim is
/// handed off once.
/// </summary>
public sealed record FinanceHandOffRequest(
    string SourceType,
    Guid SourceId,
    string EmployeeNumber,
    string Currency,
    decimal Amount,
    DateOnly ClaimDate,
    string? Description,
    string? CostCentre,
    string? Project,
    IReadOnlyList<FinanceLine> Lines);

/// <summary>A hand-off as the calling module shows it: its state, the finance system's reference and the payment status.</summary>
public sealed record FinanceHandOffView(
    string SourceType,
    Guid SourceId,
    string State,
    string? ExternalReference,
    string? PaymentStatus,
    string? ErrorClass,
    DateTimeOffset? SentAt,
    DateTimeOffset? CheckedAt);

/// <summary>
/// Hands approved claims to the finance or ERP system (spec 017 FR-021) through the tenant's finance connection. Sending
/// is a durable connector job, so an approval never waits on finance; the payment status is then checked until it is
/// paid or rejected. <see cref="FinanceHandOffChanged"/> tells the caller of every change.
/// </summary>
public interface IFinanceHandOff
{
    Task<FinanceHandOffView> SubmitAsync(FinanceHandOffRequest request, CancellationToken ct = default);

    Task<FinanceHandOffView?> GetAsync(string sourceType, Guid sourceId, CancellationToken ct = default);
}

public sealed record FinanceHandOffChanged(string SourceType, Guid SourceId, string State, string? ExternalReference, string? PaymentStatus) : IIntegrationEvent;

/// <summary>
/// The canonical HR employee (spec 019 section 2). <paramref name="RecordId"/> is the HR system's own ID of the person;
/// <paramref name="Status"/> is one of <see cref="HrEmployeeStatuses"/>. Names are kept in both languages when HR has them.
/// <paramref name="AssignmentDate"/> is the date the current org unit, manager, title and location take effect (FR-005).
/// </summary>
public sealed record HrEmployee(
    string RecordId,
    string? EmployeeNumber,
    string? WorkEmail,
    string? NameEn,
    string? NameAr,
    string Status,
    DateOnly? HireDate,
    DateOnly? TerminationDate,
    string? JobTitle,
    string? Grade,
    string? EmploymentType,
    string? CostCentre,
    string? Location,
    string? OrgUnitCode,
    string? ManagerEmployeeNumber,
    DateOnly? AssignmentDate = null,
    string? PersonalEmail = null);

public static class HrEmployeeStatuses
{
    public const string Active = "active";
    public const string Terminated = "terminated";

    public static readonly IReadOnlyList<string> All = [Active, Terminated];
}

/// <summary>The canonical HR fields a connection maps (spec 019 section 2), as their names in mappings.</summary>
public static class HrFields
{
    public const string RecordId = "record_id";
    public const string EmployeeNumber = "employee_number";
    public const string WorkEmail = "work_email";
    public const string NameEn = "name_en";
    public const string NameAr = "name_ar";
    public const string Status = "status";
    public const string HireDate = "hire_date";
    public const string TerminationDate = "termination_date";
    public const string JobTitle = "job_title";
    public const string Grade = "grade";
    public const string EmploymentType = "employment_type";
    public const string CostCentre = "cost_centre";
    public const string Location = "location";
    public const string OrgUnitCode = "org_unit_code";
    public const string ManagerEmployeeNumber = "manager_employee_number";
    public const string AssignmentDate = "assignment_date";

    /// <summary>
    /// The joiner's own email outside the tenant, used only to send pre-boarding before day one (spec 022 FR-016). It
    /// never leaves this module in <see cref="HrRecordView"/>; <see cref="IHrRecords.PreHireAsync"/> is its one reader.
    /// </summary>
    public const string PersonalEmail = "personal_email";

    public static readonly IReadOnlyList<string> All =
    [
        RecordId, EmployeeNumber, WorkEmail, NameEn, NameAr, Status, HireDate, TerminationDate, JobTitle, Grade, EmploymentType, CostCentre, Location,
        OrgUnitCode, ManagerEmployeeNumber, AssignmentDate, PersonalEmail,
    ];

    /// <summary>Fields kept out of <see cref="HrRecordView"/>: other modules never see them with a person's record.</summary>
    public static readonly IReadOnlyList<string> Private = [PersonalEmail];

    /// <summary>The fields of one assignment: they change together on the assignment date (US3 scenario 1).</summary>
    public static readonly IReadOnlyList<string> Assignment = [OrgUnitCode, ManagerEmployeeNumber, JobTitle, Location];

    /// <summary>A mapping must name these; everything else is optional.</summary>
    public static readonly IReadOnlyList<string> Required = [RecordId, Status];
}

/// <summary>
/// A matched person's HR record as other modules see it: the mapped fields only, by canonical name.
/// <paramref name="SeenAt"/> is when HR last confirmed the record, by a sync or a live read.
/// </summary>
public sealed record HrRecordView(Guid UserId, string RecordId, IReadOnlyDictionary<string, string> Fields, DateTimeOffset? SeenAt = null);

/// <summary>
/// A live read of one person's HR record (spec 019 US5 scenario 3). <see cref="State"/> is <c>ok</c>,
/// <c>not_matched</c> (no HR record matches the person) or <c>unavailable</c> (the HR system did not answer, or its
/// connection cannot read one record). <see cref="Record"/> is set only when the state is ok.
/// </summary>
public sealed record HrRecordRead(string State, HrRecordView? Record)
{
    public const string Ok = "ok";
    public const string NotMatched = "not_matched";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// HR records of matched people (spec 019 FR-001, FR-003) for the modules that own what HR feeds: profiles (spec 010)
/// read the HR fields here after each applied sync. Only the canonical contract is visible; HR system DTOs stay in the
/// packs. Without the <c>hr.integration</c> capability nothing is returned.
/// </summary>
public interface IHrRecords
{
    /// <summary>Matched records, all of them or those of the given people.</summary>
    Task<IReadOnlyList<HrRecordView>> MatchedAsync(IReadOnlyCollection<Guid>? userIds = null, CancellationToken ct = default);

    /// <summary>The canonical fields the tenant's HR connection maps, so their owners can mark them as from HR.</summary>
    Task<IReadOnlyList<string>> MappedFieldsAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads one matched person's record from the HR system now, for a value that must be current. The stored copy is
    /// not changed; the next sync does that.
    /// </summary>
    Task<HrRecordRead> CurrentAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// A hire HR knows before they have an account (spec 022 FR-016): the stored record, when it is active, matched to
    /// no account and has a personal email. Null otherwise, or without the <c>hr.integration</c> capability.
    /// </summary>
    Task<HrPreHire?> PreHireAsync(string recordId, CancellationToken ct = default);
}

/// <summary>
/// A joiner without an account yet: the personal email from HR, the start date and the hierarchy unit their HR org
/// code maps to (null when the code is not mapped). Used only to send pre-boarding.
/// </summary>
public sealed record HrPreHire(string RecordId, string PersonalEmail, DateOnly? HireDate, Guid? OrgUnitId);

/// <summary>Who keeps leave balances (spec 019 FR-013): TeamBeats' own ledger, or the HR system.</summary>
public static class HrLeaveSources
{
    public const string TeamBeats = "teambeats";
    public const string Hr = "hr";

    public static readonly IReadOnlyList<string> All = [TeamBeats, Hr];
}

/// <summary>
/// The leave balance source setting (spec 019 US7, FR-013), versioned. <paramref name="AllowStaleSubmit"/> lets people
/// send a request on the last known balance when HR cannot be read (US7 scenario 2). <paramref name="FromHr"/> is true
/// only when HR is chosen and the plan includes <c>hr.integration</c>.
/// </summary>
public sealed record HrLeaveSetting(string Source, bool AllowStaleSubmit, int Version, bool FromHr);

public sealed record HrLeaveBalance(string TypeKey, decimal Balance);

/// <summary>
/// One person's balances as HR reports them. <see cref="State"/> is <c>ok</c> (read now), <c>stale</c> (HR did not
/// answer; <see cref="Balances"/> are the last known, read at <see cref="AsOf"/>), <c>not_matched</c> or
/// <c>unavailable</c> (nothing known).
/// </summary>
public sealed record HrLeaveBalances(string State, IReadOnlyList<HrLeaveBalance> Balances, DateTimeOffset? AsOf)
{
    public const string Ok = "ok";
    public const string Stale = "stale";
    public const string NotMatched = "not_matched";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Leave handed to HR (spec 022 FR-006, spec 019 US7 scenario 1): an approved request, a cancellation, or a change of
/// dates or days. <paramref name="Kind"/> is <c>approved</c>, <c>cancelled</c> or <c>changed</c>.
/// </summary>
public sealed record HrLeaveHandOffRequest(Guid RequestId, Guid UserId, string Kind, string TypeKey, DateOnly Start, DateOnly End, bool StartHalf, bool EndHalf,
    decimal Days)
{
    public const string Approved = "approved";
    public const string Cancelled = "cancelled";
    public const string Changed = "changed";
}

/// <summary>States of a leave hand-off: queued (sent or waiting to retry), recorded (HR confirmed), failed, unavailable (no usable HR connection).</summary>
public static class HrLeaveHandOffStates
{
    public const string Queued = "queued";
    public const string Recorded = "recorded";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// HR as the leave balance source (spec 019 US7, spec 022 FR-006). Balances are read live from the HR connection, with
/// the last known kept for when HR does not answer. Hand-offs are durable connector jobs keyed by request, kind and
/// change, retried hourly when HR is unavailable, so an approval never waits on HR. Tenant comes from context.
/// </summary>
public interface IHrLeave
{
    Task<HrLeaveSetting> SettingAsync(CancellationToken ct = default);

    Task<HrLeaveBalances> BalancesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Hands one change to HR and returns its state; the same request, kind and change number is sent once.</summary>
    Task<string> HandOffAsync(HrLeaveHandOffRequest request, int change, CancellationToken ct = default);
}

/// <summary>A leave hand-off changed state (queued, recorded, failed). IDs and the HR reference only.</summary>
public sealed record HrLeaveHandOffChanged(Guid RequestId, string Kind, string State, string? ExternalReference, int Change = 0) : IIntegrationEvent;

/// <summary>An HR sync applied its changes (spec 019 section 12). Counts only, never HR values.</summary>
public sealed record HrSyncCompleted(Guid ConnectionId, Guid RunId, int Matched, int Changed) : IIntegrationEvent;

/// <summary>An HR sync stopped before applying because it passed the change threshold (FR-004).</summary>
public sealed record HrSyncHeld(Guid ConnectionId, Guid RunId, int Terminations, int Moves) : IIntegrationEvent;

/// <summary>
/// Lifecycle events (spec 019 FR-006), consumed by organization (002), identity (003), profiles (010) and leave and
/// journeys (022). Each comes from one HR change, traceable to its run and HR record (FR-018). IDs and dates only.
/// <paramref name="UserId"/> is null while the HR record matches nobody yet (US2 scenario 2).
/// </summary>
public sealed record EmployeeHired(Guid? UserId, string RecordId, DateOnly StartDate, Guid RunId, Guid ChangeId) : IIntegrationEvent;

/// <summary>The person moved to another org unit on <paramref name="EffectiveDate"/> (US3).</summary>
public sealed record EmployeeTransferred(Guid UserId, string RecordId, Guid? FromUnitId, Guid ToUnitId, DateOnly EffectiveDate, Guid RunId, Guid ChangeId)
    : IIntegrationEvent;

/// <summary>The person's manager changed on <paramref name="EffectiveDate"/>; the manager is null when HR's manager matches nobody.</summary>
public sealed record EmployeeManagerChanged(Guid UserId, string RecordId, Guid? ManagerUserId, DateOnly EffectiveDate, Guid RunId, Guid ChangeId)
    : IIntegrationEvent;

/// <summary>
/// HR recorded a termination (US4 scenario 1): published at once, so offboarding can start. Access ends with
/// <see cref="EmployeeLeft"/> at the end of <paramref name="LastWorkingDay"/>, or within minutes when <paramref name="Immediate"/>.
/// </summary>
public sealed record EmployeeTerminated(Guid? UserId, string RecordId, DateOnly LastWorkingDay, bool Immediate, Guid RunId, Guid ChangeId) : IIntegrationEvent;

/// <summary>The last working day of a terminated employee has ended (FR-008): their TeamBeats access ends now.</summary>
public sealed record EmployeeLeft(Guid UserId, string RecordId, DateOnly LastWorkingDay, Guid ChangeId) : IIntegrationEvent;

/// <summary>A former employee is active again (US4 scenario 3). <paramref name="Reactivate"/> is the tenant's choice to reopen the old account.</summary>
public sealed record EmployeeRehired(Guid? UserId, string RecordId, DateOnly StartDate, bool Reactivate, Guid RunId, Guid ChangeId) : IIntegrationEvent;
