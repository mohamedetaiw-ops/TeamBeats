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
    string Via,
    GrantCondition? Condition = null);

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
    IReadOnlyList<AppliedRule> Rules,
    IReadOnlyDictionary<string, string>? Attributes = null)
{
    /// <summary>User attributes for conditions (spec 004 section 6). Absent keys fail closed.</summary>
    public IReadOnlyDictionary<string, string> UserAttributes { get; } = Attributes ?? new Dictionary<string, string>();

    /// <summary>Request attributes for conditions (spec 026), set per request on the current user's grants only, never cached.</summary>
    public IReadOnlyDictionary<string, string> RequestAttributes { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The grants of an application app (spec 035 FR-011). An app is not a member of the tenant, so what every member may
    /// view is not open to it: it sees only what its own roles cover, at their org scope (section 7 sibling cases).
    /// </summary>
    public bool App { get; init; }

    public static EffectiveGrants None(Guid userId) =>
        new(userId, false, [], new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), []);
}

/// <summary>
/// Loads effective grants. Cached per instance by (tenant, user, grants version, hierarchy version), so any grant,
/// hierarchy or membership change is visible on the next request; the TTL bounds staleness to 60 seconds (FR-008),
/// which also covers user attribute changes from the directory or HR sync (spec 004 US7 scenario 6).
/// </summary>
public sealed class EffectiveGrantsLoader(
    IDbSession session,
    ITenantContext context,
    ITenantMemberships memberships,
    IOrganizationHierarchy hierarchy,
    IEnumerable<IUserAttributeProvider> attributeProviders,
    IMemoryCache cache)
{
    public static readonly TimeSpan MaxStaleness = TimeSpan.FromSeconds(60);

    private bool _changedInScope;

    /// <summary>
    /// Grants changed in this scope's transaction, which is not committed yet: from now on grants are read fresh and
    /// never shared, so a version that might still roll back is never cached for other requests.
    /// </summary>
    internal void GrantsChanged() => _changedInScope = true;

    public async Task<EffectiveGrants> LoadAsync(Guid userId, CancellationToken ct)
    {
        if (_changedInScope)
        {
            return await LoadUncachedAsync(userId, ct);
        }
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

    /// <summary>
    /// The grants of an application app (spec 035 FR-011): only the roles given to the app itself, never the default
    /// Employee role, groups, org memberships or inheritance rules, and never a permission an app may not hold, even if
    /// a role was changed behind the rules. Cached like a user's, so a removed role is gone within 60 seconds.
    /// </summary>
    public async Task<EffectiveGrants> LoadAppAsync(Guid appId, CancellationToken ct)
    {
        var tenantId = context.RequireTenant();
        var grantsVersion = await session.ScalarAsync<long?>("select version from authz.grants_version", null, ct) ?? 0;
        var hierarchyVersion = await hierarchy.GetVersionAsync(ct);
        var key = (tenantId, "app", appId, grantsVersion, hierarchyVersion);
        if (!_changedInScope && cache.TryGetValue(key, out EffectiveGrants? cached) && cached is not null)
        {
            return cached;
        }
        var rows = await session.QueryAsync(
            """
            select a.id, r.key, a.scope_type, a.scope_id, a.scope_resource_type,
                   array(select permission_key from authz.role_permission p where p.role_id = r.id) as permissions
            from authz.role_assignment a join authz.role r on r.id = a.role_id
            where a.revoked_at is null and a.subject_type = 'app' and a.subject_id = @appId and r.assignable_to_apps
            """, new { appId },
            r => (Id: r.GetGuid(0), Role: r.GetString(1), Scope: Sql.ParseEnum<ScopeType>(r.GetString(2)), ScopeId: r.Get<Guid?>("scope_id"),
                ScopeResourceType: r.Get<string>("scope_resource_type"), Permissions: r.GetFieldValue<string[]>(5)), ct);
        var grants = new List<Grant>();
        foreach (var x in rows)
        {
            IReadOnlySet<Guid> units = x.Scope switch
            {
                ScopeType.OrgUnit => new HashSet<Guid> { x.ScopeId!.Value },
                ScopeType.OrgSubtree => await hierarchy.GetSubtreesAsync([x.ScopeId!.Value], ct),
                _ => new HashSet<Guid>(),
            };
            grants.Add(new Grant(x.Id, x.Role, x.Scope, x.ScopeId, x.ScopeResourceType,
                x.Permissions.Where(p => !Permissions.ExcludedFromApps(p)).ToHashSet(StringComparer.Ordinal), units, "app"));
        }
        var loaded = new EffectiveGrants(appId, true, grants, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), []) { App = true };
        if (!_changedInScope)
        {
            cache.Set(key, loaded, MaxStaleness);
        }
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
                   array(select permission_key from authz.role_permission p where p.role_id = r.id) as permissions,
                   a.condition::text as condition
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
                Condition = GrantCondition.FromStored(r.Get<string>("condition")),
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
            x.Subject == "group" ? "group" : "direct", x.Condition)).ToList();

        // Default Employee role at tenant scope, unless removed for this membership type. Guests never receive it
        // (spec 004 section 6), whatever their policy row says.
        var policies = await memberships.GetMembershipTypePoliciesAsync(ct);
        if (membership.MembershipType != MembershipTypes.Guest
            && (!policies.TryGetValue(membership.MembershipType, out var defaultEmployee) || defaultEmployee))
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

        return new EffectiveGrants(userId, true, grants, groups, memberUnits, withAncestors, rules,
            await LoadAttributesAsync(userId, membership.MembershipType, ct));
    }

    /// <summary>
    /// User attributes from systems of record only: membership type from tenancy, the rest from registered providers
    /// (directory, HR sync). A provider cannot set membership type or a key outside the user catalogue.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadAttributesAsync(Guid userId, string membershipType, CancellationToken ct)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConditionAttributes.MembershipType] = membershipType,
        };
        foreach (var provider in attributeProviders)
        {
            foreach (var (key, value) in await provider.GetAsync(userId, ct))
            {
                if (ConditionAttributes.All.TryGetValue(key, out var a) && a.Source == AttributeSource.User
                    && !string.IsNullOrWhiteSpace(value) && !attributes.ContainsKey(key))
                {
                    attributes[key] = value.Trim();
                }
            }
        }
        return attributes;
    }
}
