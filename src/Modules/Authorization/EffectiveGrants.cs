using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Authorization;

/// <summary>A role assignment as it applies to one user, with org subtrees already expanded.</summary>
public sealed record Grant(
    Guid? AssignmentId,
    string RoleKey,
    ScopeType ScopeType,
    Guid? ScopeId,
    string? ScopeResourceType,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<Guid> CoveredUnits,
    string Via);

/// <summary>Inheritance rule that applies to this user: members of <see cref="UnitId"/> may view resources restricted to its descendants.</summary>
public sealed record AppliedRule(Guid UnitId, string Rule, IReadOnlySet<Guid> Descendants);

/// <summary>Snapshot of everything that decides access for one user in one tenant at one grants/hierarchy version.</summary>
public sealed record EffectiveGrants(
    Guid UserId,
    bool ActiveMember,
    IReadOnlyList<Grant> Grants,
    IReadOnlySet<Guid> Groups,
    IReadOnlySet<Guid> MemberUnits,
    IReadOnlySet<Guid> MemberUnitsAndAncestors,
    IReadOnlyList<AppliedRule> Rules)
{
    public static EffectiveGrants None(Guid userId) =>
        new(userId, false, [], new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), []);
}

/// <summary>
/// Loads effective grants. Cached per instance by (tenant, user, grants version, hierarchy version), so any grant,
/// hierarchy or membership change is visible on the next request; the TTL bounds staleness to 60 seconds (FR-008).
/// </summary>
public sealed class EffectiveGrantsLoader(
    IDbSession session,
    ITenantContext context,
    ITenantMemberships memberships,
    IOrganizationHierarchy hierarchy,
    IMemoryCache cache)
{
    public static readonly TimeSpan MaxStaleness = TimeSpan.FromSeconds(60);

    public async Task<EffectiveGrants> LoadAsync(Guid userId, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var grantsVersion = await session.ScalarAsync<long?>("select version from authz.grants_version", null, ct) ?? 0;
        var hierarchyVersion = await hierarchy.GetVersionAsync(ct);
        var key = (tenantId, userId, grantsVersion, hierarchyVersion);
        if (cache.TryGetValue(key, out EffectiveGrants? cached) && cached is not null)
        {
            return cached;
        }

        var loaded = await LoadUncachedAsync(userId, ct);
        cache.Set(key, loaded, MaxStaleness);
        return loaded;
    }

    private async Task<EffectiveGrants> LoadUncachedAsync(Guid userId, CancellationToken ct)
    {
        // FR-002: every decision first verifies active tenant membership.
        var membership = await memberships.GetAsync(userId, ct);
        if (membership is null || membership.Status != MembershipStatus.Active)
        {
            return EffectiveGrants.None(userId);
        }

        var groups = (await session.QueryAsync("select group_id from authz.group_member where user_id = @u", new { u = userId },
            r => r.GetGuid(0), ct)).ToHashSet();

        var rows = await session.QueryAsync(
            """
            select a.id, r.key, a.scope_type, a.scope_id, a.scope_resource_type, a.subject_type,
                   array(select permission_key from authz.role_permission p where p.role_id = r.id) as permissions
            from authz.role_assignment a join authz.role r on r.id = a.role_id
            where a.revoked_at is null
              and ((a.subject_type = 'user' and a.subject_id = @u) or (a.subject_type = 'group' and a.subject_id = any(@groups)))
            """,
            new { u = userId, groups = groups.ToArray() },
            r => new
            {
                Id = r.GetGuid(0),
                Role = r.GetString(1),
                Scope = Sql.ParseEnum<ScopeType>(r.GetString(2)),
                ScopeId = r.Get<Guid?>("scope_id"),
                ScopeResourceType = r.Get<string>("scope_resource_type"),
                Subject = r.GetString(5),
                Permissions = r.GetFieldValue<string[]>(6),
            }, ct);

        var subtreeRoots = rows.Where(x => x.Scope == ScopeType.OrgSubtree && x.ScopeId is not null).Select(x => x.ScopeId!.Value).Distinct().ToList();
        var subtrees = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var root in subtreeRoots)
        {
            subtrees[root] = await hierarchy.GetSubtreesAsync([root], ct);
        }

        var grants = rows.Select(x => new Grant(
            x.Id, x.Role, x.Scope, x.ScopeId, x.ScopeResourceType, x.Permissions.ToHashSet(StringComparer.Ordinal),
            x.Scope switch
            {
                ScopeType.OrgUnit => new HashSet<Guid> { x.ScopeId!.Value },
                ScopeType.OrgSubtree => subtrees[x.ScopeId!.Value],
                _ => new HashSet<Guid>(),
            },
            x.Subject == "group" ? "group" : "direct")).ToList();

        // Default Employee role at tenant scope, unless removed for this membership type (spec 004 section 6).
        var policies = await memberships.GetMembershipTypePoliciesAsync(ct);
        if (!policies.TryGetValue(membership.MembershipType, out var defaultEmployee) || defaultEmployee)
        {
            var employeePermissions = await session.QueryAsync(
                "select p.permission_key from authz.role r join authz.role_permission p on p.role_id = r.id where r.key = @k",
                new { k = BuiltInRoles.Employee }, r => r.GetString(0), ct);
            grants.Add(new Grant(null, BuiltInRoles.Employee, ScopeType.Tenant, null, null,
                employeePermissions.ToHashSet(StringComparer.Ordinal), new HashSet<Guid>(), "default"));
        }

        var memberUnits = (await hierarchy.GetActiveMembershipsAsync(userId, ct)).Select(m => m.OrgUnitId).ToHashSet();

        var ruleUnits = memberUnits.Count == 0
            ? []
            : await session.QueryAsync(
                "select org_unit_id, rule from authz.inheritance_rule where enabled and org_unit_id = any(@units)",
                new { units = memberUnits.ToArray() }, r => (Unit: r.GetGuid(0), Rule: r.GetString(1)), ct);
        var rules = new List<AppliedRule>();
        foreach (var (unit, rule) in ruleUnits)
        {
            rules.Add(new AppliedRule(unit, rule, await hierarchy.GetSubtreesAsync([unit], ct)));
        }

        var withAncestors = new HashSet<Guid>();
        foreach (var unit in memberUnits)
        {
            withAncestors.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(unit, ct));
        }

        return new EffectiveGrants(userId, true, grants, groups, memberUnits, withAncestors, rules);
    }
}
