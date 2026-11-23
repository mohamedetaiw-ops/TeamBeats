using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Pages;

/// <param name="OrgUnitId">The org unit whose pages (and those of the units below it) the route covers; null for the whole tenant.</param>
/// <param name="ApproverIds">People who may decide besides those holding <c>page.approve</c> at the page's scope; at most 20.</param>
public sealed record PageRouteRequest(Guid? OrgUnitId, IReadOnlyList<Guid>? ApproverIds);

public sealed record PageRouteView(Guid Id, Guid? OrgUnitId, IReadOnlyList<Guid> ApproverIds, Guid UpdatedBy, DateTimeOffset UpdatedAt);

/// <summary>
/// Approval routes for pages (spec 032 R2 T023, FR-051): a scope the tenant marks so its pages go live only through an
/// approved submission. A route covers its org unit and the units below it, and the nearest route up the tree applies; a
/// tenant-wide route covers every page. Routes need <c>experience.approval_routes</c>; without it pages publish in one
/// step as in R1. Someone who publishes pages at a scope sets its route.
/// </summary>
public sealed class PageRoutes(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxApprovers = 20;

    internal sealed record Route(Guid Id, Guid? OrgUnitId, Guid[] ApproverIds);

    private const string Select = "select id, org_unit_id, approver_ids, updated_by, updated_at from experience.page_route";

    private static PageRouteView Read(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.Get<Guid?>("org_unit_id"), r.GetFieldValue<Guid[]>(2), r.GetGuid(3), r.GetFieldValue<DateTimeOffset>(4));

    private static AuthzTarget Target(Guid? orgUnitId) => orgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    /// <summary>The route that applies to a page of <paramref name="orgUnitId"/>, or null when none does or the package has no routes.</summary>
    internal async Task<Route?> ForPageAsync(Guid? orgUnitId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ExperienceApprovalRoutes, ct))
        {
            return null;
        }
        var routes = await session.QueryAsync("select id, org_unit_id, approver_ids from experience.page_route", null,
            r => new Route(r.GetGuid(0), r.Get<Guid?>("org_unit_id"), r.GetFieldValue<Guid[]>(2)), ct);
        if (routes.Count == 0)
        {
            return null;
        }
        if (orgUnitId is { } unit)
        {
            // Ancestors and self come nearest first (ordered by depth): the unit's own route, then its parent's, and so on.
            var path = await hierarchy.GetAncestorsAndSelfAsync(unit, ct);
            foreach (var step in path)
            {
                if (routes.FirstOrDefault(r => r.OrgUnitId == step) is { } own)
                {
                    return own;
                }
            }
        }
        return routes.FirstOrDefault(r => r.OrgUnitId is null);
    }

    /// <summary>The routes at scopes where the caller publishes pages.</summary>
    public async Task<IReadOnlyList<PageRouteView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceApprovalRoutes, ct);
        var all = await session.QueryAsync(Select + " order by updated_at", null, Read, ct);
        var shown = new List<PageRouteView>();
        foreach (var route in all)
        {
            if ((await authz.CheckAsync(Permissions.PagePublish, Target(route.OrgUnitId), ct)).Allowed)
            {
                shown.Add(route);
            }
        }
        return shown;
    }

    /// <summary>Creates or replaces the route of a scope.</summary>
    public async Task<PageRouteView> SetAsync(PageRouteRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceApprovalRoutes, ct);
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await authz.DemandVisibleAsync(Permissions.PagePublish, Target(request.OrgUnitId), ct);
        var approvers = (request.ApproverIds ?? []).Distinct().ToArray();
        if (approvers.Length > MaxApprovers)
        {
            throw AppException.Validation("approverIds", "validation.page.route_approvers");
        }
        foreach (var approver in approvers)
        {
            if (await memberships.GetAsync(approver, ct) is not { Status: MembershipStatus.Active })
            {
                throw AppException.Validation("approverIds", "validation.page.route_approvers");
            }
        }
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var id = await session.ScalarAsync<Guid>(
            """
            insert into experience.page_route (tenant_id, id, org_unit_id, approver_ids, updated_by, updated_at)
            values (platform.current_tenant_id(), @id, @unit, @approvers, @user, @now)
            on conflict (tenant_id, coalesce(org_unit_id, '00000000-0000-0000-0000-000000000000'::uuid))
            do update set approver_ids = excluded.approver_ids, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            returning id
            """, new { id = Ids.New(), unit = request.OrgUnitId, approvers, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageRouteChanged, "page_route", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { scope = request.OrgUnitId?.ToString() ?? "tenant", approvers }), ct);
        return new PageRouteView(id, request.OrgUnitId, approvers, user, new DateTimeOffset(now.UtcTicks / 10 * 10, TimeSpan.Zero));
    }

    /// <summary>Removes a route; submissions already waiting still go to their approvers.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceApprovalRoutes, ct);
        var route = await session.QuerySingleOrDefaultAsync(Select + " where id = @id", new { id }, Read, ct) ?? throw AppException.NotFound();
        await authz.DemandVisibleAsync(Permissions.PagePublish, Target(route.OrgUnitId), ct);
        await session.ExecAsync("delete from experience.page_route where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageRouteChanged, "page_route", id.ToString(), TargetOrgUnitId: route.OrgUnitId,
            Changes: new { scope = route.OrgUnitId?.ToString() ?? "tenant", removed = true }), ct);
    }
}
