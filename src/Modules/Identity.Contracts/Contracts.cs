using TeamBeats.Primitives;

namespace TeamBeats.Modules.Identity.Contracts;

public sealed record UserInfo(Guid Id, string DisplayName, string Email, string Status, string? PreferredLocale, DateTimeOffset? LastSignInAt);

public interface IUserDirectory
{
    Task<UserInfo?> GetAsync(Guid userId, CancellationToken ct = default);

    Task<UserInfo?> FindByEmailAsync(string email, CancellationToken ct = default);

    Task<IReadOnlyList<UserInfo>> ListAsync(string? search, int limit, CancellationToken ct = default);

    /// <summary>True while the directory has the account disabled; product admins cannot re-enable it (spec 003 FR-013).</summary>
    Task<bool> IsDisabledByDirectoryAsync(Guid userId, CancellationToken ct = default);
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
