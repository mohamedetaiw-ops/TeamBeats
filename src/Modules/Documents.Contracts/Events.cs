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
