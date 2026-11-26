using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>A pack as the platform operator sees it: whether it is turned off for all tenants, why, by whom and when.</summary>
public sealed record PlatformPackView(string Kind, int Version, string CapabilityKey, bool Available, string? Reason, Guid? TurnedOffBy, DateTimeOffset? TurnedOffAt);

public sealed record PlatformPackRequest(bool Available, string? Reason);

/// <summary>
/// The platform switch for connector packs (spec 017 section 3: the platform operator publishes and updates packs and
/// disables a pack for all tenants). A pack turned off is shown in every tenant's marketplace as unavailable, cannot be
/// added, and the runtime refuses its calls with <see cref="ConnectorErrorClasses.PackUnavailable"/>; existing
/// connections and their settings stay, so turning it on again needs nothing from tenants. Platform audit records each change.
/// </summary>
public sealed class PlatformPacks(IDbSession session, IEnumerable<IConnectorPack> packs, ITenantContext context, IAuditWriter audit, IClock clock)
{
    public Task<bool> IsOffAsync(string kind, CancellationToken ct) =>
        session.ScalarAsync<bool>("select exists (select 1 from integrations.platform_pack_state where kind = @kind)", new { kind }, ct);

    public async Task<IReadOnlyDictionary<string, string>> OffAsync(CancellationToken ct) =>
        (await session.QueryAsync("select kind, reason from integrations.platform_pack_state", null, r => (Kind: r.GetString(0), Reason: r.GetString(1)), ct))
            .ToDictionary(x => x.Kind, x => x.Reason, StringComparer.Ordinal);

    public async Task<IReadOnlyList<PlatformPackView>> ListAsync(CancellationToken ct)
    {
        var off = (await session.QueryAsync("select kind, reason, turned_off_by, turned_off_at from integrations.platform_pack_state", null,
            r => (Kind: r.GetString(0), Reason: r.GetString(1), By: r.GetGuid(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct)).ToDictionary(x => x.Kind);
        return packs.Where(p => p.Listed).OrderBy(p => p.Kind, StringComparer.Ordinal).Select(p => off.TryGetValue(p.Kind, out var o)
            ? new PlatformPackView(p.Kind, p.Version, p.CapabilityKey, false, o.Reason, o.By, o.At)
            : new PlatformPackView(p.Kind, p.Version, p.CapabilityKey, true, null, null, null)).ToList();
    }

    public async Task<PlatformPackView> SetAsync(string kind, PlatformPackRequest request, CancellationToken ct)
    {
        if (!packs.Any(p => p.Listed && p.Kind == kind))
        {
            throw AppException.NotFound();
        }
        if (request.Available)
        {
            if (await session.ExecAsync("delete from integrations.platform_pack_state where kind = @kind", new { kind }, ct) > 0)
            {
                await audit.WritePlatformAsync(new AuditEntry(AuditActions.ConnectorPackTurnedOn, "connector_pack", kind), null, ct);
            }
        }
        else
        {
            var reason = request.Reason?.Trim() ?? "";
            if (reason.Length is < 1 or > 500)
            {
                throw AppException.Validation("reason", "validation.integrations.pack_reason");
            }
            await session.ExecAsync(
                """
                insert into integrations.platform_pack_state (kind, reason, turned_off_by, turned_off_at) values (@kind, @reason, @by, @now)
                on conflict (kind) do update set reason = excluded.reason, turned_off_by = excluded.turned_off_by, turned_off_at = excluded.turned_off_at
                """, new { kind, reason, by = context.ActorId ?? throw AppException.Unauthenticated(), now = clock.UtcNow }, ct);
            await audit.WritePlatformAsync(new AuditEntry(AuditActions.ConnectorPackTurnedOff, "connector_pack", kind, Changes: new { reason }), null, ct);
        }
        return (await ListAsync(ct)).Single(p => p.Kind == kind);
    }
}
