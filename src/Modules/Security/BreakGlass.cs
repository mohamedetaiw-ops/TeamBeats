using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

public sealed record BreakGlassRequest(string? Reason, int? Minutes);

public sealed record BreakGlassSessionView(Guid Id, Guid OperatorId, string Reason, DateTimeOffset OpenedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt, int Actions, bool Open);

/// <summary>
/// Break-glass (spec 001, spec 026 FR-020): the only way a platform operator acts on a tenant's data. An operator opens a
/// session for one tenant with a reason and a length, acts while it is open, and ends it (or it expires). Opening, each
/// action and the end are written to the tenant's own audit stream, so the tenant's security officers see them on the
/// audit page and in their SIEM, and to the platform stream with the tenant as subject. Opening also raises a security
/// alert, so the tenant is told at once.
/// </summary>
public sealed class BreakGlassService(IDbSession session, TenantContextAccessor context, ITenantRegistry tenants, IAuditWriter audit,
    AlertService alerts, IOutbox outbox, PolicyStore store, IClock clock)
{
    public const int MinMinutes = 15;
    public const int MaxMinutes = 240;
    public const int DefaultMinutes = 60;

    private Guid Operator => context.ActorType == ActorType.PlatformOperator && context.ActorId is { } id
        ? id
        : throw AppException.Unauthenticated();

    /// <summary>Enters the tenant's context, so row-level security keeps every read and write to that tenant.</summary>
    private async Task EnterAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await tenants.FindByIdAsync(tenantId, ct) ?? throw AppException.NotFound();
        if (tenant.Status == TenantLifecycleStatus.Closed)
        {
            throw AppException.Conflict("conflict.break_glass.tenant_closed");
        }
        context.SetTenant(tenant.Id);
    }

    private async Task WriteAsync(string action, Guid tenantId, Guid sessionId, object changes, CancellationToken ct)
    {
        var entry = new AuditEntry(action, "break_glass_session", sessionId.ToString(), Changes: changes);
        await audit.WriteAsync(entry, ct);
        await audit.WritePlatformAsync(entry, tenantId, ct);
    }

    public async Task<BreakGlassSessionView> OpenAsync(Guid tenantId, BreakGlassRequest request, CancellationToken ct)
    {
        var operatorId = Operator;
        var reason = request.Reason?.Trim() ?? "";
        var minutes = request.Minutes ?? DefaultMinutes;
        var errors = new ValidationErrors();
        if (reason.Length is < 10 or > 500)
        {
            errors.Add("reason", "validation.break_glass.reason");
        }
        if (minutes is < MinMinutes or > MaxMinutes)
        {
            errors.Add("minutes", "validation.break_glass.minutes");
        }
        errors.ThrowIfAny();
        await EnterAsync(tenantId, ct);
        var now = clock.UtcNow;
        var open = await session.ScalarAsync<long>(
            "select count(*) from security.break_glass_session where operator_id = @op and ended_at is null and expires_at > @now",
            new { op = operatorId, now }, ct);
        if (open > 0)
        {
            throw AppException.Conflict("conflict.break_glass.open");
        }
        var id = Ids.New();
        var expires = now.AddMinutes(minutes);
        await session.ExecAsync(
            """
            insert into security.break_glass_session (tenant_id, id, operator_id, reason, opened_at, expires_at)
            values (@t, @id, @op, @reason, @now, @expires)
            """, new { t = tenantId, id, op = operatorId, reason, now, expires }, ct);
        await WriteAsync(AuditActions.BreakGlassOpened, tenantId, id, new { reason, minutes, expiresAt = expires }, ct);
        await alerts.RaiseAsync(AlertKinds.PlatformAccess, null, new { sessionId = id, reason, minutes, expiresAt = expires },
            $"{AlertKinds.PlatformAccess}:{id}", ct);
        return new BreakGlassSessionView(id, operatorId, reason, now, expires, null, 0, true);
    }

    public async Task<IReadOnlyList<BreakGlassSessionView>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        _ = Operator;
        await EnterAsync(tenantId, ct);
        var now = clock.UtcNow;
        return await session.QueryAsync(
            """
            select id, operator_id, reason, opened_at, expires_at, ended_at, actions
            from security.break_glass_session order by opened_at desc limit 100
            """, null, r =>
            {
                var ended = r.IsDBNull(5) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(5);
                var expires = r.GetFieldValue<DateTimeOffset>(4);
                return new BreakGlassSessionView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), expires, ended,
                    r.GetInt32(6), ended is null && expires > now);
            }, ct);
    }

    /// <summary>The operator's open session for the tenant, locked for the action; refused when none is open.</summary>
    private async Task<Guid> RequireOpenAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        await EnterAsync(tenantId, ct);
        var found = await session.ScalarAsync<Guid?>(
            """
            select id from security.break_glass_session
            where id = @id and operator_id = @op and ended_at is null and expires_at > @now
            for update
            """, new { id = sessionId, op = Operator, now = clock.UtcNow }, ct);
        return found ?? throw AppException.Conflict("conflict.break_glass.not_open");
    }

    public async Task EndAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        await RequireOpenAsync(tenantId, sessionId, ct);
        await session.ExecAsync("update security.break_glass_session set ended_at = @now where id = @id", new { id = sessionId, now = clock.UtcNow }, ct);
        await WriteAsync(AuditActions.BreakGlassEnded, tenantId, sessionId, new { ended = "operator" }, ct);
    }

    /// <summary>
    /// Clears the tenant's allow lists (all pages and administration pages) when they lock every administrator out (spec 026
    /// edge cases). Block entries stay. The before values are kept in the audit entry, so the tenant can put them back.
    /// </summary>
    public async Task ResetNetworkRulesAsync(Guid tenantId, Guid sessionId, CancellationToken ct)
    {
        await RequireOpenAsync(tenantId, sessionId, ct);
        await session.ExecAsync("insert into security.policy (tenant_id) values (platform.current_tenant_id()) on conflict do nothing", null, ct);
        await session.ExecAsync("select 1 from security.policy for update", null, ct);
        var current = await store.ReadAsync(ct) ?? SecurityPolicySnapshot.Default;
        await session.ExecAsync(
            "update security.policy set ip_allow = '{}', admin_ip_allow = '{}', version = version + 1, updated_at = @now, updated_by = null",
            new { now = clock.UtcNow }, ct);
        await session.ExecAsync("update security.break_glass_session set actions = actions + 1 where id = @id", new { id = sessionId }, ct);
        await WriteAsync(AuditActions.BreakGlassNetworkReset, tenantId, sessionId,
            new { before = new { ipAllow = current.IpAllow, adminIpAllow = current.AdminIpAllow } }, ct);
        await outbox.EnqueueAsync(new SecurityPolicyChanged(current.Version + 1, ["ipAllow", "adminIpAllow"]), ct);
        session.OnCommitted(() => store.Forget(tenantId));
    }

    /// <summary>Whether any break-glass session on the current tenant is open now (posture page).</summary>
    public async Task<bool> AnyOpenAsync(CancellationToken ct) =>
        await session.ScalarAsync<long>("select count(*) from security.break_glass_session where ended_at is null and expires_at > @now",
            new { now = clock.UtcNow }, ct) > 0;
}
