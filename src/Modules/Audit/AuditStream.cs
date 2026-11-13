using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Audit;

/// <summary><see cref="IAuditStream"/> over the sealed rows of the current tenant (forced RLS keeps it to that tenant).</summary>
public sealed class AuditStream(IDbSession session) : IAuditStream
{
    public const int MaxBatch = 1000;

    public async Task<IReadOnlyList<AuditStreamEvent>> ReadAsync(long afterPosition, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select chain_pos, id, occurred_at, actor_type, actor_id, action, outcome, target_type, target_id, target_org_unit_id, source_ip,
              correlation_id, changes
            from audit.audit_event where chain_pos > @afterPosition order by chain_pos limit @limit
            """, new { afterPosition, limit = Math.Clamp(limit, 1, MaxBatch) },
            r => new AuditStreamEvent(r.GetInt64(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
                r.IsDBNull(12) ? null : JsonDocument.Parse(r.GetString(12)).RootElement.Clone()), ct);

    public async Task<long> EndAsync(CancellationToken ct = default) =>
        await session.ScalarAsync<long?>("select max(chain_pos) from audit.audit_event where chain_pos is not null", null, ct) ?? 0;

    public async Task<long> PositionBeforeAsync(DateTimeOffset since, CancellationToken ct = default) =>
        (await session.ScalarAsync<long?>("select min(chain_pos) from audit.audit_event where chain_pos is not null and occurred_at >= @since", new { since }, ct)
            is { } first ? first : await EndAsync(ct) + 1) - 1;
}
