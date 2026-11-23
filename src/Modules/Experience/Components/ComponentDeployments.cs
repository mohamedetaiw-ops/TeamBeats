using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Components;

public sealed record ComponentDeploymentView(string ComponentId, string From, string To, Guid DeployedBy, DateTimeOffset DeployedAt);

/// <summary>An upgrade the platform offers: a registered hook from one major to the next.</summary>
public sealed record ComponentUpgradeOption(string ComponentId, string Name, int FromMajor, string From, string To, bool Deployed);

public sealed record ComponentDeploymentsView(IReadOnlyList<ComponentDeploymentView> Deployed, IReadOnlyList<ComponentUpgradeOption> Available);

/// <summary>
/// Independent component deployment (spec 032 R2 T031, FR-033). Component code ships with the platform and both majors of a
/// component stay registered; a tenant chooses when its pages move to the next major. Upgrading records a deployment, and
/// from then on every live release, fragment and personal layout that still places the old major is shown with the new one:
/// the registered migration hook converts the placement in memory when the page is read, so no page definition is rebuilt
/// or republished. A placement the hook cannot convert, or whose converted properties the new manifest rejects, keeps
/// showing the old major. Pages that do not place the component are never read differently. Removing a deployment rolls
/// live pages back to the old major; drafts the upgrade already converted stay converted.
/// </summary>
public sealed class ComponentDeployments(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ComponentRegistry registry,
    IAuditWriter audit,
    IClock clock,
    TeamBeats.Localization.ILocalizer localizer,
    TeamBeats.Localization.RequestLocale locale)
{
    private IReadOnlyDictionary<string, ComponentMigration>? _deployed;

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await authz.DemandAsync(Permissions.ComponentEnable, AuthzTarget.Tenant, ct);
    }

    /// <summary>What the tenant deployed, and every upgrade the platform offers with whether it is deployed.</summary>
    public async Task<ComponentDeploymentsView> ListAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        var deployed = await session.QueryAsync(
            "select component_id, from_major, to_major, deployed_by, deployed_at from experience.component_deployment order by component_id, from_major",
            null, r => new ComponentDeploymentView(r.Get<string>("component_id")!, $"{r.Get<string>("component_id")}@{r.Get<int>("from_major")}",
                $"{r.Get<string>("component_id")}@{r.Get<int>("to_major")}", r.Get<Guid>("deployed_by"), r.Get<DateTimeOffset>("deployed_at")), ct);
        var available = registry.Migrations.Select(m => new ComponentUpgradeOption(m.Id, localizer.Get(registry.Find(m.To)!.Display.NameKey, locale.Locale),
            m.FromMajor, m.From, m.To, deployed.Any(d => d.From == m.From))).ToList();
        return new ComponentDeploymentsView(deployed, available);
    }

    /// <summary>Records that the tenant's live pages show <paramref name="migration"/>'s target major. Called by the upgrade.</summary>
    internal async Task RecordAsync(ComponentMigration migration, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into experience.component_deployment (tenant_id, component_id, from_major, to_major, deployed_by, deployed_at)
            values (platform.current_tenant_id(), @id, @from, @to, @user, @now)
            on conflict (tenant_id, component_id, from_major) do update set to_major = excluded.to_major, deployed_by = excluded.deployed_by,
              deployed_at = excluded.deployed_at
            """, new { id = migration.Id, from = migration.FromMajor, to = migration.ToMajor, user = context.RequireUser(), now = clock.UtcNow }, ct);
        _deployed = null;
    }

    /// <summary>Rolls live pages back to <c>id@fromMajor</c>; releases were never changed, so they show it again at once.</summary>
    public async Task RemoveAsync(string componentId, int fromMajor, CancellationToken ct)
    {
        await DemandAsync(ct);
        var removed = await session.ExecAsync("delete from experience.component_deployment where component_id = @componentId and from_major = @fromMajor",
            new { componentId, fromMajor }, ct);
        if (removed == 0)
        {
            throw AppException.NotFound();
        }
        _deployed = null;
        await audit.WriteAsync(new AuditEntry(AuditActions.ComponentRolledBack, "component", componentId,
            Changes: new { from = $"{componentId}@{fromMajor + 1}", to = $"{componentId}@{fromMajor}" }), ct);
    }

    /// <summary>The page as the tenant's deployments show it: placements of a deployed old major move to the new one.</summary>
    internal async Task<PageDefinition> ApplyAsync(PageDefinition definition, CancellationToken ct)
    {
        var deployed = await DeployedAsync(ct);
        if (deployed.Count == 0 || !definition.Regions.Any(r => r.Placements.Any(p => deployed.ContainsKey(p.ComponentRef))))
        {
            return definition;
        }
        return definition with
        {
            Regions = definition.Regions.Select(r => r with { Placements = r.Placements.Select(p => Upgrade(p, deployed)).ToList() }).ToList(),
        };
    }

    /// <summary>One placement as the deployments show it; used for placements that come from personal layouts and defaults.</summary>
    internal async Task<Func<Placement, Placement>> UpgraderAsync(CancellationToken ct)
    {
        var deployed = await DeployedAsync(ct);
        return deployed.Count == 0 ? p => p : p => Upgrade(p, deployed);
    }

    private Placement Upgrade(Placement placement, IReadOnlyDictionary<string, ComponentMigration> deployed)
    {
        // Follows a chain of deployed majors (1 to 2, then 2 to 3); every step goes up one major, so it ends.
        while (deployed.TryGetValue(placement.ComponentRef, out var migration) && registry.Find(migration.To) is { } target)
        {
            JsonElement? props;
            try
            {
                props = migration.Convert(placement.Props);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return placement;
            }
            if (PropertyValidator.Validate(target.Properties, props).Count > 0)
            {
                return placement;
            }
            placement = placement with { ComponentRef = migration.To, Props = props };
        }
        return placement;
    }

    private async Task<IReadOnlyDictionary<string, ComponentMigration>> DeployedAsync(CancellationToken ct)
    {
        if (_deployed is not null)
        {
            return _deployed;
        }
        var rows = await session.QueryAsync("select component_id, from_major from experience.component_deployment", null,
            r => $"{r.Get<string>("component_id")}@{r.Get<int>("from_major")}", ct);
        // Only deployments whose hook is still registered apply; a hook the platform withdrew leaves pages on the old major.
        return _deployed = rows.Select(registry.MigrationFrom).OfType<ComponentMigration>().ToDictionary(m => m.From, StringComparer.Ordinal);
    }
}
