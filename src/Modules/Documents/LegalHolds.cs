using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// The legal hold register (spec 011 R2). Callers authorize their own action; this only records and answers. Records
/// managers' holds are kept and changed by <see cref="Records"/>.
/// </summary>
public sealed class LegalHolds(IDbSession session, ITenantContext context, IClock clock) : ILegalHolds
{
    public async Task<IReadOnlySet<Guid>> HeldAsync(string resourceType, IReadOnlyCollection<Guid> resourceIds, CancellationToken ct = default)
    {
        if (resourceIds.Count == 0)
        {
            return new HashSet<Guid>();
        }
        var ids = resourceIds.Distinct().ToArray();
        if (resourceType == Securables.Document)
        {
            // Documents are held by records managers' holds as well (custodian, library, folder, item).
            return (await session.QueryAsync($"select d.id from documents.document d where d.id = any(@ids) and {RecordsSql.Held("d")}",
                new { ids }, r => r.GetGuid(0), ct)).ToHashSet();
        }
        return (await session.QueryAsync("select resource_id from documents.legal_hold where resource_type = @type and resource_id = any(@ids)",
            new { type = resourceType, ids }, r => r.GetGuid(0), ct)).ToHashSet();
    }

    public async Task<IReadOnlyList<AuditHoldSelection>> AuditSelectionsAsync(CancellationToken ct = default) =>
        await session.QueryAsync("select custodian_id, from_at, to_at from documents.hold where status = 'active' and include_audit",
            null, r => new AuditHoldSelection(r.GetGuid(0), r.Get<DateTimeOffset?>("from_at"), r.Get<DateTimeOffset?>("to_at")), ct);

    public Task PlaceAsync(string resourceType, Guid resourceId, string reason, CancellationToken ct = default) =>
        session.ExecAsync(
            """
            insert into documents.legal_hold (tenant_id, resource_type, resource_id, reason, placed_by, placed_at)
            values (platform.current_tenant_id(), @type, @id, @reason, @by, @now) on conflict do nothing
            """, new { type = resourceType, id = resourceId, reason, by = context.ActorType == ActorType.User ? context.ActorId : null, now = clock.UtcNow }, ct);

    public Task ReleaseAsync(string resourceType, Guid resourceId, CancellationToken ct = default) =>
        session.ExecAsync("delete from documents.legal_hold where resource_type = @type and resource_id = @id", new { type = resourceType, id = resourceId }, ct);
}
