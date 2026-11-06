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

    public async Task<Guid> SyncDirectoryGroupAsync(string key, string name, IReadOnlyCollection<Guid> memberUserIds, CancellationToken ct = default)
    {
        var tenantId = context.RequireTenant();
        var existing = await session.QuerySingleOrDefaultAsync("select id, source, name from authz.user_group where key = @k", new { k = key },
            r => (Id: r.GetGuid(0), Source: r.GetString(1), Name: r.GetString(2)), ct);
        Guid groupId;
        if (existing == default)
        {
            groupId = Ids.New();
            await session.ExecAsync("insert into authz.user_group (tenant_id, id, key, name, source) values (@t, @id, @k, @n, 'idp')",
                new { t = tenantId, id = groupId, k = key, n = name }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.RoleChanged, "group", groupId.ToString(), Changes: new { created = key, source = "idp" }), ct);
        }
        else
        {
            if (existing.Source != "idp")
            {
                throw new InvalidOperationException("A product group cannot be changed by directory sync.");
            }
            groupId = existing.Id;
            if (existing.Name != name)
            {
                await session.ExecAsync("update authz.user_group set name = @n where id = @id", new { id = groupId, n = name }, ct);
            }
        }

        var current = (await session.QueryAsync("select user_id from authz.group_member where group_id = @g", new { g = groupId }, r => r.GetGuid(0), ct)).ToHashSet();
        var wanted = memberUserIds.ToHashSet();
        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        foreach (var u in added)
        {
            await session.ExecAsync("insert into authz.group_member (tenant_id, group_id, user_id) values (@t, @g, @u) on conflict do nothing",
                new { t = tenantId, g = groupId, u }, ct);
        }
        if (removed.Count > 0)
        {
            await session.ExecAsync("delete from authz.group_member where group_id = @g and user_id = any(@u)", new { g = groupId, u = removed.ToArray() }, ct);
        }
        if (added.Count > 0 || removed.Count > 0)
        {
            await store.BumpAsync(ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.RoleChanged, "group", groupId.ToString(),
                Changes: new { membersAdded = added.Count, membersRemoved = removed.Count, source = "idp" }), ct);
        }
        return groupId;
    }

    public async Task AssignBuiltInRoleToGroupAsync(Guid groupId, string builtInRoleKey, ScopeType scopeType, Guid? scopeId, CancellationToken ct = default)
    {
        var roleId = await session.ScalarAsync<Guid?>("select id from authz.role where key = @k and is_builtin", new { k = builtInRoleKey }, ct)
            ?? throw new InvalidOperationException($"Built-in role {builtInRoleKey} is not seeded.");
        var exists = await session.ScalarAsync<bool>(
            """
            select exists (select 1 from authz.role_assignment where subject_type = 'group' and subject_id = @g and role_id = @r
                           and scope_type = @s and scope_id is not distinct from @sid and revoked_at is null)
            """, new { g = groupId, r = roleId, s = scopeType, sid = scopeId }, ct);
        if (exists)
        {
            return;
        }
        var id = Ids.New();
        await store.InsertAssignmentAsync(id, "group", groupId, roleId, scopeType, scopeId, null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleAssigned, "role_assignment", id.ToString(),
            TargetOrgUnitId: scopeType is ScopeType.OrgUnit or ScopeType.OrgSubtree ? scopeId : null,
            Changes: new { subjectType = "group", subjectId = groupId, role = builtInRoleKey, scopeType = Sql.ToSnake(scopeType.ToString()), scopeId, system = true }), ct);
        await outbox.EnqueueAsync(new RoleAssignmentChanged(id, groupId, builtInRoleKey, "assigned"), ct);
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
