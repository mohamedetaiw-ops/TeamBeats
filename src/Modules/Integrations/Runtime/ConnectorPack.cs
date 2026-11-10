using System.Text.Json;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// A connector action as its pack declares it (spec 017 FR-008). <paramref name="Idempotent"/> means the external
/// system itself drops a repeated call with the same key, or the pack checks for the earlier result before acting, so a
/// retry after a timeout is safe. <paramref name="BusinessErrors"/> are the codes a route can branch on.
/// <paramref name="Inputs"/> are the fields a caller may map (FR-008, FR-009); the pack sends nothing else. Actions with
/// no declared inputs (a webhook delivery, for example) cannot be used as a route step or an
/// agent tool.
/// </summary>
public sealed record ConnectorActionDefinition(
    string Name,
    string SideEffect,
    bool Idempotent,
    IReadOnlyList<string> BusinessErrors,
    IReadOnlyList<ConnectorInput>? Inputs = null);

/// <summary>What a pack learns about the connection it calls. Secrets stay references; the pack reads them through the secret store.</summary>
public sealed record PackConnection(Guid TenantId, Guid Id, string Kind, int PackVersion, string? ExternalId, JsonElement Settings);

/// <summary>One attempt of an action. <paramref name="Attempt"/> counts from 1.</summary>
public sealed record PackCall(PackConnection Connection, string Action, JsonElement Input, string IdempotencyKey, int Attempt);

public enum PackResultKind
{
    /// <summary>Done; output and external reference are returned.</summary>
    Ok,

    /// <summary>A declared business error. Not retried.</summary>
    BusinessError,

    /// <summary>Timeout, 5xx, 429 or a broken answer. Retried when safe.</summary>
    Technical,

    /// <summary>The credential was refused or the request can never succeed. Not retried.</summary>
    Fatal,
}

/// <summary>
/// The answer of one attempt. For a technical error, <paramref name="MayHaveActed"/> is true when the request reached
/// the system (a timeout after sending, for example), so only an idempotent action may be retried.
/// </summary>
public sealed record PackCallResult(
    PackResultKind Kind,
    JsonElement? Output = null,
    string? ExternalReference = null,
    string? ErrorClass = null,
    string? ErrorCode = null,
    TimeSpan? RetryAfter = null,
    bool MayHaveActed = false,
    int? RecordCount = null)
{
    public static PackCallResult Ok(JsonElement? output = null, string? externalReference = null, int? recordCount = null) =>
        new(PackResultKind.Ok, output, externalReference, RecordCount: recordCount);

    public static PackCallResult Business(string code) => new(PackResultKind.BusinessError, ErrorClass: ConnectorErrorClasses.BusinessError, ErrorCode: code);

    public static PackCallResult Technical(string errorClass, bool mayHaveActed = false, TimeSpan? retryAfter = null) =>
        new(PackResultKind.Technical, ErrorClass: errorClass, MayHaveActed: mayHaveActed, RetryAfter: retryAfter);

    public static PackCallResult Fatal(string errorClass) => new(PackResultKind.Fatal, ErrorClass: errorClass);
}

/// <summary>
/// A connector pack (spec 017 plan): a connection kind, the capability key that puts it in a package, and its actions.
/// Packs translate between canonical input and the external system at the edge; no external DTO leaves the pack.
/// Packs never call models (FR-024). Outbound HTTP goes through <see cref="SafeHttp.ClientName"/>.
/// </summary>
public interface IConnectorPack
{
    string Kind { get; }

    int Version { get; }

    string CapabilityKey { get; }

    IReadOnlyList<ConnectorActionDefinition> Actions { get; }

    Task<PackCallResult> CallAsync(PackCall call, CancellationToken ct);

    /// <summary>The actions of one connection. Packs with fixed actions return <see cref="Actions"/>; custom REST reads the tenant's own.</summary>
    Task<IReadOnlyList<ConnectorActionDefinition>> ActionsForAsync(PackConnection connection, CancellationToken ct) => Task.FromResult(Actions);

    /// <summary>
    /// Checks connection settings when they are saved and returns them in stored form. Settings hold secret manager
    /// references, never secret values. Throws a validation error naming the field.
    /// </summary>
    Task<JsonElement> ValidateSettingsAsync(JsonElement settings, CancellationToken ct) => Task.FromResult(settings);

    /// <summary>
    /// Settings as stored after a change: <paramref name="next"/> is the validated new settings, <paramref name="previous"/>
    /// the stored ones. Packs that keep state across changes (a webhook secret in its rotation overlap) carry it here.
    /// </summary>
    JsonElement CarryOver(JsonElement previous, JsonElement next, DateTimeOffset now) => next;

    /// <summary>The credential expiry date the settings declare, if any (spec 017 US1 scenario 5).</summary>
    DateOnly? CredentialExpiry(JsonElement settings) => null;

    /// <summary>Test connection (FR-004): each check separately, with a plain result.</summary>
    Task<IReadOnlyList<PackCheck>> TestAsync(PackConnection connection, CancellationToken ct) => Task.FromResult<IReadOnlyList<PackCheck>>([]);
}

/// <summary>One check of Test connection: <paramref name="ResultKey"/> is a resource key such as <c>integrations.test.ok</c>.</summary>
public sealed record PackCheck(string Name, bool Ok, string ResultKey, string? ErrorClass = null);

/// <summary>Plain error classes shown in run history (spec 017 section 10), with the technical detail kept out.</summary>
public static class ConnectorErrorClasses
{
    public const string BusinessError = "business_error";
    public const string CredentialRefused = "credential_refused";
    public const string NotResponding = "not_responding";
    public const string Throttled = "throttled";
    public const string ServerError = "server_error";
    public const string InvalidResponse = "invalid_response";
    public const string BlockedAddress = "blocked_address";
    public const string HostNotAllowed = "host_not_allowed";
    public const string CircuitOpen = "circuit_open";
    public const string RateLimited = "rate_limited";
    public const string ConnectionDisabled = "connection_disabled";
    public const string ConnectionMissing = "connection_missing";
    public const string NotInPackage = "not_in_package";
    public const string UnknownAction = "unknown_action";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string Unexpected = "unexpected";
    public const string InvalidInput = "invalid_input";
    public const string CredentialMissing = "credential_missing";
    public const string RequestRefused = "request_refused";
}
