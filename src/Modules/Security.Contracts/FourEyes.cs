using System.Text.Json;

namespace TeamBeats.Modules.Security.Contracts;

/// <summary>The kinds of critical tenant setting a second administrator approves when four-eyes is on (spec 026 FR-016).</summary>
public static class FourEyesKinds
{
    public const string SecurityPolicy = "security.policy";
    public const string FourEyes = "security.four_eyes";
    public const string IdentityProvider = "identity.provider";
    public const string AuditRetention = "audit.retention";
    public const string Keys = "security.keys";
}

/// <summary>A change held for a second administrator, as the owning endpoint answers it (202).</summary>
public sealed record PendingChangeView(
    Guid Id,
    string Kind,
    string Status,
    IReadOnlyList<string> Fields,
    Guid RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    Guid? DecidedBy = null,
    DateTimeOffset? DecidedAt = null,
    string? Reason = null,
    bool CanDecide = false,
    bool CanWithdraw = false);

/// <summary>
/// Four-eyes approval (spec 026 FR-016). An owning endpoint checks a request as it always does, then asks
/// <see cref="HoldAsync"/>: with four-eyes off, or while an approved change is being applied, it answers null and the
/// endpoint applies the change itself; otherwise the change is stored for another holder of the kind's permission and the
/// endpoint answers 202 with it.
/// </summary>
public interface IFourEyes
{
    Task<PendingChangeView?> HoldAsync(string kind, object request, IReadOnlyList<string> fields, CancellationToken ct = default);

    /// <summary>The change of a kind waiting for approval, if any, for the notice on its settings page.</summary>
    Task<PendingChangeView?> PendingAsync(string kind, CancellationToken ct = default);
}

/// <summary>
/// Applies an approved change of one kind (spec 026 FR-016). The owning module registers one per kind; it runs as the
/// approver through the same code path as a direct change, so validation, permission and audit are those of a direct change.
/// </summary>
public interface IFourEyesApplier
{
    string Kind { get; }

    /// <summary>The permission over the tenant that requesting and approving a change of this kind needs.</summary>
    string Permission { get; }

    Task ApplyAsync(JsonElement request, CancellationToken ct);
}

/// <summary>The tenant's data key, unwrapped with its customer-managed key (spec 026 US8). <see cref="Id"/> never changes for a tenant.</summary>
public sealed record TenantDataKey(Guid Id, byte[] Key);

/// <summary>
/// The tenant's data key for encrypting content at rest under the customer's key (spec 026 US8, FR-007). Null when the
/// tenant has no customer-managed key, so the caller keeps the platform key. The plaintext key is held in memory for at
/// most 15 minutes and is gone when the customer revokes their key.
/// </summary>
public interface ITenantDataKeys
{
    Task<TenantDataKey?> CurrentAsync(CancellationToken ct = default);

    /// <summary>The key with this id, or null when it cannot be had (another tenant's id, or the key is revoked).</summary>
    Task<TenantDataKey?> FindAsync(Guid id, CancellationToken ct = default);
}
