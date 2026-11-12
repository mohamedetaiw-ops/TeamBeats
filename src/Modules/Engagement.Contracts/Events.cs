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
