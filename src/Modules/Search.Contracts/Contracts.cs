using TeamBeats.Primitives;

namespace TeamBeats.Modules.Search.Contracts;

/// <summary>
/// What a result is (spec 012 section 2). Owners name the kind of each unit; the results page filters and labels by it.
/// The R2 sources (communities, courses, project spaces) add their kinds here.
/// </summary>
public static class SearchKinds
{
    public const string Person = "person";
    public const string Content = "content";
    public const string Document = "document";
    public const string Knowledge = "knowledge";
    public const string Policy = "policy";
    public const string Service = "service";

    public static readonly IReadOnlyList<string> All = [Person, Content, Document, Knowledge, Policy, Service];
}

/// <summary>
/// One searchable unit of an item as its owner gives it: a section of an article, one FAQ pair, one policy clause, a
/// document's text, a service or a person's card. <paramref name="Key"/> tells units of one item apart and stays the same
/// across versions where it can (an anchor); <paramref name="Link"/> is the path that opens the item at the unit, which is
/// also the citation anchor for the assistant. <paramref name="Classification"/> is one of public, internal, confidential
/// or restricted and decides whether the unit may be embedded and given to AI (spec 012 section 11). Search splits long
/// text into passages itself.
/// </summary>
public sealed record SearchUnit(
    string Key,
    string Kind,
    string Locale,
    string Title,
    string Text,
    string Link,
    string Classification,
    Guid? OrgUnitId = null,
    int? Version = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>
/// A module that owns searchable items (spec 012 plan, indexing and permission filtering). Search never reads the
/// owner's tables: it asks the owner for an item's units when the item changes and, at query time, which candidate
/// items the signed-in person may read now, so the index can never show what the owner would not.
/// </summary>
public interface ISearchSource
{
    /// <summary>The source name, stored with every unit (for example <c>content</c>). Stable once data exists.</summary>
    string Source { get; }

    /// <summary>
    /// The units readers get now, read without a user (indexing runs as the system). Empty when the item is gone, not
    /// published, superseded or excluded from search by its owner (FR-011, FR-021), which removes it from the index.
    /// </summary>
    Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>A page of the IDs of every item that may have units, in ID order after <paramref name="after"/>, for rebuilds (FR-012).</summary>
    Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default);

    /// <summary>Which of the items the signed-in person may read now (FR-002). Decided by the owner with the central authorization service.</summary>
    Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}

/// <summary>
/// Asks Search to read an item again from its owner (spec 012 section 12). Owners whose events do not name the item
/// (or that have no event yet) publish this; Search also maps the owner events of section 12 to it.
/// </summary>
public sealed record SearchItemChanged(string Source, Guid ItemId) : IIntegrationEvent;

/// <summary>Asks Search to rebuild the tenant's index from every owner (FR-012), for a new tenant, a repair or a configuration change.</summary>
public sealed record SearchRebuildRequested(Guid RebuildId) : IIntegrationEvent;

/// <summary>
/// A retrieval for a model (spec 012 US3, FR-007). <paramref name="UseCase"/> is the AI use case the passages are for
/// (spec 013, for example <c>assistant</c>); its classification ceiling decides what may be returned. <paramref name="TopK"/>
/// defaults to <see cref="ISearchRetrieval.DefaultTopK"/>; <paramref name="Kinds"/> narrows the sources. <paramref name="Source"/>
/// and <paramref name="ItemId"/>, given together, keep the retrieval to one item, ranked by the query, with no cap per item
/// (spec 025 FR-011, a policy-scoped question).
/// </summary>
public sealed record RetrievalRequest(string Query, string UseCase, int? TopK = null, IReadOnlyList<string>? Kinds = null, string? Source = null, Guid? ItemId = null);

/// <summary>
/// One passage the person may read, with what a citation needs: <paramref name="Anchor"/> is the link that opens the
/// item at the passage. The text is untrusted data written by people (section 14), never instructions for the model.
/// </summary>
public sealed record RetrievedPassage(
    string Source,
    Guid ItemId,
    string Kind,
    string Title,
    string Text,
    string Anchor,
    string Locale,
    string Classification,
    int? Version,
    DateTimeOffset? UpdatedAt,
    double Score);

/// <summary>
/// The passages for a retrieval, best first. <paramref name="Withheld"/> counts passages the person could read that
/// the AI policy keeps from this use case by classification, so the assistant can say evidence was withheld by policy
/// (US3 scenario 2). <paramref name="Degraded"/> is true when semantic retrieval should have run and could not.
/// </summary>
public sealed record RetrievalResult(IReadOnlyList<RetrievedPassage> Passages, int Withheld, bool Degraded);

/// <summary>
/// The retrieval contract for the assistant and agent tools (spec 012 US3, plan API): top passages filtered as the
/// signed-in person by each owner and by the AI classification policy of the use case. Specs 014, 020 and 027 call it.
/// </summary>
public interface ISearchRetrieval
{
    const int DefaultTopK = 8;
    const int MaxTopK = 20;

    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct = default);

    /// <summary>
    /// The passages of one item, in order, for work on that item only (spec 014 US3: summarize, explain, translate).
    /// Empty when the item is not in the index or the person may not read it; passages above the use case's
    /// classification ceiling are withheld and counted. <paramref name="locale"/> picks the language version when the
    /// item has several, otherwise the first one indexed.
    /// </summary>
    Task<RetrievalResult> ItemAsync(string source, Guid itemId, string useCase, string? locale = null, CancellationToken ct = default);
}
