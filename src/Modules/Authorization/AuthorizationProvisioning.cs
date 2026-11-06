using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Authorization;

/// <summary>Seeding and system assignments used by tenant creation and first sign-in.</summary>
public sealed class AuthorizationProvisioning(IDbSession session, ITenantContext context, AssignmentStore store, IAuditWriter audit, Messaging.IOutbox outbox)
    : IAuthorizationProvisioning
{
    public async Task SeedTenantRolesAsync(CancellationToken ct = default)
    {
        var tenantId = context.RequireTenant();
        foreach (var role in BuiltInRoleDefinitions.All)
        {
            var id = Ids.New();
            await session.ExecAsync(
                "insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin) values (@t, @id, @k, true, @d, @da)",
                new { t = tenantId, id, k = role.Key, d = role.Delegable, da = role.DelegatedAdmin }, ct);
            foreach (var permission in role.Permissions)
            {
                await session.ExecAsync("insert into authz.role_permission (tenant_id, role_id, permission_key) values (@t, @r, @p)",
                    new { t = tenantId, r = id, p = permission }, ct);
            }
        }
        await session.ExecAsync("insert into authz.grants_version (tenant_id, version) values (@t, 1) on conflict do nothing", new { t = tenantId }, ct);
    }

    public async Task<Guid> AssignBuiltInRoleAsync(Guid userId, string builtInRoleKey, ScopeType scopeType, Guid? scopeId, Guid? grantedBy, CancellationToken ct = default)
    {
        var roleId = await session.ScalarAsync<Guid?>("select id from authz.role where key = @k and is_builtin", new { k = builtInRoleKey }, ct)
            ?? throw new InvalidOperationException($"Built-in role {builtInRoleKey} is not seeded.");
        var existing = await session.ScalarAsync<Guid?>(
            """
            select id from authz.role_assignment where subject_type = 'user' and subject_id = @u and role_id = @r
              and scope_type = @s and scope_id is not distinct from @sid and revoked_at is null
            limit 1
            """, new { u = userId, r = roleId, s = scopeType, sid = scopeId }, ct);
        if (existing is { } found)
        {
            return found;
        }

        var id = Ids.New();
        await store.InsertAssignmentAsync(id, "user", userId, roleId, scopeType, scopeId, null, grantedBy, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleAssigned, "role_assignment", id.ToString(),
            TargetOrgUnitId: scopeType is ScopeType.OrgUnit or ScopeType.OrgSubtree ? scopeId : null,
            Changes: new { subjectType = "user", subjectId = userId, role = builtInRoleKey, scopeType = Sql.ToSnake(scopeType.ToString()), scopeId, system = grantedBy is null }), ct);
        await outbox.EnqueueAsync(new RoleAssignmentChanged(id, userId, builtInRoleKey, "assigned"), ct);
        return id;
    }

    public Task<bool> IsBuiltInRoleKeyAsync(string roleKey, CancellationToken ct = default) =>
        session.ScalarAsync<bool>("select exists (select 1 from authz.role where key = @k and is_builtin)", new { k = roleKey }, ct);

    public async Task<bool> RevokeSystemAssignmentAsync(Guid assignmentId, string reason, CancellationToken ct = default)
    {
        var a = await session.QuerySingleOrDefaultAsync(
            """
            select a.subject_id, r.key, a.scope_type, a.scope_id
            from authz.role_assignment a join authz.role r on r.id = a.role_id where a.id = @id and a.revoked_at is null
            """, new { id = assignmentId },
            r => (SubjectId: r.GetGuid(0), Role: r.GetString(1), Scope: Sql.ParseEnum<ScopeType>(r.GetString(2)), ScopeId: r.Get<Guid?>("scope_id")), ct);
        if (a == default)
        {
            return true;
        }
        if (a.Role == BuiltInRoles.TenantAdministrator && a.Scope == ScopeType.Tenant)
        {
            var remaining = await session.ScalarAsync<long>(
                """
                select count(*) from authz.role_assignment x join authz.role r on r.id = x.role_id
                where r.key = @k and x.scope_type = 'tenant' and x.revoked_at is null and x.id <> @id
                """, new { k = BuiltInRoles.TenantAdministrator, id = assignmentId }, ct);
            if (remaining == 0)
            {
                return false;
            }
        }

        await session.ExecAsync("update authz.role_assignment set revoked_at = now() where id = @id", new { id = assignmentId }, ct);
        await store.BumpAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleRevoked, "role_assignment", assignmentId.ToString(),
            TargetOrgUnitId: a.Scope is ScopeType.OrgUnit or ScopeType.OrgSubtree ? a.ScopeId : null,
            Changes: new { subjectType = "user", subjectId = a.SubjectId, role = a.Role, scopeType = Sql.ToSnake(a.Scope.ToString()), a.ScopeId, system = true, reason }), ct);
        await outbox.EnqueueAsync(new RoleAssignmentChanged(assignmentId, a.SubjectId, a.Role, "revoked"), ct);
        return true;
    }

    public async Task<IReadOnlyList<RoleAssignmentSummary>> GetUserAssignmentsAsync(Guid userId, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select a.id, r.key, a.scope_type, a.scope_id from authz.role_assignment a join authz.role r on r.id = a.role_id
            where a.subject_type = 'user' and a.subject_id = @u and a.revoked_at is null order by a.created_at
            """, new { u = userId },
            r => new RoleAssignmentSummary(r.GetGuid(0), r.GetString(1), Sql.ParseEnum<ScopeType>(r.GetString(2)), r.Get<Guid?>("scope_id")), ct);

    public async Task<bool> IsLastTenantAdministratorAsync(Guid userId, CancellationToken ct = default)
    {
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where a.subject_id = @u) as mine, count(*) as total
            from authz.role_assignment a join authz.role r on r.id = a.role_id
            where r.key = @k and a.scope_type = 'tenant' and a.revoked_at is null and a.subject_type = 'user'
            """, new { u = userId, k = BuiltInRoles.TenantAdministrator }, r => (Mine: r.GetInt64(0), Total: r.GetInt64(1)), ct);
        return counts.Mine > 0 && counts.Total == counts.Mine;
    }
}
