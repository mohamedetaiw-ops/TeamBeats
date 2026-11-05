using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

public sealed record CreateTenantRequest(
    string Key,
    string DisplayName,
    string PackageKey,
    string DefaultLocale,
    IReadOnlyList<string>? EnabledLocales,
    string? TimeZone,
    IReadOnlyList<string> Admins);

public sealed record TenantView(Guid Id, string Key, string DisplayName, string Status, DateTimeOffset CreatedAt, DateTimeOffset? DeletionDueAt);

public sealed record StatusChangeRequest(string? Reason);

/// <summary>Platform operator use cases for the tenant lifecycle (spec 001 US1, US3).</summary>
public sealed class PlatformTenants(
    IDbSession session,
    TenantContextAccessor context,
    IAuthorizationProvisioning authz,
    ISubscriptionProvisioning subscriptions,
    ISessionRevocation sessions,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public async Task<TenantView> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        var key = TenantRules.NormalizeKey(request.Key);
        var enabled = request.EnabledLocales is { Count: > 0 } ? request.EnabledLocales : [request.DefaultLocale];
        var timeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone;
        var admins = (request.Admins ?? []).Select(TenantRules.NormalizeEmail).Distinct().ToList();

        var errors = new ValidationErrors();
        TenantRules.ValidateKey(key, errors);
        TenantRules.ValidateLocales(request.DefaultLocale, enabled, errors);
        TenantRules.ValidateTimeZone(timeZone, errors);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200)
        {
            errors.Add("displayName", "validation.required");
        }
        if (admins.Count == 0)
        {
            errors.Add("admins", "validation.tenant.admin_required");
        }
        if (admins.Any(a => !TenantRules.LooksLikeEmail(a)))
        {
            errors.Add("admins", "validation.email");
        }
        errors.ThrowIfAny();

        var id = Ids.New();
        try
        {
            await session.ExecAsync(
                "insert into tenancy.tenant (id, key, display_name, status) values (@id, @key, @name, 'active')",
                new { id, key, name = request.DisplayName.Trim() }, ct);
        }
        catch (PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.tenant.key_taken");
        }

        // From here on, everything is written inside the new tenant's context, in the same transaction (FR-004).
        context.SetTenant(id);
        await session.ExecAsync(
            """
            insert into tenancy.tenant_settings (tenant_id, default_locale, enabled_locales, time_zone)
            values (@id, @locale, @enabled, @tz)
            """, new { id, locale = request.DefaultLocale, enabled = enabled.ToArray(), tz = timeZone }, ct);
        await session.ExecAsync("insert into tenancy.hierarchy_version (tenant_id, version) values (@id, 1)", new { id }, ct);
        foreach (var type in MembershipTypes.All)
        {
            await session.ExecAsync(
                "insert into tenancy.membership_type_policy (tenant_id, membership_type, default_employee_role) values (@id, @type, true)",
                new { id, type }, ct);
        }

        await authz.SeedTenantRolesAsync(ct);
        await subscriptions.AssignInitialPackageAsync(request.PackageKey, ct);

        foreach (var email in admins)
        {
            var appointmentId = Ids.New();
            await session.ExecAsync(
                """
                insert into tenancy.tenant_admin_appointment (tenant_id, id, invited_email, status, appointed_by)
                values (@id, @aid, @email, 'pending', @by)
                """, new { id, aid = appointmentId, email, by = context.ActorId }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.TenantAdminAppointed, "tenant_admin_appointment", appointmentId.ToString(),
                Changes: new { status = "pending" }), ct);
        }

        var created = new { key, package = request.PackageKey, defaultLocale = request.DefaultLocale, admins = admins.Count };
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantCreated, "tenant", id.ToString(), Changes: created), ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.TenantCreated, "tenant", id.ToString(), Changes: created), id, ct);
        await outbox.EnqueueAsync(new TenantCreated(id, key, request.PackageKey), ct);

        return new TenantView(id, key, request.DisplayName.Trim(), "active", clock.UtcNow, null);
    }

    public async Task<IReadOnlyList<TenantView>> ListAsync(string? status, string? q, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select id, key, display_name, status, created_at, deletion_due_at from tenancy.tenant
            where (@status::text is null or status = @status)
              and (@q::text is null or key ilike '%' || @q || '%' or display_name ilike '%' || @q || '%')
            order by key
            limit 500
            """,
            new { status, q = q?.Replace("%", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal) },
            MapView, ct);

    public async Task<TenantView> ChangeStatusAsync(Guid tenantId, TenantLifecycleStatus to, string? reason, CancellationToken ct)
    {
        var current = await session.QuerySingleOrDefaultAsync(
            "select id, key, display_name, status, created_at, deletion_due_at, closed_retention_days from tenancy.tenant where id = @id for update",
            new { id = tenantId }, r => (View: MapView(r), Retention: r.GetInt32(6)), ct);
        if (current == default)
        {
            throw AppException.NotFound();
        }

        var from = Sql.ParseEnum<TenantLifecycleStatus>(current.View.Status);
        if (!TenantRules.CanTransition(from, to))
        {
            throw AppException.Conflict("conflict.tenant.invalid_transition");
        }

        DateTimeOffset? deletionDue = to == TenantLifecycleStatus.Closed ? clock.UtcNow.AddDays(current.Retention) : null;
        await session.ExecAsync(
            """
            update tenancy.tenant set status = @to, status_changed_at = now(),
              closed_at = case when @to = 'closed' then now() else closed_at end,
              deletion_due_at = coalesce(@due, deletion_due_at)
            where id = @id
            """, new { id = tenantId, to, due = deletionDue }, ct);

        context.SetTenant(tenantId);
        if (to is TenantLifecycleStatus.Suspended or TenantLifecycleStatus.Closed)
        {
            await sessions.RevokeTenantSessionsAsync("tenant_" + Sql.ToSnake(to.ToString()), ct);
        }

        var changes = new { from = Sql.ToSnake(from.ToString()), to = Sql.ToSnake(to.ToString()), reason, deletionDueAt = deletionDue };
        await audit.WriteAsync(new AuditEntry(AuditActions.TenantStatusChanged, "tenant", tenantId.ToString(), Changes: changes), ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.TenantStatusChanged, "tenant", tenantId.ToString(), Changes: changes), tenantId, ct);

        IIntegrationEvent evt = to switch
        {
            TenantLifecycleStatus.Suspended => new TenantSuspended(tenantId, reason),
            TenantLifecycleStatus.Active => new TenantReactivated(tenantId),
            _ => new TenantClosed(tenantId, deletionDue!.Value),
        };
        switch (evt)
        {
            case TenantSuspended s: await outbox.EnqueueAsync(s, ct); break;
            case TenantReactivated r: await outbox.EnqueueAsync(r, ct); break;
            case TenantClosed c: await outbox.EnqueueAsync(c, ct); break;
        }

        return current.View with { Status = Sql.ToSnake(to.ToString()), DeletionDueAt = deletionDue ?? current.View.DeletionDueAt };
    }

    private static TenantView MapView(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.Get<DateTimeOffset?>("deletion_due_at"));
}
