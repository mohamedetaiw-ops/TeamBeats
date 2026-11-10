using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

/// <summary>Implements the published Tenancy contracts. Other modules never read <c>tenancy.*</c> tables.</summary>
public sealed class TenancyContracts(
    IDbSession session,
    ITenantContext context,
    IAuthorizationProvisioning authzProvisioning) : IOrganizationHierarchy, ITenantMemberships
{
    public Task<bool> IsAncestorOrSelfAsync(Guid ancestorId, Guid unitId, CancellationToken ct = default) =>
        session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.org_closure where ancestor_id = @a and descendant_id = @d)",
            new { a = ancestorId, d = unitId }, ct);

    public async Task<IReadOnlyList<Guid>> GetAncestorsAndSelfAsync(Guid unitId, CancellationToken ct = default) =>
        await session.QueryAsync("select ancestor_id from tenancy.org_closure where descendant_id = @d order by depth",
            new { d = unitId }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetPathsAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default)
    {
        var ids = unitIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Guid>>();
        }
        var rows = await session.QueryAsync("select descendant_id, ancestor_id from tenancy.org_closure where descendant_id = any(@ids) order by descendant_id, depth desc",
            new { ids }, r => (Unit: r.GetGuid(0), Ancestor: r.GetGuid(1)), ct);
        return rows.GroupBy(r => r.Unit).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.Ancestor).ToList());
    }

    public async Task<IReadOnlySet<Guid>> GetSubtreesAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default)
    {
        var ids = unitIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new HashSet<Guid>();
        }
        return (await session.QueryAsync("select distinct descendant_id from tenancy.org_closure where ancestor_id = any(@ids)",
            new { ids }, r => r.GetGuid(0), ct)).ToHashSet();
    }

    public async Task<IReadOnlyList<OrgMembershipInfo>> GetActiveMembershipsAsync(Guid userId, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select m.id, m.org_unit_id, m.type, m.is_primary
            from tenancy.org_membership m join tenancy.org_unit u on u.id = m.org_unit_id
            where m.user_id = @u and m.valid_from <= now() and (m.valid_to is null or m.valid_to > now()) and u.status = 'active'
            """, new { u = userId }, r => new OrgMembershipInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetBoolean(3)), ct);

    public async Task<IReadOnlyList<UserOrgMembership>> ListActiveMembershipsAsync(CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select m.user_id, m.org_unit_id, m.is_primary
            from tenancy.org_membership m join tenancy.org_unit u on u.id = m.org_unit_id
            where m.valid_from <= now() and (m.valid_to is null or m.valid_to > now()) and u.status = 'active'
            """, null, r => new UserOrgMembership(r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2)), ct);

    public Task<bool> UnitExistsAsync(Guid unitId, CancellationToken ct = default) =>
        session.ScalarAsync<bool>("select exists (select 1 from tenancy.org_unit where id = @id)", new { id = unitId }, ct);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> GetNamesAsync(IEnumerable<Guid> unitIds,
        CancellationToken ct = default)
    {
        var rows = await session.QueryAsync(
            "select org_unit_id, locale, name from tenancy.org_unit_name where org_unit_id = any(@ids)",
            new { ids = unitIds.Distinct().ToArray() }, r => (Unit: r.GetGuid(0), Locale: r.GetString(1), Name: r.GetString(2)), ct);
        return rows.GroupBy(r => r.Unit).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(r => r.Locale, r => r.Name, StringComparer.Ordinal));
    }

    public async Task<IReadOnlySet<Guid>> GetMemberUserIdsAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default) =>
        (await session.QueryAsync(
            "select distinct user_id from tenancy.org_membership where org_unit_id = any(@ids) and valid_to is null",
            new { ids = unitIds.ToArray() }, r => r.GetGuid(0), ct)).ToHashSet();

    public async Task<long> GetVersionAsync(CancellationToken ct = default) =>
        await session.ScalarAsync<long?>("select version from tenancy.hierarchy_version", null, ct) ?? 0;

    public Task<TenantMembershipInfo?> GetAsync(Guid userId, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync(
            "select user_id, membership_type, status, preferred_org_unit_id from tenancy.tenant_membership where user_id = @u",
            new { u = userId },
            r => new TenantMembershipInfo(r.GetGuid(0), r.GetString(1), Sql.ParseEnum<MembershipStatus>(r.GetString(2)), r.Get<Guid?>("preferred_org_unit_id")),
            ct);

    public async Task<IReadOnlyDictionary<Guid, TenantMembershipInfo>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        (await session.QueryAsync(
            "select user_id, membership_type, status, preferred_org_unit_id from tenancy.tenant_membership where user_id = any(@ids)",
            new { ids = userIds.Distinct().ToArray() },
            r => new TenantMembershipInfo(r.GetGuid(0), r.GetString(1), Sql.ParseEnum<MembershipStatus>(r.GetString(2)), r.Get<Guid?>("preferred_org_unit_id")),
            ct)).ToDictionary(m => m.UserId);

    public async Task<IReadOnlyList<TenantMembershipInfo>> ListAsync(CancellationToken ct = default) =>
        await session.QueryAsync(
            "select user_id, membership_type, status, preferred_org_unit_id from tenancy.tenant_membership",
            null,
            r => new TenantMembershipInfo(r.GetGuid(0), r.GetString(1), Sql.ParseEnum<MembershipStatus>(r.GetString(2)), r.Get<Guid?>("preferred_org_unit_id")),
            ct);

    public async Task CreateAsync(Guid userId, string membershipType, CancellationToken ct = default)
    {
        if (!MembershipTypes.All.Contains(membershipType))
        {
            throw new ArgumentOutOfRangeException(nameof(membershipType));
        }
        await session.ExecAsync(
            "insert into tenancy.tenant_membership (tenant_id, user_id, membership_type, status) values (@t, @u, @type, 'active')",
            new { t = context.RequireTenant(), u = userId, type = membershipType }, ct);
        await session.ExecAsync("update tenancy.hierarchy_version set version = version + 1", null, ct);
    }

    public Task<bool> HasPendingAdminAppointmentAsync(string email, CancellationToken ct = default) =>
        session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.tenant_admin_appointment where invited_email = @e and status = 'pending')",
            new { e = TenantRules.NormalizeEmail(email) }, ct);

    public async Task<int> ClaimAdminAppointmentsAsync(string email, Guid userId, CancellationToken ct = default)
    {
        var claimed = await session.ExecAsync(
            """
            update tenancy.tenant_admin_appointment set status = 'active', user_id = @u, activated_at = now()
            where invited_email = @e and status = 'pending'
            """, new { e = TenantRules.NormalizeEmail(email), u = userId }, ct);
        if (claimed > 0)
        {
            await authzProvisioning.AssignBuiltInRoleAsync(userId, BuiltInRoles.TenantAdministrator, ScopeType.Tenant, null, null, ct);
        }
        return claimed;
    }

    public async Task SetStatusAsync(Guid userId, MembershipStatus status, CancellationToken ct = default)
    {
        var updated = await session.ExecAsync(
            "update tenancy.tenant_membership set status = @s, status_changed_at = now() where user_id = @u",
            new { u = userId, s = status }, ct);
        if (updated == 0)
        {
            throw Primitives.AppException.NotFound();
        }
        await session.ExecAsync("update tenancy.hierarchy_version set version = version + 1", null, ct);
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetMembershipTypePoliciesAsync(CancellationToken ct = default) =>
        (await session.QueryAsync("select membership_type, default_employee_role from tenancy.membership_type_policy", null,
            r => (r.GetString(0), r.GetBoolean(1)), ct)).ToDictionary(x => x.Item1, x => x.Item2);
}
