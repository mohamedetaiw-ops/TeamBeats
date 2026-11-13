using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents.Contracts;

/// <summary>
/// A user downloaded a document file (spec 011, consumed by spec 026 download volume alerts). IDs and size only, never
/// names or content. The Documents module publishes it once spec 011 downloads exist.
/// </summary>
public sealed record DocumentDownloaded(Guid DocumentId, Guid LibraryId, Guid UserId, long Bytes) : IIntegrationEvent;

/// <summary>
/// A version of a document became the one readers get (spec 011 section 12), for search, subscriptions and analytics.
/// IDs and numbers only; consumers read the document through the module's contract with the reader's permissions.
/// </summary>
public sealed record DocumentVersionPublished(Guid DocumentId, Guid LibraryId, Guid VersionId, int Major, int Minor) : IIntegrationEvent;

/// <summary>A folder or document gained or lost its own permissions, so search re-checks what sits under it.</summary>
public sealed record DocumentPermissionsChanged(Guid LibraryId, string ResourceType, Guid ResourceId) : IIntegrationEvent;

/// <summary>A document went to the recycle bin (spec 011 FR-018), so search and subscriptions stop showing it.</summary>
public sealed record DocumentDeleted(Guid DocumentId, Guid LibraryId) : IIntegrationEvent;

/// <summary>A controlled document's owner withdrew it on review (spec 011 US3 scenario 5); readers no longer get it.</summary>
public sealed record DocumentWithdrawn(Guid DocumentId, Guid LibraryId) : IIntegrationEvent;

/// <summary>A reader confirmed reading a controlled document's version in force (spec 011 FR-008), for spec 025 attestation.</summary>
public sealed record DocumentReadConfirmed(Guid DocumentId, Guid VersionId, Guid UserId) : IIntegrationEvent;

/// <summary>
/// A reader marked a knowledge article helpful or not (spec 011 section 13, <c>kb.article_feedback</c>), for analytics
/// in spec 021. The article, the answer and whether a comment came with it; never the comment or the reader.
/// </summary>
public sealed record KnowledgeFeedbackGiven(Guid ArticleId, bool Helpful, bool CommentPresent) : IIntegrationEvent;

/// <summary>
/// One unit of a knowledge article for the search index (spec 029) and the assistant (spec 014): each question and
/// answer pair of an FAQ article, or the whole article in one locale for the other templates. The link opens the reader
/// page at the exact pair, so a citation points where the answer is.
/// </summary>
public sealed record KnowledgeUnit(Guid ArticleId, string Locale, string? Anchor, string Title, string Text, string Link);

/// <summary>Knowledge base units for indexing, read as the current user: an article the caller may not read has none.</summary>
public interface IKnowledgeIndex
{
    Task<IReadOnlyList<KnowledgeUnit>> UnitsAsync(Guid articleId, CancellationToken ct = default);
}

/// <summary>The version of a document that readers get: its name, file name, content type and size.</summary>
public sealed record DocumentReference(Guid DocumentId, string Name, Guid VersionId, string Label, string FileName, string ContentType, long Bytes);

/// <summary>
/// Documents and videos for another module that links to them (spec 028 FR-001, a course lesson made of a document or a
/// video). The caller keeps the document ID; the reader opens the file through the Documents routes, which check access
/// again. Every call is in the current tenant, as the current user.
/// </summary>
public interface IDocumentReferences
{
    /// <summary>The version in force when the caller may read the document and it has passed the malware scan; otherwise null.</summary>
    Task<DocumentReference?> ReadableAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>
/// The document library of a project or team space (spec 029 FR-006). The space is the library's audience: the space
/// roles assigned on the space apply to the library, so owners manage it, members add files and viewers read, and
/// nobody else reaches it. Spaces calls this after it has decided the caller may create or change the space.
/// </summary>
public interface ISpaceLibraries
{
    /// <summary>Creates the space's library and returns its ID, or null when document libraries are not in the plan.</summary>
    Task<Guid?> CreateAsync(Guid spaceId, string name, string classification, CancellationToken ct = default);

    /// <summary>Follows the space's name and classification.</summary>
    Task UpdateAsync(Guid libraryId, string name, string classification, CancellationToken ct = default);

    /// <summary>An archived space's library stays readable and takes no changes (FR-013).</summary>
    Task SetArchivedAsync(Guid libraryId, bool archived, CancellationToken ct = default);

    /// <summary>
    /// Whether the space's guests may open Confidential files (spec 029 FR-008). Restricted files stay closed to guests
    /// whatever this says (spec 026 FR-011).
    /// </summary>
    Task SetGuestConfidentialAsync(Guid libraryId, bool allowed, CancellationToken ct = default);
}

/// <summary>
/// A guest opened a Confidential item (spec 026 US5 scenario 3): a download or a preview. SpaceId is the space whose
/// library holds it. References only, never names or content.
/// </summary>
public sealed record GuestOpenedClassified(Guid DocumentId, Guid LibraryId, Guid? SpaceId, Guid UserId, string Classification) : IIntegrationEvent;
