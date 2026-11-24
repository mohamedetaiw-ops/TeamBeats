using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Entitlements;

/// <summary>An add-on or a contract override for one capability, with its validity window.</summary>
public sealed record GrantView(string CapabilityKey, bool Enabled, long? Limit, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? Reason, bool Active);

/// <param name="ValidTo">Required for an override (a contract always ends); optional for an add-on.</param>
public sealed record GrantRequest(bool? Enabled, long? Limit, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, string? Reason);

/// <summary>Published whenever a tenant's effective entitlements may have changed, so caches and gated modules refresh. No keys means any key (a subscription change).</summary>
public sealed record EntitlementsChanged(Guid TenantId, IReadOnlyList<string> ChangedKeys) : IIntegrationEvent;

/// <summary>
/// Add-ons and contract overrides (spec 005 US4, FR-004), set by platform operators only: a tenant can never change its
/// own entitlements (section 14). Every change is audited to the tenant and the platform (ENT-008).
/// </summary>
public sealed class ContractGrants(IDbSession session, ITenantContext context, IAuditWriter audit, IOutbox outbox)
{
    public const string FromPackage = "package";
    public const string Addon = "addon";
    public const string Override = "override";
    public const int MaxReason = 500;

    private static string Table(string kind) => kind switch
    {
        Addon => "entitlements.addon",
        Override => "entitlements.override",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private const string Columns = "capability_key, enabled, limit_value, valid_from, valid_to, reason, valid_from <= now() and (valid_to is null or valid_to > now())";

    private static GrantView Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetBoolean(1), r.Get<long?>("limit_value"), r.GetFieldValue<DateTimeOffset>(3), r.Get<DateTimeOffset?>("valid_to"), r.Get<string>("reason"), r.GetBoolean(6));

    /// <summary>The current tenant's grants of this kind that apply now.</summary>
    internal static async Task<IReadOnlyList<GrantView>> ActiveAsync(IDbSession session, string kind, CancellationToken ct) =>
        (await ListAsync(session, kind, ct)).Where(g => g.Active).ToList();

    internal static async Task<IReadOnlyList<GrantView>> ListAsync(IDbSession session, string kind, CancellationToken ct) =>
        await session.QueryAsync($"select {Columns} from {Table(kind)} order by capability_key", null, Map, ct);

    public Task<IReadOnlyList<GrantView>> ListAsync(string kind, CancellationToken ct) => ListAsync(session, kind, ct);

    public async Task<GrantView> SetAsync(string kind, string capabilityKey, GrantRequest request, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var type = await session.ScalarAsync<string>("select type from entitlements.capability where key = @k", new { k = capabilityKey }, ct)
            ?? throw AppException.Validation("capabilityKey", "validation.package.unknown_capability");
        var errors = new ValidationErrors();
        var enabled = request.Enabled ?? true;
        if (request.Limit is not null && (type != "limit" || request.Limit < 0))
        {
            errors.Add("limit", "validation.grant.limit_invalid");
        }
        var from = request.ValidFrom ?? DateTimeOffset.UtcNow;
        if (kind == Override && request.ValidTo is null)
        {
            errors.Add("validTo", "validation.grant.end_required");
        }
        if (request.ValidTo is { } to && to <= from)
        {
            errors.Add("validTo", "validation.grant.end_before_start");
        }
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is { Length: > MaxReason })
        {
            errors.Add("reason", "validation.grant.reason_too_long");
        }
        errors.ThrowIfAny();

        var before = (await ListAsync(kind, ct)).FirstOrDefault(g => g.CapabilityKey == capabilityKey);
        await session.ExecAsync(
            $"""
            insert into {Table(kind)} (tenant_id, capability_key, enabled, limit_value, valid_from, valid_to, reason, set_by, updated_at)
            values (@t, @k, @e, @l, @f, @to, @r, @by, now())
            on conflict (tenant_id, capability_key) do update set enabled = excluded.enabled, limit_value = excluded.limit_value,
              valid_from = excluded.valid_from, valid_to = excluded.valid_to, reason = excluded.reason, set_by = excluded.set_by, updated_at = now()
            """, new { t = tenantId, k = capabilityKey, e = enabled, l = request.Limit, f = from, to = request.ValidTo, r = reason, by = context.ActorId }, ct);
        var after = (await ListAsync(kind, ct)).First(g => g.CapabilityKey == capabilityKey);
        await RecordAsync(kind, capabilityKey, new { change = before is null ? "added" : "changed", before = Summary(before), after = Summary(after) }, ct);
        return after;
    }

    public async Task RemoveAsync(string kind, string capabilityKey, CancellationToken ct)
    {
        var before = (await ListAsync(kind, ct)).FirstOrDefault(g => g.CapabilityKey == capabilityKey) ?? throw AppException.NotFound();
        await session.ExecAsync($"delete from {Table(kind)} where capability_key = @k", new { k = capabilityKey }, ct);
        await RecordAsync(kind, capabilityKey, new { change = "removed", before = Summary(before) }, ct);
    }

    // The reason stays in the grant; the audit names the values and dates only.
    private static object? Summary(GrantView? g) => g is null ? null : new { g.Enabled, g.Limit, g.ValidFrom, g.ValidTo };

    private async Task RecordAsync(string kind, string capabilityKey, object changes, CancellationToken ct)
    {
        var action = kind == Addon ? AuditActions.AddonChanged : AuditActions.OverrideChanged;
        var tenantId = context.RequireTenant();
        await audit.WriteAsync(new AuditEntry(action, "capability", capabilityKey, Changes: changes), ct);
        await audit.WritePlatformAsync(new AuditEntry(action, "capability", capabilityKey, Changes: changes), tenantId, ct);
        await outbox.EnqueueAsync(new EntitlementsChanged(tenantId, [capabilityKey]), ct);
    }
}

internal static class GrantEndpoints
{
    /// <summary>Platform routes for one tenant's add-ons and overrides; the tenant itself has no route to them.</summary>
    public static void MapGrantEndpoints(this RouteGroupBuilder platform)
    {
        foreach (var (kind, segment) in new[] { (ContractGrants.Addon, "addons"), (ContractGrants.Override, "overrides") })
        {
            platform.MapGet($"/tenants/{{tenantId:guid}}/{segment}", async (Guid tenantId, ContractGrants grants, ITenantRegistry registry,
                TenantContextAccessor context, CancellationToken ct) =>
            {
                await EnterAsync(tenantId, registry, context, ct);
                return Results.Ok(await grants.ListAsync(kind, ct));
            });
            platform.MapPut($"/tenants/{{tenantId:guid}}/{segment}/{{key}}", async (Guid tenantId, string key, GrantRequest request, ContractGrants grants,
                ITenantRegistry registry, TenantContextAccessor context, IDbSession session, CancellationToken ct) =>
            {
                await EnterAsync(tenantId, registry, context, ct);
                var grant = await grants.SetAsync(kind, key, request, ct);
                await session.CommitAsync(ct);
                return Results.Ok(grant);
            });
            platform.MapDelete($"/tenants/{{tenantId:guid}}/{segment}/{{key}}", async (Guid tenantId, string key, ContractGrants grants,
                ITenantRegistry registry, TenantContextAccessor context, IDbSession session, CancellationToken ct) =>
            {
                await EnterAsync(tenantId, registry, context, ct);
                await grants.RemoveAsync(kind, key, ct);
                await session.CommitAsync(ct);
                return Results.NoContent();
            });
        }
    }

    private static async Task EnterAsync(Guid tenantId, ITenantRegistry registry, TenantContextAccessor context, CancellationToken ct)
    {
        var tenant = await registry.FindByIdAsync(tenantId, ct) ?? throw AppException.NotFound();
        context.SetTenant(tenant.Id);
    }
}
