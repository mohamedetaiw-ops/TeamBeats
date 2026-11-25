using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

public sealed record FourEyesView(bool Enabled, int Holders, PendingChangeView? Pending);

public sealed record FourEyesRequest(bool? Enabled);

public sealed record RejectChangeRequest(string? Reason);

/// <summary>
/// Four-eyes approval of critical tenant settings (spec 026 FR-016; defaults in the R3 plan). With the tenant switch on,
/// a change of a held kind is stored for another person holding the kind's permission, who approves it with step-up or
/// rejects it; the requester may withdraw it; after 72 hours it expires. Approving applies it as the approver through the
/// owning module's <see cref="IFourEyesApplier"/>. Turning the switch on needs two active holders of <c>security.manage</c>,
/// so a tenant cannot lock itself out, and turning it off is itself held.
/// </summary>
public sealed class FourEyesService(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuthorizationProvisioning roles,
    ITenantMemberships memberships, IAuditWriter audit, INotificationSender notifications, IClock clock, IServiceProvider services) : IFourEyes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);
    public const string PendingTemplate = "security.change_pending";
    public const string DecidedTemplate = "security.change_decided";
    public const string ChangesLink = "/admin/security/changes";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Set while an approved change is applied, so the owning endpoint applies it instead of holding it again.</summary>
    private bool applying;

    private IReadOnlyList<IFourEyesApplier> Appliers => services.GetServices<IFourEyesApplier>().ToList();

    private IFourEyesApplier Applier(string kind) =>
        Appliers.FirstOrDefault(a => a.Kind == kind) ?? throw new InvalidOperationException($"No four-eyes applier for {kind}.");

    public async Task<bool> EnabledAsync(CancellationToken ct) =>
        await session.ScalarAsync<bool?>("select enabled from security.four_eyes", null, ct) ?? false;

    public async Task<PendingChangeView?> HoldAsync(string kind, object request, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        if (applying || !await EnabledAsync(ct))
        {
            return null;
        }
        var user = context.RequireUser();
        var now = clock.UtcNow;
        await ExpireAsync(now, ct);
        if (await session.ScalarAsync<long>("select count(*) from security.pending_change where kind = @kind and status = 'pending'", new { kind }, ct) > 0)
        {
            throw AppException.Conflict("conflict.four_eyes.pending");
        }
        var id = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(request, request.GetType(), Json);
        await session.ExecAsync(
            """
            insert into security.pending_change (tenant_id, id, kind, request, fields, status, requested_by, requested_at, expires_at)
            values (platform.current_tenant_id(), @id, @kind, @payload::jsonb, @fields, 'pending', @user, @now, @expires)
            """, new { id, kind, payload, fields = fields.ToArray(), user, now, expires = now + Lifetime }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityChangeRequested, "pending_change", id.ToString(), Changes: new { kind, fields }), ct);
        var others = (await roles.TenantHoldersAsync(Applier(kind).Permission, ct)).Where(u => u != user).Distinct().ToList();
        if (others.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(others, NotificationCategories.SecurityNotice, PendingTemplate, ChangesLink,
                DedupeKey: $"{PendingTemplate}:{id:N}"), ct);
        }
        return (await GetAsync(id, ct))!;
    }

    public async Task<PendingChangeView?> PendingAsync(string kind, CancellationToken ct = default)
    {
        var rows = await QueryAsync("where kind = @kind and status = 'pending' and expires_at > @now", new { kind, now = clock.UtcNow }, 1, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Pending and recent changes of the kinds the person may decide; others are not listed.</summary>
    public async Task<IReadOnlyList<PendingChangeView>> ListAsync(CancellationToken ct)
    {
        var kinds = await KindsAsync(ct);
        if (kinds.Count == 0)
        {
            throw AppException.Denied();
        }
        await ExpireAsync(clock.UtcNow, ct);
        return await QueryAsync("where kind = any(@kinds)", new { kinds = kinds.ToArray() }, 50, ct);
    }

    public async Task<FourEyesView> SettingAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityView, AuthzTarget.Tenant, ct);
        return new FourEyesView(await EnabledAsync(ct), (await ActiveHoldersAsync(ct)).Count, await PendingAsync(FourEyesKinds.FourEyes, ct));
    }

    /// <summary>Turns the switch on at once, or holds turning it off; answers the setting and any pending change.</summary>
    public async Task<FourEyesView> SetAsync(FourEyesRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        if (request.Enabled is not { } enabled)
        {
            throw AppException.Validation("enabled", "validation.required");
        }
        var current = await EnabledAsync(ct);
        if (enabled == current)
        {
            return await SettingAsync(ct);
        }
        if (enabled && (await ActiveHoldersAsync(ct)).Count < 2)
        {
            throw AppException.Validation("enabled", "validation.four_eyes.one_holder");
        }
        if (!enabled && await HoldAsync(FourEyesKinds.FourEyes, request, ["enabled"], ct) is { } held)
        {
            return new FourEyesView(true, (await ActiveHoldersAsync(ct)).Count, held);
        }
        await session.ExecAsync(
            """
            insert into security.four_eyes (tenant_id, enabled, updated_at, updated_by) values (platform.current_tenant_id(), @enabled, @now, @user)
            on conflict (tenant_id) do update set enabled = excluded.enabled, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { enabled, now = clock.UtcNow, user = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityFourEyesChanged, "security_policy", null, Changes: new { from = current, to = enabled }), ct);
        return await SettingAsync(ct);
    }

    public async Task<PendingChangeView> ApproveAsync(Guid id, CancellationToken ct)
    {
        var (change, applier) = await DecidableAsync(id, ct);
        using var document = JsonDocument.Parse(await session.ScalarAsync<string>("select request::text from security.pending_change where id = @id", new { id }, ct) ?? "{}");
        applying = true;
        try
        {
            await applier.ApplyAsync(document.RootElement.Clone(), ct);
        }
        finally
        {
            applying = false;
        }
        return await DecideAsync(change, "approved", null, AuditActions.SecurityChangeApproved, ct);
    }

    public async Task<PendingChangeView> RejectAsync(Guid id, RejectChangeRequest request, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (reason is { Length: > 500 })
        {
            throw AppException.Validation("reason", "validation.four_eyes.reason_too_long");
        }
        var (change, _) = await DecidableAsync(id, ct);
        return await DecideAsync(change, "rejected", string.IsNullOrEmpty(reason) ? null : reason, AuditActions.SecurityChangeRejected, ct);
    }

    public async Task<PendingChangeView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        var change = await GetAsync(id, ct) ?? throw AppException.NotFound();
        if (change.RequestedBy != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        if (change.Status != "pending" || change.ExpiresAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.four_eyes.closed");
        }
        return await DecideAsync(change, "withdrawn", null, AuditActions.SecurityChangeWithdrawn, ct, notify: false);
    }

    /// <summary>Closes pending changes older than 72 hours as expired, audited and told to the requester.</summary>
    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        var expired = await session.QueryAsync(
            "update security.pending_change set status = 'expired', decided_at = @now where status = 'pending' and expires_at <= @now returning id, kind, requested_by",
            new { now }, r => (Id: r.GetGuid(0), Kind: r.GetString(1), By: r.GetGuid(2)), ct);
        foreach (var e in expired)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.SecurityChangeExpired, "pending_change", e.Id.ToString(), Changes: new { kind = e.Kind }), ct);
            await notifications.SendAsync(new NotificationRequest([e.By], NotificationCategories.SecurityNotice, DecidedTemplate, ChangesLink,
                DedupeKey: $"{DecidedTemplate}:{e.Id:N}"), ct);
        }
        return expired.Count;
    }

    private async Task<(PendingChangeView Change, IFourEyesApplier Applier)> DecidableAsync(Guid id, CancellationToken ct)
    {
        var change = await GetAsync(id, ct) ?? throw AppException.NotFound();
        var applier = Applier(change.Kind);
        if (!(await authz.CheckAsync(applier.Permission, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        if (change.Status != "pending" || change.ExpiresAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.four_eyes.closed");
        }
        if (change.RequestedBy == context.RequireUser())
        {
            throw AppException.Conflict("conflict.four_eyes.own_change");
        }
        return (change, applier);
    }

    private async Task<PendingChangeView> DecideAsync(PendingChangeView change, string status, string? reason, string action, CancellationToken ct, bool notify = true)
    {
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var updated = await session.ExecAsync(
            "update security.pending_change set status = @status, decided_by = @user, decided_at = @now, reason = @reason where id = @id and status = 'pending'",
            new { id = change.Id, status, user, now, reason }, ct);
        if (updated == 0)
        {
            throw AppException.Conflict("conflict.four_eyes.closed");
        }
        await audit.WriteAsync(new AuditEntry(action, "pending_change", change.Id.ToString(),
            Changes: new { kind = change.Kind, fields = change.Fields, requestedBy = change.RequestedBy, decidedBy = user, reason }), ct);
        if (notify)
        {
            await notifications.SendAsync(new NotificationRequest([change.RequestedBy], NotificationCategories.SecurityNotice, DecidedTemplate, ChangesLink,
                DedupeKey: $"{DecidedTemplate}:{change.Id:N}"), ct);
        }
        return (await GetAsync(change.Id, ct))!;
    }

    private async Task<IReadOnlyList<string>> KindsAsync(CancellationToken ct)
    {
        var kinds = new List<string>();
        foreach (var applier in Appliers)
        {
            if ((await authz.CheckAsync(applier.Permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                kinds.Add(applier.Kind);
            }
        }
        return kinds;
    }

    private async Task<IReadOnlyList<Guid>> ActiveHoldersAsync(CancellationToken ct)
    {
        var active = new List<Guid>();
        foreach (var user in (await roles.TenantHoldersAsync(Permissions.SecurityManage, ct)).Distinct())
        {
            if ((await memberships.GetAsync(user, ct))?.Status == MembershipStatus.Active)
            {
                active.Add(user);
            }
        }
        return active;
    }

    private async Task<PendingChangeView?> GetAsync(Guid id, CancellationToken ct)
    {
        var rows = await QueryAsync("where id = @id", new { id }, 1, ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<IReadOnlyList<PendingChangeView>> QueryAsync(string where, object args, int limit, CancellationToken ct)
    {
        var user = context.ActorId;
        var rows = await session.QueryAsync(
            $"""
            select id, kind, status, fields, requested_by, requested_at, expires_at, decided_by, decided_at, reason
            from security.pending_change {where} order by requested_at desc limit {limit}
            """, args,
            r => new PendingChangeView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetGuid(4),
                r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6), r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"),
                r.Get<string?>("reason")), ct);
        var now = clock.UtcNow;
        return rows.Select(c =>
        {
            var open = c.Status == "pending" && c.ExpiresAt > now;
            return c with { CanDecide = open && c.RequestedBy != user, CanWithdraw = open && c.RequestedBy == user };
        }).ToList();
    }
}

/// <summary>Applies an approved change of the tenant security policy (spec 026 FR-016) through the direct path.</summary>
public sealed class SecurityPolicyApplier(PolicyService policies) : IFourEyesApplier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => FourEyesKinds.SecurityPolicy;

    public string Permission => Permissions.SecurityManage;

    public async Task ApplyAsync(JsonElement request, CancellationToken ct) =>
        await policies.UpdateAsync(null, request.Deserialize<SecurityPolicyRequest>(Json)!, ct);
}

/// <summary>Applies turning four-eyes off once another administrator approves it.</summary>
public sealed class FourEyesSwitchApplier(FourEyesService fourEyes) : IFourEyesApplier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => FourEyesKinds.FourEyes;

    public string Permission => Permissions.SecurityManage;

    public async Task ApplyAsync(JsonElement request, CancellationToken ct) => await fourEyes.SetAsync(request.Deserialize<FourEyesRequest>(Json)!, ct);
}
