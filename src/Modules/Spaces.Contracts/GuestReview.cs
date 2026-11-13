namespace TeamBeats.Modules.Spaces.Contracts;

/// <summary>One guest's membership of one active space, with the person who invited them (the sponsor).</summary>
public sealed record GuestMembership(Guid SpaceId, string SpaceName, Guid UserId, Guid? SponsorId, DateTimeOffset? ExpiresAt);

/// <summary>
/// Guest memberships for access reviews (spec 026 FR-012): the Security module lists them and removes the ones a
/// sponsor does not confirm. Removal works like the end of access: the guest leaves the space, the owners are told,
/// and a guest left in no space is disabled and signed out.
/// </summary>
public interface ISpaceGuestReview
{
    Task<IReadOnlyList<GuestMembership>> ActiveGuestsAsync(CancellationToken ct = default);

    /// <summary>Removes the guest from the space; false when they were no longer in it.</summary>
    Task<bool> RemoveGuestAsync(Guid spaceId, Guid userId, string reason, CancellationToken ct = default);

    /// <summary>The person who invited the guest into the space (spec 026 US5 scenario 3), or null when unknown.</summary>
    Task<Guid?> SponsorOfAsync(Guid spaceId, Guid userId, CancellationToken ct = default);

    /// <summary>Whether the space has a guest or an open guest invitation (spec 026 US6 scenario 2: a guest space).</summary>
    Task<bool> HasGuestsAsync(Guid spaceId, CancellationToken ct = default);
}
