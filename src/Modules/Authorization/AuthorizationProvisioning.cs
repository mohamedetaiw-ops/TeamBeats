using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Authorization;

/// <summary>Seeding and system assignments used by tenant creation and first sign-in.</summary>
public sealed class AuthorizationProvisioning(IDbSession session, ITenantContext context, AssignmentStore store, IAuditWriter audit, Messaging.IOutbox outbox,
    IServiceProvider services)
    : IAuthorizationProvisioning
{
    public async Task SeedTenantRolesAsync(CancellationToken ct = default)
    {
        var tenantId = context.RequireTenant();
        foreach (var role in BuiltInRoleDefinitions.All)
        {
            var id = Ids.New();
            await session.ExecAsync(
                "insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, is_delegated_admin, assignable_to_apps) values (@t, @id, @k, true, @d, @da, @apps)",
                new { t = tenantId, id, k = role.Key, d = role.Delegable, da = role.DelegatedAdmin, apps = role.AssignableToApps }, ct);
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

    public async Task<IReadOnlySet<string>> RolePermissionsAsync(string roleKey, CancellationToken ct = default) =>
        (await session.QueryAsync("select p.permission_key from authz.role r join authz.role_permission p on p.role_id = r.id where r.key = @k",
            new { k = roleKey }, r => r.GetString(0), ct)).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<Guid>> TenantHoldersAsync(string permission, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select distinct coalesce(m.user_id, a.subject_id)
            from authz.role_assignment a
            join authz.role_permission p on p.role_id = a.role_id and p.permission_key = @p
            left join authz.group_member m on a.subject_type = 'group' and m.group_id = a.subject_id
            where a.scope_type = 'tenant' and a.revoked_at is null and (a.subject_type = 'user' or m.user_id is not null)
            """, new { p = permission }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlyList<AdminAssignment>> AdminAssignmentsAsync(CancellationToken ct = default)
    {
        var rows = await session.QueryAsync(
            """
            select a.id, a.subject_id, r.key, a.scope_type, a.scope_id, array_agg(p.permission_key)
            from authz.role_assignment a
            join authz.role r on r.id = a.role_id
            join authz.role_permission p on p.role_id = a.role_id
            where a.subject_type = 'user' and a.revoked_at is null and a.scope_type in ('tenant', 'org_unit', 'org_subtree')
            group by a.id, a.subject_id, r.key, a.scope_type, a.scope_id
            order by a.subject_id, r.key, a.id
            """, null,
            r => (Row: new AdminAssignment(r.GetGuid(0), r.GetGuid(1), r.GetString(2), Sql.ParseEnum<ScopeType>(r.GetString(3)), r.Get<Guid?>("scope_id")),
                Permissions: r.GetFieldValue<string[]>(5)), ct);
        return rows.Where(r => r.Permissions.Any(Permissions.Administers)).Select(r => r.Row).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ResourceHoldersAsync(string permission, string resourceType, Guid resourceId, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select distinct a.subject_id
            from authz.role_assignment a
            join authz.role_permission p on p.role_id = a.role_id and p.permission_key = @p
            where a.scope_type = 'resource' and a.scope_resource_type = @rt and a.scope_id = @rid and a.subject_type = 'user' and a.revoked_at is null
            """, new { p = permission, rt = resourceType, rid = resourceId }, r => r.GetGuid(0), ct);

    public async Task<bool> UserHoldsAsync(Guid userId, string permission, AuthzTarget target, CancellationToken ct = default)
    {
        context.RequireTenant();
        // Resolved here: the policy service depends on tenancy, which depends on this class for tenant creation.
        var authz = services.GetRequiredService<AuthorizationService>();
        return (await authz.EvaluateAsync(await authz.GrantsForAsync(userId, ct), permission, target, ct)).Allowed;
    }

    public async Task<IReadOnlySet<Guid>> GroupIdsOfAsync(Guid userId, CancellationToken ct = default) =>
        (await session.QueryAsync("select group_id from authz.group_member where user_id = @userId", new { userId }, r => r.GetGuid(0), ct)).ToHashSet();

    public async Task<IReadOnlyList<Guid>> GroupMembersAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct = default) =>
        groupIds.Count == 0 ? [] : await session.QueryAsync(
            "select distinct user_id from authz.group_member where group_id = any(@groupIds) order by user_id", new { groupIds = groupIds.ToArray() }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ExistingGroupsAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct = default) =>
        groupIds.Count == 0 ? new HashSet<Guid>()
            : (await session.QueryAsync("select id from authz.user_group where id = any(@ids)", new { ids = groupIds.ToArray() }, r => r.GetGuid(0), ct)).ToHashSet();

    public Task<bool> IsBuiltInRoleKeyAsync(string roleKey, CancellationToken ct = default) =>
        session.ScalarAsync<bool>("select exists (select 1 from authz.role where key = @k and is_builtin)", new { k = roleKey }, ct);

    public async Task SetResourceRoleAsync(Guid userId, string resourceType, Guid resourceId, string? builtInRoleKey, Guid? grantedBy, CancellationToken ct = default)
    {
        var current = await session.QueryAsync(
            """
            select a.id, r.key from authz.role_assignment a join authz.role r on r.id = a.role_id
            where a.subject_type = 'user' and a.subject_id = @u and a.scope_type = 'resource' and a.scope_id = @rid and a.scope_resource_type = @rt
              and a.revoked_at is null and r.is_builtin
            """, new { u = userId, rid = resourceId, rt = resourceType }, r => (Id: r.GetGuid(0), Role: r.GetString(1)), ct);
        if (builtInRoleKey is not null && current.Count == 1 && current[0].Role == builtInRoleKey)
        {
            return;
        }
        foreach (var (id, role) in current)
        {
            await session.ExecAsync("update authz.role_assignment set revoked_at = now() where id = @id", new { id }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.RoleRevoked, "role_assignment", id.ToString(),
                Changes: new { subjectType = "user", subjectId = userId, role, scopeType = "resource", scopeId = resourceId, scopeResourceType = resourceType, system = true }), ct);
            await outbox.EnqueueAsync(new RoleAssignmentChanged(id, userId, role, "revoked"), ct);
        }
        if (builtInRoleKey is null)
        {
            if (current.Count > 0)
            {
                await store.BumpAsync(ct);
                ForgetGrants();
            }
            return;
        }
        var roleId = await session.ScalarAsync<Guid?>("select id from authz.role where key = @k and is_builtin", new { k = builtInRoleKey }, ct)
            ?? throw new InvalidOperationException($"Built-in role {builtInRoleKey} is not seeded.");
        var assignment = Ids.New();
        await store.InsertAssignmentAsync(assignment, "user", userId, roleId, ScopeType.Resource, resourceId, resourceType, grantedBy, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleAssigned, "role_assignment", assignment.ToString(),
            Changes: new { subjectType = "user", subjectId = userId, role = builtInRoleKey, scopeType = "resource", scopeId = resourceId, scopeResourceType = resourceType, system = true }), ct);
        await outbox.EnqueueAsync(new RoleAssignmentChanged(assignment, userId, builtInRoleKey, "assigned"), ct);
        ForgetGrants();
    }

    private void ForgetGrants()
    {
        services.GetRequiredService<EffectiveGrantsLoader>().GrantsChanged();
        services.GetRequiredService<AuthorizationService>().Forget();
    }

    public async Task ClearResourceRolesAsync(string resourceType, Guid resourceId, CancellationToken ct = default)
    {
        var revoked = await session.ExecAsync(
            "update authz.role_assignment set revoked_at = now() where scope_type = 'resource' and scope_id = @rid and scope_resource_type = @rt and revoked_at is null",
            new { rid = resourceId, rt = resourceType }, ct);
        if (revoked > 0)
        {
            await store.BumpAsync(ct);
            ForgetGrants();
        }
    }

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

    public async Task AddAclEntryAsync(string resourceType, Guid resourceId, string subjectType, Guid subjectId, string permission, CancellationToken ct = default)
    {
        if (subjectType is not ("user" or "group" or "org_unit" or "org_subtree") || !Permissions.All.Contains(permission))
        {
            throw new ArgumentException("Unknown ACL subject type or permission.");
        }
        await session.ExecAsync(
            """
            insert into authz.resource_acl (tenant_id, id, resource_type, resource_id, subject_type, subject_id, permission_key, granted_by)
            select @t, @id, @rt, @rid, @st, @sid, @p, @by
            where not exists (select 1 from authz.resource_acl where resource_type = @rt and resource_id = @rid and subject_type = @st
              and subject_id = @sid and permission_key = @p and condition is null)
            """, new { t = context.RequireTenant(), id = Ids.New(), rt = resourceType, rid = resourceId, st = subjectType, sid = subjectId, p = permission, by = context.ActorId }, ct);
        await AclChangedAsync(resourceType, resourceId, ct);
    }

    public async Task CopyAclAsync(string fromType, Guid fromId, string toType, Guid toId, CancellationToken ct = default)
    {
        await session.ExecAsync("delete from authz.resource_acl where resource_type = @t and resource_id = @id", new { t = toType, id = toId }, ct);
        await session.ExecAsync(
            """
            insert into authz.resource_acl (tenant_id, id, resource_type, resource_id, subject_type, subject_id, permission_key, granted_by, condition)
            select tenant_id, gen_random_uuid(), @tt, @tid, subject_type, subject_id, permission_key, @by, condition
            from authz.resource_acl where resource_type = @ft and resource_id = @fid
            """, new { ft = fromType, fid = fromId, tt = toType, tid = toId, by = context.ActorId }, ct);
        await AclChangedAsync(toType, toId, ct);
    }

    public async Task ClearAclAsync(string resourceType, Guid resourceId, CancellationToken ct = default)
    {
        await session.ExecAsync("delete from authz.resource_acl where resource_type = @t and resource_id = @id", new { t = resourceType, id = resourceId }, ct);
        await AclChangedAsync(resourceType, resourceId, ct);
    }

    private async Task AclChangedAsync(string resourceType, Guid resourceId, CancellationToken ct)
    {
        await store.BumpAsync(ct);
        await outbox.EnqueueAsync(new ResourcePermissionChanged(resourceType, resourceId), ct);
    }
}
