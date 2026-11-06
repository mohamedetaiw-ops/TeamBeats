using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

public sealed record AddMembershipRequest(Guid UserId, string Type, DateTimeOffset? ValidFrom);

public sealed record OrgMembershipView(Guid Id, Guid UserId, Guid OrgUnitId, string Type, bool IsPrimary, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

/// <summary>Organization memberships (spec 002 US3). Exactly one primary per user while any are active.</summary>
public sealed class OrgMembershipService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    OrgHierarchyService hierarchy,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public static readonly IReadOnlySet<string> Types = new HashSet<string> { "primary", "secondary", "secondment", "other" };

    public async Task<IReadOnlyList<OrgMembershipView>> ListForUnitAsync(Guid unitId, CancellationToken ct)
    {
        _ = await hierarchy.LoadAsync(unitId, ct) ?? throw AppException.NotFound();
        await authz.DemandVisibleAsync(Permissions.UsersView, AuthzTarget.OrgUnit(unitId), ct);
        return await session.QueryAsync(
            "select id, user_id, org_unit_id, type, is_primary, valid_from, valid_to from tenancy.org_membership where org_unit_id = @u order by valid_from",
            new { u = unitId }, Map, ct);
    }

    public async Task<IReadOnlyList<OrgMembershipView>> ListForUserAsync(Guid userId, CancellationToken ct) =>
        await session.QueryAsync(
            "select id, user_id, org_unit_id, type, is_primary, valid_from, valid_to from tenancy.org_membership where user_id = @u order by valid_from",
            new { u = userId }, Map, ct);

    public async Task<OrgMembershipView> AddAsync(Guid unitId, AddMembershipRequest request, CancellationToken ct)
    {
        var unit = await hierarchy.LoadAsync(unitId, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.OrgMembershipManage, AuthzTarget.OrgUnit(unitId), ct);
        if (unit.Status != "active")
        {
            throw AppException.Validation("orgUnitId", "validation.org.unit_inactive");
        }
        if (!Types.Contains(request.Type ?? string.Empty))
        {
            throw AppException.Validation("type", "validation.membership.type");
        }

        // FR-006: an organization membership requires an active tenant membership. A user from another tenant is
        // invisible here (RLS) and gets the same answer.
        var activeTenantMember = await session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.tenant_membership where user_id = @u and status = 'active')", new { u = request.UserId }, ct);
        if (!activeTenantMember)
        {
            throw AppException.Validation("userId", "validation.membership.no_tenant_membership");
        }

        var duplicate = await session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.org_membership where user_id = @u and org_unit_id = @unit and valid_to is null)",
            new { u = request.UserId, unit = unitId }, ct);
        if (duplicate)
        {
            throw AppException.Conflict("conflict.membership.exists");
        }

        return await AddCoreAsync(unitId, request.UserId, request.Type!, request.ValidFrom, null, ct);
    }

    /// <summary>Adds a membership after the caller has authorized and validated it. A directory membership is always primary.</summary>
    internal async Task<OrgMembershipView> AddCoreAsync(Guid unitId, Guid userId, string type, DateTimeOffset? from, Guid? directoryConnectionId, CancellationToken ct)
    {
        var request = new AddMembershipRequest(userId, type, from);
        var hasActive = await session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.org_membership where user_id = @u and valid_to is null)", new { u = request.UserId }, ct);
        var makePrimary = request.Type == "primary" || !hasActive;
        if (makePrimary && hasActive)
        {
            await DemoteCurrentPrimaryAsync(request.UserId, ct);
        }

        var id = Ids.New();
        var validFrom = request.ValidFrom ?? clock.UtcNow;
        await session.ExecAsync(
            """
            insert into tenancy.org_membership (tenant_id, id, user_id, org_unit_id, type, is_primary, valid_from, owner, owner_connection_id)
            values (@t, @id, @u, @unit, @type, @primary, @from, @owner, @conn)
            """,
            new
            {
                t = context.RequireTenant(), id, u = request.UserId, unit = unitId, type = makePrimary ? "primary" : request.Type, primary = makePrimary, from = validFrom,
                owner = directoryConnectionId is null ? OrgOwners.Product : OrgOwners.Directory, conn = directoryConnectionId,
            }, ct);
        if (makePrimary)
        {
            await session.ExecAsync(
                "update tenancy.tenant_membership set preferred_org_unit_id = @unit where user_id = @u",
                new { u = request.UserId, unit = unitId }, ct);
        }
        await hierarchy.BumpVersionAsync(ct);

        await audit.WriteAsync(new AuditEntry(AuditActions.OrgMembershipAdded, "org_membership", id.ToString(), TargetOrgUnitId: unitId,
            Changes: new { userId = request.UserId, type = request.Type, isPrimary = makePrimary, owner = directoryConnectionId is null ? OrgOwners.Product : OrgOwners.Directory }), ct);
        if (makePrimary && hasActive)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.OrgMembershipPrimaryChanged, "org_membership", id.ToString(), TargetOrgUnitId: unitId,
                Changes: new { userId = request.UserId, newPrimaryUnitId = unitId }), ct);
        }
        await outbox.EnqueueAsync(new OrganizationMembershipChanged(request.UserId, unitId, "added"), ct);
        return new OrgMembershipView(id, request.UserId, unitId, makePrimary ? "primary" : request.Type!, makePrimary, validFrom, null);
    }

    public async Task EndAsync(Guid membershipId, CancellationToken ct)
    {
        var m = await session.QuerySingleOrDefaultAsync(
            "select id, user_id, org_unit_id, type, is_primary, valid_from, valid_to from tenancy.org_membership where id = @id",
            new { id = membershipId }, Map, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.OrgMembershipManage, AuthzTarget.OrgUnit(m.OrgUnitId), ct);
        if (m.ValidTo is not null)
        {
            return;
        }
        var owner = await session.ScalarAsync<string>("select owner from tenancy.org_membership where id = @id", new { id = membershipId }, ct);
        if (owner == OrgOwners.Directory)
        {
            throw AppException.Conflict("conflict.org.directory_owned");
        }
        await EndInternalAsync(m, ct);
    }

    /// <summary>Ends a directory-owned membership; history is kept (spec 002 US5 scenario 5).</summary>
    internal async Task EndFromDirectoryAsync(Guid membershipId, CancellationToken ct)
    {
        var m = await session.QuerySingleOrDefaultAsync(
            "select id, user_id, org_unit_id, type, is_primary, valid_from, valid_to from tenancy.org_membership where id = @id",
            new { id = membershipId }, Map, ct);
        if (m is { ValidTo: null })
        {
            await EndInternalAsync(m, ct);
        }
    }

    internal async Task HandleDeactivationAsync(IReadOnlyList<Guid> units, string action, Guid? moveTo, CancellationToken ct)
    {
        if (action == "move")
        {
            if (moveTo is not { } target)
            {
                throw AppException.Validation("moveMembershipsTo", "validation.required");
            }
            var targetUnit = await hierarchy.LoadAsync(target, ct) ?? throw AppException.Validation("moveMembershipsTo", "validation.org.parent_not_found");
            if (targetUnit.Status != "active" || units.Contains(target))
            {
                throw AppException.Validation("moveMembershipsTo", "validation.org.unit_inactive");
            }
            await authz.DemandAsync(Permissions.OrgMembershipManage, AuthzTarget.OrgUnit(target), ct);
        }

        var active = await session.QueryAsync(
            "select id, user_id, org_unit_id, type, is_primary, valid_from, valid_to from tenancy.org_membership where org_unit_id = any(@ids) and valid_to is null",
            new { ids = units.ToArray() }, Map, ct);
        foreach (var m in active)
        {
            await EndInternalAsync(m, ct);
            if (action == "move")
            {
                var alreadyThere = await session.ScalarAsync<bool>(
                    "select exists (select 1 from tenancy.org_membership where user_id = @u and org_unit_id = @unit and valid_to is null)",
                    new { u = m.UserId, unit = moveTo }, ct);
                if (!alreadyThere)
                {
                    await AddAsync(moveTo!.Value, new AddMembershipRequest(m.UserId, m.IsPrimary ? "primary" : m.Type, null), ct);
                }
            }
        }
    }

    private async Task EndInternalAsync(OrgMembershipView m, CancellationToken ct)
    {
        await session.ExecAsync("update tenancy.org_membership set valid_to = @now, is_primary = false where id = @id",
            new { id = m.Id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgMembershipEnded, "org_membership", m.Id.ToString(), TargetOrgUnitId: m.OrgUnitId,
            Changes: new { userId = m.UserId, wasPrimary = m.IsPrimary }), ct);

        if (m.IsPrimary)
        {
            // Keep exactly one primary while any memberships remain: promote the oldest remaining active one.
            var next = await session.QuerySingleOrDefaultAsync(
                "select id, org_unit_id from tenancy.org_membership where user_id = @u and valid_to is null order by valid_from, id limit 1",
                new { u = m.UserId }, r => (Id: r.GetGuid(0), Unit: r.GetGuid(1)), ct);
            if (next != default)
            {
                await session.ExecAsync("update tenancy.org_membership set is_primary = true, type = 'primary' where id = @id", new { id = next.Id }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.OrgMembershipPrimaryChanged, "org_membership", next.Id.ToString(),
                    TargetOrgUnitId: next.Unit, Changes: new { userId = m.UserId, newPrimaryUnitId = next.Unit }), ct);
            }
            await session.ExecAsync("update tenancy.tenant_membership set preferred_org_unit_id = @unit where user_id = @u",
                new { u = m.UserId, unit = next == default ? (Guid?)null : next.Unit }, ct);
        }

        await hierarchy.BumpVersionAsync(ct);
        await outbox.EnqueueAsync(new OrganizationMembershipChanged(m.UserId, m.OrgUnitId, "ended"), ct);
    }

    private Task DemoteCurrentPrimaryAsync(Guid userId, CancellationToken ct) =>
        session.ExecAsync(
            "update tenancy.org_membership set is_primary = false, type = case when type = 'primary' then 'secondary' else type end where user_id = @u and is_primary and valid_to is null",
            new { u = userId }, ct);

    private static OrgMembershipView Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetBoolean(4), r.GetFieldValue<DateTimeOffset>(5), r.Get<DateTimeOffset?>("valid_to"));
}
