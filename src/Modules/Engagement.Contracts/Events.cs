using TeamBeats.Primitives;

namespace TeamBeats.Modules.Engagement.Contracts;

/// <summary>A post was published in a community (spec 023 section 12). Search indexes it with the community's access rules (FR-023).</summary>
public sealed record PostPublished(Guid PostId, Guid CommunityId, Guid ChannelId) : IIntegrationEvent;

/// <summary>A post was removed, by its author or a moderator, or its community was archived. Search drops it (FR-020).</summary>
public sealed record PostRemoved(Guid PostId, Guid CommunityId) : IIntegrationEvent;

/// <summary>A comment or reply was added to a post.</summary>
public sealed record CommentAdded(Guid CommentId, Guid PostId, Guid CommunityId) : IIntegrationEvent;

/// <summary>A poll closed, at its end time or earlier by its author or a moderator (spec 023 section 12).</summary>
public sealed record PollClosed(Guid PollId, Guid CommunityId) : IIntegrationEvent;

/// <summary>A survey closed, at its end date or earlier by its author or a publisher (spec 023 section 12).</summary>
public sealed record SurveyClosed(Guid SurveyId, Guid OrgUnitId) : IIntegrationEvent;

/// <summary>Someone thanked people or a team (spec 023 US4). Recipients is empty for a team shout-out.</summary>
public sealed record RecognitionGiven(Guid RecognitionId, Guid GiverId, IReadOnlyList<Guid> Recipients, Guid? TeamUnitId) : IIntegrationEvent;

/// <summary>
/// The discussion of a project or team space (spec 029 FR-006): a closed community with one channel whose members are
/// the space's members, decided by the space roles on the space. Owners of the space moderate it. It is not listed in
/// the community directory and nobody joins or leaves it there. Spaces calls this after deciding the caller may.
/// </summary>
public interface ISpaceChannels
{
    /// <summary>Creates the space's community and returns its ID, or null when communities are not in the plan.</summary>
    Task<Guid?> CreateAsync(Guid spaceId, string name, string classification, CancellationToken ct = default);

    /// <summary>Follows the space's name and classification.</summary>
    Task UpdateAsync(Guid communityId, string name, string classification, CancellationToken ct = default);

    /// <summary>An archived space's discussion stays readable and takes no posts (FR-013).</summary>
    Task SetArchivedAsync(Guid communityId, bool archived, CancellationToken ct = default);
}
