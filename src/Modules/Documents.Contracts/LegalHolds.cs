namespace TeamBeats.Modules.Documents.Contracts;

/// <summary>
/// Legal holds of spec 011 (FR-015; FR-020 of spec 009: a hold takes precedence over retention). A held resource is not
/// deleted, purged or disposed by any module's retention or cleanup. Each module asks before deleting; the holds are kept
/// by the Documents module, in the current tenant. Documents are held by custodian and date range, library, folder or
/// item through records managers' holds; other modules' resources are held one by one.
/// </summary>
public interface ILegalHolds
{
    /// <summary>Which of the resources are under a hold now. For documents (<c>doc_document</c>) every kind of hold counts.</summary>
    Task<IReadOnlySet<Guid>> HeldAsync(string resourceType, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct = default);

    /// <summary>Places a hold on one resource; placing it again keeps the first.</summary>
    Task PlaceAsync(string resourceType, Guid resourceId, string reason, CancellationToken ct = default);

    Task ReleaseAsync(string resourceType, Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// The audit records active holds keep (US7 scenario 5): those with the custodian as actor or target in the date
    /// range. The audit purge keeps them until the hold is released.
    /// </summary>
    Task<IReadOnlyList<AuditHoldSelection>> AuditSelectionsAsync(CancellationToken ct = default);
}

/// <summary>Audit records of one custodian, as actor or target, between two optional times.</summary>
public sealed record AuditHoldSelection(Guid CustodianId, DateTimeOffset? From, DateTimeOffset? To);
