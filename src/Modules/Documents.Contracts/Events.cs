using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents.Contracts;

/// <summary>
/// A user downloaded a document file (spec 011, consumed by spec 026 download volume alerts). IDs and size only, never
/// names or content. The Documents module publishes it once spec 011 downloads exist.
/// </summary>
public sealed record DocumentDownloaded(Guid DocumentId, Guid LibraryId, Guid UserId, long Bytes) : IIntegrationEvent;
