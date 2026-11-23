using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Components;

public sealed record EnablementRequest(bool Enabled);

/// <param name="Name">The component's name in the caller's language (R2 T032 admin screens).</param>
public sealed record EnablementView(string ComponentId, bool Enabled, string? Name = null);

/// <summary>Tenant choices about platform components (spec 032 section 7). A component with no stored choice is enabled.</summary>
public sealed class ComponentEnablement(IDbSession session, ITenantContext context, IAuthorizationService authz, ComponentRegistry registry,
    IAuditWriter audit, IClock clock, TeamBeats.Localization.ILocalizer localizer, TeamBeats.Localization.RequestLocale locale)
{
    public async Task<IReadOnlySet<string>> DisabledAsync(CancellationToken ct) =>
        (await session.QueryAsync("select component_id from experience.component_enablement where not enabled", null,
            r => r.Get<string>("component_id")!, ct)).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<EnablementView>> ListAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ComponentEnable, AuthzTarget.Tenant, ct);
        var disabled = await DisabledAsync(ct);
        // The newest manifest names the component; every major shares the id the switch turns off.
        return registry.All.GroupBy(m => m.Id, StringComparer.Ordinal).Select(g => g.OrderByDescending(m => System.Version.Parse(m.Version)).First())
            .Select(m => new EnablementView(m.Id, !disabled.Contains(m.Id), localizer.Get(m.Display.NameKey, locale.Locale))).ToList();
    }

    public async Task<EnablementView> SetAsync(string componentId, bool enabled, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ComponentEnable, AuthzTarget.Tenant, ct);
        if (registry.All.All(m => m.Id != componentId))
        {
            throw AppException.NotFound();
        }
        var before = !(await DisabledAsync(ct)).Contains(componentId);
        await session.ExecAsync(
            """
            insert into experience.component_enablement (tenant_id, component_id, enabled, changed_by, changed_at)
            values (@t, @c, @e, @u, @now)
            on conflict (tenant_id, component_id) do update set enabled = excluded.enabled, changed_by = excluded.changed_by, changed_at = excluded.changed_at
            """,
            new { t = context.RequireTenant(), c = componentId, e = enabled, u = context.RequireUser(), now = clock.UtcNow }, ct);
        if (before != enabled)
        {
            await audit.WriteAsync(new AuditEntry(enabled ? AuditActions.ComponentEnabled : AuditActions.ComponentDisabled, "component", componentId,
                Changes: new { enabled = new { before, after = enabled } }), ct);
        }
        return new EnablementView(componentId, enabled);
    }
}
