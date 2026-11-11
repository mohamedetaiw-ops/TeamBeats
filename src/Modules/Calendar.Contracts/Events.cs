using TeamBeats.Primitives;

namespace TeamBeats.Modules.Calendar.Contracts;

/// <summary>A company event, department event or important date was published or changed (spec 037 section 12). IDs only.</summary>
public sealed record CalendarEventPublished(Guid EventId, Guid? OrgUnitId, string Kind, bool Changed) : IIntegrationEvent;

/// <summary>An event was cancelled or deleted.</summary>
public sealed record CalendarEventCancelled(Guid EventId, Guid? OrgUnitId, bool Deleted) : IIntegrationEvent;

/// <summary>A person answered an event with RSVP on.</summary>
public sealed record CalendarEventResponded(Guid EventId, string OccurrenceKey, Guid UserId, string Answer) : IIntegrationEvent;
