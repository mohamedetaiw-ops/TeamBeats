using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Sso;

/// <summary>
/// Applies identity provider groups to roles at sign-in (spec 003 US2 scenario 6). Roles granted by a mapping are
/// tracked, so leaving the group removes them at the next sign-in. Roles an administrator assigned are never removed,
/// and a role the user already held is not taken over by a mapping. Unknown groups are ignored, logged at debug only.
/// </summary>
public sealed class GroupRoleSync(IDbSession session, ITenantContext context, IAuthorizationProvisioning authz, ILogger<GroupRoleSync> logger)
{
    public async Task ApplyAsync(Guid userId, IReadOnlyList<string> groups, IReadOnlyList<GroupRoleMapping> map, CancellationToken ct)
    {
        var memberOf = new HashSet<string>(groups.Select(g => g.ToLowerInvariant()), StringComparer.Ordinal);
        var desired = map.Where(m => memberOf.Contains(m.GroupId))
            .GroupBy(m => Key(m.RoleKey, m.ScopeType, m.ScopeId))
            .ToDictionary(g => g.Key, g => g.First());
        var unknown = memberOf.Count(g => map.All(m => m.GroupId != g));
        if (unknown > 0)
        {
            logger.LogDebug("Ignored {Count} identity provider groups without a role mapping", unknown);
        }

        var tracked = await session.QueryAsync(
            "select assignment_id, role_key, scope_type, scope_id from identity.group_role_grant where user_id = @u",
            new { u = userId }, r => (Assignment: r.GetGuid(0), Key: Key(r.GetString(1), r.GetString(2), r.Get<Guid?>("scope_id"))), ct);
        var trackedKeys = tracked.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        var held = (await authz.GetUserAssignmentsAsync(userId, ct))
            .Select(a => Key(a.RoleKey, Sql.ToSnake(a.ScopeType.ToString()), a.ScopeId)).ToHashSet(StringComparer.Ordinal);

        foreach (var (key, mapping) in desired)
        {
            if (trackedKeys.Contains(key) || held.Contains(key))
            {
                continue;
            }
            var scope = Sql.ParseEnum<ScopeType>(mapping.ScopeType);
            var assignmentId = await authz.AssignBuiltInRoleAsync(userId, mapping.RoleKey, scope, mapping.ScopeId, null, ct);
            await session.ExecAsync(
                """
                insert into identity.group_role_grant (tenant_id, user_id, group_id, role_key, scope_type, scope_id, assignment_id)
                values (@t, @u, @g, @r, @s, @sid, @a)
                """,
                new { t = context.RequireTenant(), u = userId, g = mapping.GroupId, r = mapping.RoleKey, s = mapping.ScopeType, sid = mapping.ScopeId, a = assignmentId }, ct);
        }

        foreach (var (assignment, key) in tracked)
        {
            if (desired.ContainsKey(key))
            {
                continue;
            }
            if (await authz.RevokeSystemAssignmentAsync(assignment, "idp_group_removed", ct))
            {
                await session.ExecAsync("delete from identity.group_role_grant where assignment_id = @a", new { a = assignment }, ct);
            }
            else
            {
                logger.LogWarning("Kept a group-mapped tenant administrator role because it is the tenant's last administrator");
            }
        }
    }

    private static string Key(string role, string scopeType, Guid? scopeId) => $"{role}|{scopeType}|{scopeId}";
}
