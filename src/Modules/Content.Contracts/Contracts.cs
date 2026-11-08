using TeamBeats.Primitives;

namespace TeamBeats.Modules.Content.Contracts;

/// <summary>A version of a content item went live (spec 009 section 12). IDs and facts only; search reads the item.</summary>
public sealed record ContentPublished(Guid ItemId, Guid SiteId, string Type, int Version) : IIntegrationEvent;

/// <summary>A published item was taken down by a publisher.</summary>
public sealed record ContentUnpublished(Guid ItemId, Guid SiteId) : IIntegrationEvent;

/// <summary>A published item reached its expiry date.</summary>
public sealed record ContentExpired(Guid ItemId, Guid SiteId) : IIntegrationEvent;
