namespace TeamBeats.Modules.Documents.Contracts;

/// <summary>
/// Legal holds of spec 011 (FR-020 of spec 009: a hold takes precedence over retention). A held resource is not deleted,
/// purged or disposed by any module's retention or cleanup. Each module asks before deleting; the hold itself is kept
/// by the Documents module, in the current tenant. R2 starts with holds on individual resources; holds by custodian,
/// library, folder or query (spec 011 FR-019) resolve to the same check.
/// </summary>
public interface ILegalHolds
{
    /// <summary>Which of the resources are under a hold now.</summary>
    Task<IReadOnlySet<Guid>> HeldAsync(string resourceType, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct = default);

    /// <summary>Places a hold on one resource; placing it again keeps the first.</summary>
    Task PlaceAsync(string resourceType, Guid resourceId, string reason, CancellationToken ct = default);

    Task ReleaseAsync(string resourceType, Guid resourceId, CancellationToken ct = default);
}
