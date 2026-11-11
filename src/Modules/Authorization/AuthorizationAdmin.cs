using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Authorization;

/// <summary>A role as administrators see it. <c>Names</c> holds a custom role's display names by locale; built-in roles take theirs from the web resources.</summary>
public sealed record RoleView(Guid Id, string Key, bool IsBuiltIn, bool IsDelegable, IReadOnlyList<string> Permissions, IReadOnlyDictionary<string, string>? Names = null);

public sealed record DefineRoleRequest(string Key, IReadOnlyDictionary<string, string>? Names, IReadOnlyList<string> Permissions, bool Delegable);

/// <param name="Condition">Optional attribute condition that narrows the grant (spec 004 US7, FR-014 to FR-016).</param>
public sealed record AssignRoleRequest(string SubjectType, Guid SubjectId, string RoleKey, string ScopeType, Guid? ScopeId, string? ScopeResourceType,
    JsonElement? Condition = null);

public sealed record AssignmentView(Guid Id, string SubjectType, Guid SubjectId, string RoleKey, string ScopeType, Guid? ScopeId, DateTimeOffset CreatedAt,
    JsonElement? Condition = null);

/// <param name="Condition">Optional attribute condition that narrows the entry (spec 004 US7, FR-014 to FR-016).</param>
public sealed record AclEntryRequest(string SubjectType, Guid SubjectId, string Permission, JsonElement? Condition = null);

public sealed record InheritanceRuleRequest(Guid OrgUnitId, string Rule, bool Enabled);

public sealed record CreateGroupRequest(string Key, string Name);

public sealed record GroupMemberRequest(Guid UserId);

/// <param name="NotApplied">Grants that cover the target but whose condition did not hold, with the attribute values used.</param>
public sealed record ExplainResult(bool Allowed, IReadOnlyList<DecisionReason> Reasons, IReadOnlyList<DecisionReason> NotApplied);

/// <summary>Role, assignment, ACL, rule and group administration (spec 004 US1 to US6).</summary>
public sealed class AuthorizationAdmin(
    IDbSession session,
    ITenantContext context,
    AuthorizationService authz,
    IEntitlementService entitlements,
    ITenantMemberships memberships,
    IOrganizationHierarchy hierarchy,
    IEnumerable<IResourceDescriptorProvider> providers,
    IAuditWriter audit,
    IOutbox outbox,
    AssignmentStore store,
    IUserDirectory users)
{
    public async Task<IReadOnlyList<RoleView>> ListRolesAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.RolesView, ct))
        {
            throw AppException.Denied();
        }
        return await session.QueryAsync(
            """
            select r.id, r.key, r.is_builtin, r.is_delegable,
                   array(select permission_key from authz.role_permission p where p.role_id = r.id order by 1),
                   r.names::text
            from authz.role r order by r.is_builtin desc, r.key
            """, null, r => new RoleView(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetFieldValue<string[]>(4),
                r.IsDBNull(5) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(5))), ct);
    }

    public async Task<RoleView> DefineRoleAsync(DefineRoleRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.RolesManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AuthzCustomRoles, ct);

        var errors = new ValidationErrors();
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Key ?? string.Empty, "^[a-z][a-z0-9_]{1,59}$"))
        {
            errors.Add("key", "validation.role.key_format");
        }
        if (request.Permissions is null || request.Permissions.Count == 0)
        {
            errors.Add("permissions", "validation.role.permissions_required");
        }
        else if (request.Permissions.Any(p => !Permissions.All.Contains(p)))
        {
            errors.Add("permissions", "validation.role.unknown_permission");
        }
        errors.ThrowIfAny();

        // A custom role can only bundle permissions the definer holds (no escalation by role design).
        var mine = await authz.GrantsForAsync(context.RequireUser(), ct);
        if (request.Permissions!.Any(p => !Evaluator.Evaluate(mine, p, AuthzTarget.Tenant, []).Allowed))
        {
            throw AppException.Denied("authz.denied.escalation");
        }

        var id = Ids.New();
        try
        {
            await session.ExecAsync(
                "insert into authz.role (tenant_id, id, key, is_builtin, is_delegable, names) values (@t, @id, @k, false, @d, @n)",
                new { t = context.RequireTenant(), id, k = request.Key, d = request.Delegable, n = request.Names is null ? null : Jsonb.From(request.Names) }, ct);
        }
        catch (PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.role.key_taken");
        }
        foreach (var p in request.Permissions!.Distinct())
        {
            await session.ExecAsync("insert into authz.role_permission (tenant_id, role_id, permission_key) values (@t, @r, @p)",
                new { t = context.RequireTenant(), r = id, p }, ct);
        }
        await BumpAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleDefined, "role", id.ToString(),
            Changes: new { key = request.Key, permissions = request.Permissions, delegable = request.Delegable }), ct);
        return new RoleView(id, request.Key!, false, request.Delegable, request.Permissions!, request.Names);
    }

    public async Task<IReadOnlyList<AssignmentView>> ListAssignmentsAsync(Guid? userId, CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.RolesView, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var rows = await session.QueryAsync(
            """
            select a.id, a.subject_type, a.subject_id, r.key, a.scope_type, a.scope_id, a.created_at, a.condition::text as condition
            from authz.role_assignment a join authz.role r on r.id = a.role_id
            where a.revoked_at is null and (@u::uuid is null or (a.subject_type = 'user' and a.subject_id = @u))
            order by a.created_at
            """, new { u = userId },
            r => new AssignmentView(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.Get<Guid?>("scope_id"),
                r.GetFieldValue<DateTimeOffset>(6), ConditionJson(r.Get<string>("condition"))), ct);
        return coverage.WholeTenant ? rows : rows.Where(a => a.ScopeId is { } s && coverage.Units.Contains(s)).ToList();
    }

    /// <summary>
    /// Delegation rules (spec 004 US5, FR-007): the assigner must hold <c>authz.roles_assign</c> over the whole target scope,
    /// the role must be delegable unless assigned by someone with tenant-wide assign rights, and every permission in
    /// the role must already be held by the assigner over that scope.
    /// </summary>
    public async Task<AssignmentView> AssignAsync(AssignRoleRequest request, CancellationToken ct)
    {
        var assigner = context.RequireUser();
        if (!Enum.TryParse<ScopeType>(request.ScopeType?.Replace("_", string.Empty, StringComparison.Ordinal), true, out var scopeType))
        {
            throw AppException.Validation("scopeType", "validation.role.scope_type");
        }
        if (request.SubjectType is not ("user" or "group"))
        {
            throw AppException.Validation("subjectType", "validation.role.subject_type");
        }
        if (request.SubjectType == "user" && (await users.GetAsync(request.SubjectId, ct))?.IsTest == true)
        {
            // Evaluation personas get their roles only from their persona definition (spec 034 FR-006).
            throw AppException.Validation("subjectId", "validation.role.test_identity");
        }
        var conditionErrors = new ValidationErrors();
        var condition = await ParseConditionAsync(request.Condition, conditionErrors, "condition", ct);
        conditionErrors.ThrowIfAny();

        var role = await session.QuerySingleOrDefaultAsync(
            """
            select r.id, r.key, r.is_delegable, r.is_delegated_admin,
                   array(select permission_key from authz.role_permission p where p.role_id = r.id)
            from authz.role r where r.key = @k
            """, new { k = request.RoleKey },
            r => (Id: r.GetGuid(0), Key: r.GetString(1), Delegable: r.GetBoolean(2), DelegatedAdmin: r.GetBoolean(3), Permissions: r.GetFieldValue<string[]>(4)), ct);
        if (role == default)
        {
            throw AppException.Validation("roleKey", "validation.role.unknown");
        }

        // Scope must exist in this tenant; another tenant's unit is indistinguishable from a missing one.
        IReadOnlySet<Guid> targetUnits = new HashSet<Guid>();
        AuthzTarget probe = AuthzTarget.Tenant;
        switch (scopeType)
        {
            case ScopeType.Tenant when request.ScopeId is not null:
                throw AppException.Validation("scopeId", "validation.role.scope_id");
            case ScopeType.OrgUnit or ScopeType.OrgSubtree:
                if (request.ScopeId is not { } unitId || !await hierarchy.UnitExistsAsync(unitId, ct))
                {
                    throw AppException.Validation("scopeId", "validation.org.parent_not_found");
                }
                targetUnits = scopeType == ScopeType.OrgUnit ? new HashSet<Guid> { unitId } : await hierarchy.GetSubtreesAsync([unitId], ct);
                break;
            case ScopeType.Resource:
                var provider = providers.FirstOrDefault(p => p.ResourceType == request.ScopeResourceType);
                if (provider is null || request.ScopeId is not { } rid || await provider.DescribeAsync(rid, ct) is not { } descriptor)
                {
                    throw AppException.Validation("scopeId", "validation.role.resource_not_found");
                }
                probe = AuthzTarget.Resource(descriptor);
                break;
        }

        var mine = await authz.GrantsForAsync(assigner, ct);
        var assignCoverage = Evaluator.Coverage(mine, Permissions.RolesAssign);
        var coversScope = scopeType switch
        {
            ScopeType.Tenant or ScopeType.Resource => assignCoverage.WholeTenant,
            _ => assignCoverage.WholeTenant || targetUnits.All(assignCoverage.Units.Contains),
        };
        if (!coversScope)
        {
            throw AppException.Denied("authz.denied.scope");
        }
        if (!role.Delegable && !assignCoverage.WholeTenant)
        {
            throw AppException.Denied("authz.denied.not_delegable");
        }

        // Never grant a permission you do not hold over the target scope.
        foreach (var permission in role.Permissions)
        {
            var held = scopeType switch
            {
                ScopeType.Tenant => Evaluator.Coverage(mine, permission).WholeTenant,
                ScopeType.Resource => Evaluator.Evaluate(mine, permission, probe, []).Allowed || Evaluator.Coverage(mine, permission).WholeTenant,
                _ => Evaluator.Coverage(mine, permission) is var c && (c.WholeTenant || targetUnits.All(c.Units.Contains)),
            };
            if (!held)
            {
                throw AppException.Denied("authz.denied.escalation");
            }
        }

        if (role.DelegatedAdmin)
        {
            await entitlements.DemandAsync(Capabilities.OrgDelegatedAdmin, ct);
        }

        if (request.SubjectType == "user")
        {
            var member = await memberships.GetAsync(request.SubjectId, ct);
            if (member is null || member.Status != MembershipStatus.Active)
            {
                throw AppException.Validation("subjectId", "validation.membership.no_tenant_membership");
            }
        }
        else if (!await session.ScalarAsync<bool>("select exists (select 1 from authz.user_group where id = @g)", new { g = request.SubjectId }, ct))
        {
            throw AppException.Validation("subjectId", "validation.role.group_not_found");
        }

        var id = Ids.New();
        try
        {
            await InsertAssignmentAsync(id, request.SubjectType, request.SubjectId, role.Id, scopeType, request.ScopeId,
                scopeType == ScopeType.Resource ? request.ScopeResourceType : null, assigner, ct, condition);
        }
        catch (PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.role.already_assigned");
        }

        await AuditAssignmentAsync(AuditActions.RoleAssigned, id, request.SubjectType, request.SubjectId, role.Key, scopeType, request.ScopeId, ct,
            condition: condition);
        await outbox.EnqueueAsync(new RoleAssignmentChanged(id, request.SubjectId, role.Key, "assigned"), ct);
        return new AssignmentView(id, request.SubjectType, request.SubjectId, role.Key, Sql.ToSnake(scopeType.ToString()), request.ScopeId, DateTimeOffset.UtcNow,
            ConditionJson(condition?.ToCanonicalJson()));
    }

    public async Task RevokeAsync(Guid assignmentId, CancellationToken ct)
    {
        var a = await session.QuerySingleOrDefaultAsync(
            """
            select a.subject_type, a.subject_id, r.key, a.scope_type, a.scope_id
            from authz.role_assignment a join authz.role r on r.id = a.role_id where a.id = @id and a.revoked_at is null
            """, new { id = assignmentId },
            r => (SubjectType: r.GetString(0), SubjectId: r.GetGuid(1), Role: r.GetString(2), Scope: Sql.ParseEnum<ScopeType>(r.GetString(3)), ScopeId: r.Get<Guid?>("scope_id")), ct);
        if (a == default)
        {
            throw AppException.NotFound();
        }

        var mine = await authz.GrantsForAsync(context.RequireUser(), ct);
        var coverage = Evaluator.Coverage(mine, Permissions.RolesAssign);
        var units = a.ScopeId is { } s && a.Scope is ScopeType.OrgUnit or ScopeType.OrgSubtree
            ? (a.Scope == ScopeType.OrgUnit ? new HashSet<Guid> { s } : await hierarchy.GetSubtreesAsync([s], ct))
            : new HashSet<Guid>();
        var allowed = coverage.WholeTenant || (a.Scope is ScopeType.OrgUnit or ScopeType.OrgSubtree && units.All(coverage.Units.Contains));
        if (!allowed)
        {
            throw AppException.Denied("authz.denied.scope");
        }

        // A tenant must always keep at least one tenant administrator (spec 001 section 6).
        if (a.Role == BuiltInRoles.TenantAdministrator && a.Scope == ScopeType.Tenant)
        {
            var remaining = await session.ScalarAsync<long>(
                """
                select count(*) from authz.role_assignment x join authz.role r on r.id = x.role_id
                where r.key = @k and x.scope_type = 'tenant' and x.revoked_at is null and x.id <> @id
                """, new { k = BuiltInRoles.TenantAdministrator, id = assignmentId }, ct);
            if (remaining == 0)
            {
                throw AppException.Conflict("conflict.tenant.last_admin");
            }
        }

        await session.ExecAsync("update authz.role_assignment set revoked_at = now(), revoked_by = @by where id = @id",
            new { id = assignmentId, by = context.ActorId }, ct);
        await BumpAsync(ct);
        await AuditAssignmentAsync(AuditActions.RoleRevoked, assignmentId, a.SubjectType, a.SubjectId, a.Role, a.Scope, a.ScopeId, ct, bump: false);
        await outbox.EnqueueAsync(new RoleAssignmentChanged(assignmentId, a.SubjectId, a.Role, "revoked"), ct);
    }

    public async Task<IReadOnlyList<AclEntryRequest>> GetAclAsync(string resourceType, Guid resourceId, CancellationToken ct)
    {
        var (_, descriptor) = await DescribeAsync(resourceType, resourceId, ct);
        await authz.DemandVisibleAsync(ManagePermission(resourceType), AuthzTarget.Resource(descriptor), ct);
        return await session.QueryAsync(
            "select subject_type, subject_id, permission_key, condition::text as condition from authz.resource_acl where resource_type = @t and resource_id = @id order by created_at",
            new { t = resourceType, id = resourceId },
            r => new AclEntryRequest(r.GetString(0), r.GetGuid(1), r.GetString(2), ConditionJson(r.Get<string>("condition"))), ct);
    }

    public async Task ReplaceAclAsync(string resourceType, Guid resourceId, IReadOnlyList<AclEntryRequest> entries, CancellationToken ct)
    {
        var (provider, descriptor) = await DescribeAsync(resourceType, resourceId, ct);
        await authz.DemandVisibleAsync(provider.ManagePermission, AuthzTarget.Resource(descriptor), ct);

        var errors = new ValidationErrors();
        var normalized = new List<(AclEntryRequest Entry, GrantCondition? Condition)>();
        foreach (var e in entries)
        {
            normalized.Add((e, await ParseConditionAsync(e.Condition, errors, "condition", ct)));
            if (e.SubjectType is not ("user" or "group" or "org_unit" or "org_subtree"))
            {
                errors.Add("subjectType", "validation.acl.subject_type");
            }
            if (!Permissions.All.Contains(e.Permission))
            {
                errors.Add("permission", "validation.role.unknown_permission");
            }
            if (e.SubjectType is "org_unit" or "org_subtree" && !await hierarchy.UnitExistsAsync(e.SubjectId, ct))
            {
                errors.Add("subjectId", "validation.org.parent_not_found");
            }
        }
        // Identical entries collapse; the same subject and permission with two different conditions is ambiguous.
        var distinct = normalized
            .GroupBy(x => (x.Entry.SubjectType, x.Entry.SubjectId, x.Entry.Permission, Condition: x.Condition?.ToCanonicalJson()))
            .Select(x => x.First()).ToList();
        if (distinct.GroupBy(x => (x.Entry.SubjectType, x.Entry.SubjectId, x.Entry.Permission)).Any(x => x.Count() > 1))
        {
            errors.Add("condition", "validation.acl.conflicting_conditions");
        }
        errors.ThrowIfAny();

        await session.ExecAsync("delete from authz.resource_acl where resource_type = @t and resource_id = @id", new { t = resourceType, id = resourceId }, ct);
        foreach (var (e, condition) in distinct)
        {
            await session.ExecAsync(
                """
                insert into authz.resource_acl (tenant_id, id, resource_type, resource_id, subject_type, subject_id, permission_key, granted_by, condition)
                values (@t, @id, @rt, @rid, @st, @sid, @p, @by, @c)
                """,
                new
                {
                    t = context.RequireTenant(), id = Ids.New(), rt = resourceType, rid = resourceId, st = e.SubjectType, sid = e.SubjectId, p = e.Permission,
                    by = context.ActorId, c = condition is null ? null : new Jsonb(condition.ToCanonicalJson()),
                }, ct);
        }
        await BumpAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ResourcePermissionChanged, resourceType, resourceId.ToString(),
            TargetOrgUnitId: descriptor.OwningOrgUnitId,
            Changes: new
            {
                entries = distinct.Select(x => new
                {
                    x.Entry.SubjectType, x.Entry.SubjectId, x.Entry.Permission, condition = x.Condition?.ToJson(),
                }),
            }), ct);
        await outbox.EnqueueAsync(new ResourcePermissionChanged(resourceType, resourceId), ct);
    }

    public async Task SetInheritanceRuleAsync(InheritanceRuleRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.InheritanceRulesManage, AuthzTarget.Tenant, ct);
        if (request.Rule != "members_view_descendant_restricted")
        {
            throw AppException.Validation("rule", "validation.rule.unknown");
        }
        if (!await hierarchy.UnitExistsAsync(request.OrgUnitId, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.org.parent_not_found");
        }
        await session.ExecAsync(
            """
            insert into authz.inheritance_rule (tenant_id, org_unit_id, rule, enabled) values (@t, @u, @r, @e)
            on conflict (tenant_id, org_unit_id, rule) do update set enabled = excluded.enabled, updated_at = now()
            """, new { t = context.RequireTenant(), u = request.OrgUnitId, r = request.Rule, e = request.Enabled }, ct);
        await BumpAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.InheritanceRuleChanged, "inheritance_rule", $"{request.OrgUnitId}/{request.Rule}",
            TargetOrgUnitId: request.OrgUnitId, Changes: new { request.Rule, request.Enabled }), ct);
    }

    public async Task<Guid> CreateGroupAsync(CreateGroupRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.RolesManage, AuthzTarget.Tenant, ct);
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Name))
        {
            throw AppException.Validation("key", "validation.required");
        }
        var id = Ids.New();
        try
        {
            await session.ExecAsync("insert into authz.user_group (tenant_id, id, key, name, source) values (@t, @id, @k, @n, 'product')",
                new { t = context.RequireTenant(), id, k = request.Key, n = request.Name }, ct);
        }
        catch (PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.group.key_taken");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleChanged, "group", id.ToString(), Changes: new { created = request.Key }), ct);
        return id;
    }

    public async Task AddGroupMemberAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.RolesManage, AuthzTarget.Tenant, ct);
        var source = await session.ScalarAsync<string?>("select source from authz.user_group where id = @g", new { g = groupId }, ct)
            ?? throw AppException.NotFound();
        if (source != "product")
        {
            // Directory-owned groups are read-only in the product (spec 003 FR-014).
            throw AppException.Conflict("conflict.group.directory_owned");
        }
        var member = await memberships.GetAsync(userId, ct);
        if (member is null)
        {
            throw AppException.Validation("userId", "validation.membership.no_tenant_membership");
        }
        await session.ExecAsync(
            "insert into authz.group_member (tenant_id, group_id, user_id) values (@t, @g, @u) on conflict do nothing",
            new { t = context.RequireTenant(), g = groupId, u = userId }, ct);
        await BumpAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.RoleChanged, "group", groupId.ToString(), Changes: new { addedUser = userId }), ct);
    }

    /// <summary>Explains a decision for any user in the tenant (spec 004 US6).</summary>
    public async Task<ExplainResult> ExplainAsync(Guid userId, string permission, Guid? orgUnitId, string? resourceType, Guid? resourceId, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AccessExplain, AuthzTarget.Tenant, ct);
        AuthzTarget target = AuthzTarget.Tenant;
        if (resourceType is not null && resourceId is { } rid)
        {
            target = AuthzTarget.Resource((await DescribeAsync(resourceType, rid, ct)).Descriptor);
        }
        else if (orgUnitId is { } unit)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                throw AppException.NotFound();
            }
            target = AuthzTarget.OrgUnit(unit);
        }

        var grants = await authz.GrantsForAsync(userId, ct);
        var decision = await authz.EvaluateAsync(grants, permission, target, ct);
        return new ExplainResult(decision.Allowed, decision.Reasons, decision.NotApplied);
    }

    public async Task<object> MyAccessAsync(CancellationToken ct)
    {
        var g = await authz.GrantsForAsync(context.RequireUser(), ct);
        return new
        {
            grants = g.Grants.Select(x => new
            {
                role = x.RoleKey, scopeType = Sql.ToSnake(x.ScopeType.ToString()), scopeId = x.ScopeId, via = x.Via, condition = x.Condition?.ToJson(),
            }),
            // Permissions that apply without a resource. A grant whose condition needs resource attributes is listed above only.
            permissions = Evaluator.ApplicableWithoutResource(g).SelectMany(x => x.Permissions).Distinct().Order(),
            memberUnits = g.MemberUnits,
        };
    }

    private async Task<(IResourceDescriptorProvider Provider, ResourceDescriptor Descriptor)> DescribeAsync(string resourceType, Guid resourceId, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.ResourceType == resourceType) ?? throw AppException.NotFound();
        var descriptor = await provider.DescribeAsync(resourceId, ct) ?? throw AppException.NotFound();
        return (provider, descriptor);
    }

    private string ManagePermission(string resourceType) =>
        providers.FirstOrDefault(p => p.ResourceType == resourceType)?.ManagePermission ?? throw AppException.NotFound();

    internal Task InsertAssignmentAsync(Guid id, string subjectType, Guid subjectId, Guid roleId, ScopeType scope, Guid? scopeId,
        string? scopeResourceType, Guid? grantedBy, CancellationToken ct, GrantCondition? condition = null) =>
        store.InsertAssignmentAsync(id, subjectType, subjectId, roleId, scope, scopeId, scopeResourceType, grantedBy, ct, condition);

    /// <summary>Parses a condition and checks that org unit values exist in this tenant (another tenant's unit is unknown).</summary>
    private async Task<GrantCondition?> ParseConditionAsync(JsonElement? json, ValidationErrors errors, string field, CancellationToken ct)
    {
        var condition = GrantCondition.Parse(json, errors, field);
        if (condition is null)
        {
            return null;
        }
        foreach (var c in condition.Comparisons().Where(c => c.Attribute.Kind == AttributeKind.OrgUnit))
        {
            foreach (var value in c.Values)
            {
                if (!await hierarchy.UnitExistsAsync(Guid.Parse(value), ct))
                {
                    errors.Add(field, "validation.org.parent_not_found");
                    return null;
                }
            }
        }
        return condition;
    }

    private static JsonElement? ConditionJson(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(stored);
        return doc.RootElement.Clone();
    }

    private async Task AuditAssignmentAsync(string action, Guid id, string subjectType, Guid subjectId, string role, ScopeType scope, Guid? scopeId,
        CancellationToken ct, bool bump = false, GrantCondition? condition = null)
    {
        if (bump)
        {
            await BumpAsync(ct);
        }
        await audit.WriteAsync(new AuditEntry(action, "role_assignment", id.ToString(),
            TargetOrgUnitId: scope is ScopeType.OrgUnit or ScopeType.OrgSubtree ? scopeId : null,
            Changes: new { subjectType, subjectId, role, scopeType = Sql.ToSnake(scope.ToString()), scopeId, condition = condition?.ToJson() }), ct);
    }

    internal Task BumpAsync(CancellationToken ct) => store.BumpAsync(ct);
}

/// <summary>Low-level writes shared by administration and provisioning (keeps the dependency graph acyclic).</summary>
public sealed class AssignmentStore(IDbSession session, ITenantContext context)
{
    public async Task InsertAssignmentAsync(Guid id, string subjectType, Guid subjectId, Guid roleId, ScopeType scope, Guid? scopeId,
        string? scopeResourceType, Guid? grantedBy, CancellationToken ct, GrantCondition? condition = null)
    {
        await session.ExecAsync(
            """
            insert into authz.role_assignment (tenant_id, id, subject_type, subject_id, role_id, scope_type, scope_id, scope_resource_type, granted_by, condition)
            values (@t, @id, @st, @sid, @role, @scope, @scopeId, @srt, @by, @c)
            """,
            new
            {
                t = context.RequireTenant(), id, st = subjectType, sid = subjectId, role = roleId, scope, scopeId, srt = scopeResourceType, by = grantedBy,
                c = condition is null ? null : new Jsonb(condition.ToCanonicalJson()),
            }, ct);
        await BumpAsync(ct);
    }

    public Task BumpAsync(CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into authz.grants_version (tenant_id, version) values (@t, 1)
            on conflict (tenant_id) do update set version = authz.grants_version.version + 1
            """, new { t = context.RequireTenant() }, ct);
}
