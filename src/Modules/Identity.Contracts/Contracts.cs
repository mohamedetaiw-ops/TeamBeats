using TeamBeats.Primitives;

namespace TeamBeats.Modules.Identity.Contracts;

/// <param name="IsTest">A synthetic test identity of an evaluation persona (spec 034 FR-006): never signs in, is never listed and is never notified.</param>
public sealed record UserInfo(Guid Id, string DisplayName, string Email, string Status, string? PreferredLocale, DateTimeOffset? LastSignInAt, bool IsTest = false);

public interface IUserDirectory
{
    Task<UserInfo?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Many users in one read; unknown IDs are left out.</summary>
    async Task<IReadOnlyDictionary<Guid, UserInfo>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var found = new Dictionary<Guid, UserInfo>();
        foreach (var id in userIds.Distinct())
        {
            if (await GetAsync(id, ct) is { } u)
            {
                found[id] = u;
            }
        }
        return found;
    }

    Task<UserInfo?> FindByEmailAsync(string email, CancellationToken ct = default);

    Task<IReadOnlyList<UserInfo>> ListAsync(string? search, int limit, CancellationToken ct = default);

    /// <summary>Every user of the tenant without sign-in times, for read models such as the people directory (spec 010).</summary>
    Task<IReadOnlyList<UserInfo>> ListAllAsync(CancellationToken ct = default);

    /// <summary>True while the directory has the account disabled; product admins cannot re-enable it (spec 003 FR-013).</summary>
    Task<bool> IsDisabledByDirectoryAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Synthetic test identities for evaluation personas (spec 034 FR-006). They have no sign-in identity, are left out of
/// every user list and search, cannot hold a session, and are skipped by notifications.
/// </summary>
public interface ITestIdentities
{
    Task<Guid> CreateAsync(string displayName, CancellationToken ct = default);
}

/// <summary>Where members signed in from (spec 026 lock-out preview). Addresses only, no other session data.</summary>
public interface ISignInHistory
{
    /// <summary>The source address of each session created since the given time, per user; sessions without one are left out.</summary>
    Task<IReadOnlyList<SignInAddress>> AddressesSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}

public sealed record SignInAddress(Guid UserId, string Address);

/// <summary>
/// Which ways in the tenant has (spec 026 FR-017 posture): an enabled single sign-on provider, and development sign-in.
/// MemberDomains are the email domains the provider turns into members on first sign-in: the tenant's own domains, whose
/// people are members and never guests (spec 026 edge case).
/// </summary>
public sealed record SignInMethods(bool SsoEnabled, bool DevSignInEnabled, IReadOnlyList<string>? MemberDomains = null);

public interface ISignInMethods
{
    Task<SignInMethods> CurrentAsync(CancellationToken ct = default);
}

public interface ISessionRevocation
{
    Task<int> RevokeUserSessionsAsync(Guid userId, string reason, CancellationToken ct = default);

    Task<int> RevokeTenantSessionsAsync(string reason, CancellationToken ct = default);
}

public sealed record UserSignedIn(Guid UserId, string Method) : IIntegrationEvent;
public sealed record UserProvisioned(Guid UserId, string Via) : IIntegrationEvent;
public sealed record UserDeprovisioned(Guid UserId, string Reason) : IIntegrationEvent;
public sealed record UserProfileSynced(Guid UserId, IReadOnlyList<string> ChangedFields) : IIntegrationEvent;
public sealed record SessionRevoked(Guid UserId, int Count, string Reason) : IIntegrationEvent;

/// <summary>One session ended by sign-out, so anything held for that session can be dropped (spec 003 FR-016).</summary>
public sealed record SessionEnded(Guid UserId, Guid SessionId) : IIntegrationEvent;

/// <summary>A synced, enabled user and the organization values the directory holds for them (spec 002 US5).</summary>
public sealed record DirectoryPerson(
    Guid UserId,
    string DirectoryId,
    string? Company,
    string? Department,
    string? Division,
    IReadOnlyList<string> AdminUnits,
    IReadOnlyList<string> Groups,
    DirectoryProfile? Profile = null);

/// <summary>
/// The profile values the directory holds for a person (spec 010 section 12). <see cref="ManagerDirectoryId"/> is the
/// manager's directory ID, which may name someone who is not a member of the tenant.
/// </summary>
public sealed record DirectoryProfile(
    string? DisplayName,
    string? Email,
    string? JobTitle,
    string? Department,
    string? Company,
    string? Office,
    string? WorkPhone,
    string? MobilePhone,
    string? PreferredLanguage,
    string? EmployeeId,
    DateOnly? HireDate,
    string? ManagerDirectoryId);

/// <summary>A directory group or administrative unit (<see cref="Kind"/> is <c>group</c> or <c>admin_unit</c>).</summary>
public sealed record DirectoryContainer(string Id, string Kind, string? DisplayName);

/// <summary>The current state of a directory connection's mirror. IDs are the directory's immutable IDs.</summary>
public sealed record DirectorySnapshot(Guid ConnectionId, IReadOnlyList<DirectoryPerson> People, IReadOnlyList<DirectoryContainer> Containers);

/// <summary>
/// Directory reads for the organization mapping (spec 002 plan) and the profile (spec 010). Tenancy never calls the
/// directory itself; it reads what directory sync (spec 003 US6) has mirrored. Tenant comes from context.
/// </summary>
public interface IDirectoryReader
{
    /// <summary>The snapshot for the tenant's directory connection, or null when directory sync is not set up.</summary>
    Task<DirectorySnapshot?> ReadAsync(CancellationToken ct = default);
}

/// <summary>Published after each successful directory sync run.</summary>
public sealed record DirectorySynced(Guid ConnectionId) : IIntegrationEvent;

/// <summary>A kiosk device was enrolled or revoked (spec 030 section 12).</summary>
public sealed record DeviceEnrolled(Guid DeviceId, Guid OrgUnitId) : IIntegrationEvent;

public sealed record DeviceRevoked(Guid DeviceId, Guid OrgUnitId) : IIntegrationEvent;
