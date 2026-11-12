using TeamBeats.Primitives;

namespace TeamBeats.Modules.Spaces.Contracts;

/// <summary>A space was created (spec 029 section 12). Kind is project or team; visibility is public, private or secret.</summary>
public sealed record SpaceCreated(Guid SpaceId, string Kind, string Visibility) : IIntegrationEvent;

/// <summary>A space was archived; it is read-only until an owner or a spaces administrator restores it.</summary>
public sealed record SpaceArchived(Guid SpaceId) : IIntegrationEvent;

/// <summary>
/// Someone joined or left a space, or their role changed. Role is owner, member or viewer, or null when they left. The
/// space's library and discussion follow its members through this event (spec 029 FR-006).
/// </summary>
public sealed record SpaceMembershipChanged(Guid SpaceId, Guid UserId, string? Role) : IIntegrationEvent;

/// <summary>A task was given to someone (spec 029 section 12). DueOn is the task's due date, if it has one.</summary>
public sealed record TaskAssigned(Guid SpaceId, Guid TaskId, Guid UserId, DateOnly? DueOn) : IIntegrationEvent;

/// <summary>A task was completed, from its page, the board or My tasks.</summary>
public sealed record TaskCompleted(Guid SpaceId, Guid TaskId, Guid By) : IIntegrationEvent;

/// <summary>Every task linked to a milestone is done.</summary>
public sealed record MilestoneReached(Guid SpaceId, Guid MilestoneId) : IIntegrationEvent;

/// <summary>An owner set the space's status: on_track, at_risk, off_track or done.</summary>
public sealed record SpaceStatusChanged(Guid SpaceId, string Status) : IIntegrationEvent;
