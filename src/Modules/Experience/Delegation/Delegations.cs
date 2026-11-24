using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Delegation;

public sealed record DelegationRequest(IReadOnlyList<string>? Areas);

/// <summary>One person's delegated areas for one organization and those below it.</summary>
public sealed record DelegationView(Guid OrgUnitId, Guid UserId, IReadOnlyList<string> Areas, Guid DelegatedBy, DateTimeOffset DelegatedAt);

/// <summary>
/// Delegated experience administration (spec 033 R2 T019, US4 scenario 3, FR-020). An area is a built-in role given at an
/// organization's subtree: pages (Page publisher), brand (Brand publisher: apply and override brands there) and
/// navigation (Navigation editor). The authorization service then decides every call, so a delegate is refused in a
/// neighbouring organization and on a value its parent locked, exactly as any holder of those roles. Delegating needs
/// roles.assign over the whole subtree, every permission of the area there (nobody grants what they do not hold), and the
/// org.delegated_admin capability. Templates stay a tenant library (spec 032), so they are not an area.
/// </summary>
public sealed class Delegations(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning provisioning,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IAuditWriter audit,
    IClock clock)
{
    private static IReadOnlyDictionary<string, string> AreaRoles => DelegationRoles.ByArea;

    private sealed record Row(Guid Unit, Guid User, string Area, Guid Assignment, bool Owned, Guid By, DateTimeOffset At);

    /// <summary>The delegations of the organizations the caller may delegate in; nothing for anyone else.</summary>
    public async Task<IReadOnlyList<DelegationView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.OrgDelegatedAdmin, ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.RolesAssign, ct))
        {
            throw AppException.Denied();
        }
        var rows = await RowsAsync(null, ct);
        var result = new List<DelegationView>();
        foreach (var group in rows.GroupBy(r => (r.Unit, r.User)))
        {
            if ((await authz.CheckAsync(Permissions.RolesAssign, AuthzTarget.OrgUnit(group.Key.Unit), ct)).Allowed)
            {
                result.Add(View(group.Key.Unit, group.Key.User, group.ToList()));
            }
        }
        return result.OrderBy(d => d.OrgUnitId).ThenBy(d => d.UserId).ToList();
    }

    /// <summary>Sets exactly these areas for the person at the organization: missing ones are given, others taken back.</summary>
    public async Task<DelegationView?> SetAsync(Guid unit, Guid user, DelegationRequest request, CancellationToken ct)
    {
        // Another tenant's organization is not found, whatever this tenant's package allows.
        if (!await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await entitlements.DemandAsync(Capabilities.OrgDelegatedAdmin, ct);
        var subtree = await hierarchy.GetSubtreesAsync([unit], ct);
        foreach (var u in subtree)
        {
            // Delegating in an organization means assigning roles over all of it, below included.
            await authz.DemandVisibleAsync(Permissions.RolesAssign, AuthzTarget.OrgUnit(u), ct);
        }
        var areas = (request.Areas ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (areas.Any(a => !AreaRoles.ContainsKey(a)))
        {
            throw AppException.Validation("areas", "validation.experience.delegation_area");
        }
        if (await memberships.GetAsync(user, ct) is not { Status: MembershipStatus.Active } member || member.MembershipType == MembershipTypes.Guest)
        {
            throw AppException.Validation("userId", "validation.experience.delegation_user");
        }
        if (user == context.ActorId)
        {
            throw AppException.Validation("userId", "validation.experience.delegation_self");
        }

        var current = await RowsAsync((unit, user), ct);
        var actor = context.ActorId ?? throw AppException.Denied();
        foreach (var area in areas.Where(a => current.All(r => r.Area != a)))
        {
            // Nobody gives what they do not hold over the whole organization.
            foreach (var permission in await provisioning.RolePermissionsAsync(AreaRoles[area], ct))
            {
                foreach (var u in subtree)
                {
                    if (!(await authz.CheckAsync(permission, AuthzTarget.OrgUnit(u), ct)).Allowed)
                    {
                        throw AppException.Denied("authz.denied.escalation");
                    }
                }
            }
            var held = (await provisioning.GetUserAssignmentsAsync(user, ct))
                .Any(a => a.RoleKey == AreaRoles[area] && a.ScopeType == ScopeType.OrgSubtree && a.ScopeId == unit);
            var assignment = await provisioning.AssignBuiltInRoleAsync(user, AreaRoles[area], ScopeType.OrgSubtree, unit, actor, ct);
            await session.ExecAsync(
                """
                insert into experience.delegation (tenant_id, org_unit_id, user_id, area, assignment_id, owned, delegated_by, delegated_at)
                values (platform.current_tenant_id(), @unit, @user, @area, @assignment, @owned, @actor, @now)
                """, new { unit, user, area, assignment, owned = !held, actor, now = clock.UtcNow }, ct);
        }
        foreach (var row in current.Where(r => !areas.Contains(r.Area)))
        {
            if (row.Owned)
            {
                await provisioning.RevokeSystemAssignmentAsync(row.Assignment, "experience.delegation_removed", ct);
            }
            await session.ExecAsync("delete from experience.delegation where org_unit_id = @unit and user_id = @user and area = @area",
                new { unit, user, area = row.Area }, ct);
        }
        var before = current.Select(r => r.Area).Order(StringComparer.Ordinal).ToList();
        var after = areas.Order(StringComparer.Ordinal).ToList();
        if (!before.SequenceEqual(after))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceDelegationChanged, "experience_delegation", $"{unit}:{user}", TargetOrgUnitId: unit,
                Changes: new { delegatedTo = user, scope = unit, before, after }), ct);
        }
        var rows = await RowsAsync((unit, user), ct);
        return rows.Count == 0 ? null : View(unit, user, rows);
    }

    private async Task<IReadOnlyList<Row>> RowsAsync((Guid Unit, Guid User)? key, CancellationToken ct) =>
        await session.QueryAsync(
            "select org_unit_id, user_id, area, assignment_id, owned, delegated_by, delegated_at from experience.delegation"
            + (key is null ? "" : " where org_unit_id = @unit and user_id = @user"),
            key is { } k ? new { unit = k.Unit, user = k.User } : null,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetBoolean(4), r.GetGuid(5), r.GetFieldValue<DateTimeOffset>(6)), ct);

    private static DelegationView View(Guid unit, Guid user, IReadOnlyList<Row> rows) =>
        new(unit, user, rows.Select(r => r.Area).Order(StringComparer.Ordinal).ToList(), rows.MaxBy(r => r.At)!.By, rows.Max(r => r.At));
}
