using TeamBeats.Primitives;

namespace TeamBeats.Modules.Content.Contracts;

/// <summary>A version of a content item went live (spec 009 section 12). IDs and facts only; search reads the item.</summary>
public sealed record ContentPublished(Guid ItemId, Guid SiteId, string Type, int Version) : IIntegrationEvent;

/// <summary>A published item was taken down by a publisher.</summary>
public sealed record ContentUnpublished(Guid ItemId, Guid SiteId) : IIntegrationEvent;

/// <summary>A published item reached its expiry date.</summary>
public sealed record ContentExpired(Guid ItemId, Guid SiteId) : IIntegrationEvent;

/// <summary>A person confirmed a critical announcement (FR-007). IDs only.</summary>
public sealed record ContentAcknowledged(Guid ItemId, int Version, Guid UserId, string Channel) : IIntegrationEvent;

/// <summary>A completion report was asked for; the worker writes the file.</summary>
public sealed record AckReportRequested(Guid ReportId, Guid ItemId) : IIntegrationEvent;

/// <summary>An item's review window opened and its owner got a review task (US3 scenario 2). IDs only.</summary>
public sealed record ContentReviewDue(Guid ItemId, Guid SiteId, Guid OwnerId, DateTimeOffset DueAt) : IIntegrationEvent;
