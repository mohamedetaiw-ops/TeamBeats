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

/// <summary>An active site and the org unit it covers (null for the whole tenant).</summary>
public sealed record SiteScope(Guid Id, Guid? OrgUnitId);

/// <summary>Lets other modules place things on a site (spec 011 site libraries) without reading Content's tables.</summary>
public interface ISiteDirectory
{
    /// <summary>The active site of the current tenant, or null when there is none with this ID.</summary>
    Task<SiteScope?> FindAsync(Guid siteId, CancellationToken ct = default);
}

/// <summary>
/// A new knowledge article draft: the site (the default site when null), its titles per locale, its owner and, per
/// locale, the level 2 headings of the template to start with.
/// </summary>
public sealed record KnowledgeDraft(Guid? SiteId, IReadOnlyDictionary<string, string> Titles, Guid? OwnerId,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Headings, string? Classification = null);

/// <summary>A published knowledge article the caller may read.</summary>
public sealed record KnowledgeArticle(Guid ItemId, Guid SiteId, Guid? SiteOrgUnitId, IReadOnlyDictionary<string, string> Titles, Guid? OwnerId, int Version,
    DateTimeOffset PublishedAt, DateTimeOffset? ReviewDueAt);

/// <summary>One heading of a published article with the text under it, per locale, and the anchor the reader page uses.</summary>
public sealed record ContentSection(string Locale, string Anchor, int Level, string Heading, string Text);

/// <summary>
/// Knowledge articles are content items of type <c>knowledge</c> (spec 011 plan): the knowledge base of the
/// <c>Documents</c> module creates them, lists them and reads their sections through this contract, and they follow the
/// spec 009 draft, review, publish and version lifecycle. Every call is in the current tenant, as the current user.
/// </summary>
public interface IKnowledgeContent
{
    /// <summary>Creates the draft with the template headings in each title's locale. Needs authoring rights on the site and <c>kb.edit</c> there.</summary>
    Task<Guid> CreateDraftAsync(KnowledgeDraft draft, CancellationToken ct = default);

    /// <summary>The article when it is published and the caller may read it; otherwise null.</summary>
    Task<KnowledgeArticle?> ReadableAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>Published articles the caller may read, the most recent first.</summary>
    Task<IReadOnlyList<KnowledgeArticle>> ReadableListAsync(int limit, CancellationToken ct = default);

    /// <summary>Whether the caller owns the article or holds <c>kb.edit</c> on its site.</summary>
    Task<bool> MayManageAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>The sections of the published version in every locale, or empty when the caller may not read it.</summary>
    Task<IReadOnlyList<ContentSection>> SectionsAsync(Guid itemId, CancellationToken ct = default);
}
