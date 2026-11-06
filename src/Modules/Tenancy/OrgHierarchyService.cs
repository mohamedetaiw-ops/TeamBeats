using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

public sealed record OrgUnitView(
    Guid Id,
    Guid? ParentId,
    string TypeKey,
    string Code,
    string Status,
    IReadOnlyDictionary<string, string> Names,
    int Level,
    string Owner = OrgOwners.Product);

/// <summary>Who owns a unit or membership (spec 002 US5 scenario 4). Only the owner may change it.</summary>
public static class OrgOwners
{
    public const string Product = "product";
    public const string Directory = "directory";
}

public sealed record OrgUnitResult(OrgUnitView Unit, IReadOnlyList<string> Warnings);

public sealed record CreateOrgUnitRequest(Guid? ParentId, string TypeKey, string Code, IReadOnlyDictionary<string, string> Names);

public sealed record UpdateOrgUnitRequest(IReadOnlyDictionary<string, string>? Names, string? TypeKey);

public sealed record MoveOrgUnitRequest(Guid? NewParentId);

public sealed record DeactivateOrgUnitRequest(string? ChildrenAction, string? MembershipsAction, Guid? MoveMembershipsTo);

public sealed record CreateUnitTypeRequest(string Key, IReadOnlyDictionary<string, string> Names);

/// <summary>Built-in unit types (spec 002 FR-002) with a nominal rank used only for placement warnings.</summary>
public static class BuiltInUnitTypes
{
    public static readonly IReadOnlyDictionary<string, int> Rank = new Dictionary<string, int>
    {
        ["organization"] = 1,
        ["subsidiary"] = 2,
        ["division"] = 3,
        ["business_unit"] = 3,
        ["department"] = 4,
    };
}

/// <summary>
/// Organization hierarchy (spec 002): adjacency list plus closure table maintained in the same transaction.
/// Structure grants nothing by itself (FR-009); every change is authorized through the central service.
/// </summary>
public sealed class OrgHierarchyService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    ITenantSettingsReader settings,
    IAuditWriter audit,
    IOutbox outbox)
{
    public const int MaxDepth = 12;

    public async Task<IReadOnlyList<OrgUnitView>> ListAsync(Guid? parentId, int? depth, CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.OrgUnitView, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }

        if (parentId is { } p && !await ExistsAsync(p, ct))
        {
            throw AppException.NotFound();
        }

        var rows = await session.QueryAsync(
            """
            with scope as (
              select c.descendant_id as id, c.depth as rel
              from tenancy.org_closure c where @parent::uuid is not null and c.ancestor_id = @parent
              union all
              select u.id, null from tenancy.org_unit u where @parent::uuid is null
            )
            select u.id, u.parent_id, u.type_key, u.code, u.status,
                   (select jsonb_object_agg(n.locale, n.name) from tenancy.org_unit_name n where n.org_unit_id = u.id)::text as names,
                   (select max(depth) + 1 from tenancy.org_closure c where c.descendant_id = u.id) as level, u.owner
            from scope s join tenancy.org_unit u on u.id = s.id
            where (@maxDepth::int is null or coalesce(s.rel, (select max(depth) from tenancy.org_closure c where c.descendant_id = u.id)) <= @maxDepth)
            order by level, u.code
            """,
            new { parent = parentId, maxDepth = depth }, MapUnit, ct);

        return rows.Where(u => coverage.Covers(u.Id)).ToList();
    }

    public async Task<OrgUnitView> GetAsync(Guid id, CancellationToken ct)
    {
        var unit = await LoadAsync(id, ct) ?? throw AppException.NotFound();
        await authz.DemandVisibleAsync(Permissions.OrgUnitView, AuthzTarget.OrgUnit(id), ct);
        return unit;
    }

    public async Task<OrgUnitResult> CreateAsync(CreateOrgUnitRequest request, CancellationToken ct)
    {
        OrgUnitView? parent = null;
        if (request.ParentId is { } parentId)
        {
            // Cross-tenant parents are invisible (RLS), so they are reported exactly like a missing unit (TEN-005).
            parent = await LoadAsync(parentId, ct) ?? throw AppException.Validation("parentId", "validation.org.parent_not_found");
            await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.OrgUnit(parentId), ct);
            if (parent.Status != "active")
            {
                throw AppException.Validation("parentId", "validation.org.parent_inactive");
            }
            if (parent.Level + 1 > MaxDepth)
            {
                throw AppException.Validation("parentId", "validation.org.max_depth");
            }
        }
        else
        {
            await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.Tenant, ct);
        }

        await ValidateTypeAsync(request.TypeKey, ct);
        await ValidateNamesAsync(request.Names, requireDefault: true, ct);
        if (string.IsNullOrWhiteSpace(request.Code) || !System.Text.RegularExpressions.Regex.IsMatch(request.Code, "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
        {
            throw AppException.Validation("code", "validation.org.code_format");
        }

        var id = await InsertUnitAsync(request.ParentId, request.TypeKey, request.Code, request.Names, null, ct);
        var unit = (await LoadAsync(id, ct))!;
        return new OrgUnitResult(unit, PlacementWarnings(parent?.TypeKey, request.TypeKey));
    }

    /// <summary>A unit owned by a directory connection, keyed by <c>external_source</c> and <c>external_id</c>.</summary>
    internal sealed record DirectoryOwnership(Guid ConnectionId, string ExternalSource, string ExternalId);

    /// <summary>Inserts a unit after the caller has authorized and validated it. Used by the API and by directory mapping.</summary>
    internal async Task<Guid> InsertUnitAsync(Guid? parentId, string typeKey, string code, IReadOnlyDictionary<string, string> names,
        DirectoryOwnership? directory, CancellationToken ct)
    {
        var id = Ids.New();
        var tenantId = context.RequireTenant();
        try
        {
            await session.ExecAsync(
                """
                insert into tenancy.org_unit (tenant_id, id, parent_id, type_key, code, status, owner, owner_connection_id, external_source, external_id)
                values (@t, @id, @p, @type, @code, 'active', @owner, @conn, @src, @ext)
                """,
                new
                {
                    t = tenantId, id, p = parentId, type = typeKey, code, owner = directory is null ? OrgOwners.Product : OrgOwners.Directory,
                    conn = directory?.ConnectionId, src = directory?.ExternalSource, ext = directory?.ExternalId,
                }, ct);
        }
        catch (PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.org.code_taken");
        }

        await session.ExecAsync(
            """
            insert into tenancy.org_closure (tenant_id, ancestor_id, descendant_id, depth)
            select tenant_id, ancestor_id, @id, depth + 1 from tenancy.org_closure where descendant_id = @p
            union all select @t, @id, @id, 0
            """, new { t = tenantId, id, p = parentId }, ct);
        await WriteNamesAsync(id, names, ct);
        await BumpVersionAsync(ct);

        await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitCreated, "org_unit", id.ToString(), TargetOrgUnitId: id,
            Changes: new { parentId, typeKey, code, names, owner = directory is null ? OrgOwners.Product : OrgOwners.Directory }), ct);
        await outbox.EnqueueAsync(new OrganizationUnitCreated(id, parentId), ct);
        return id;
    }

    /// <summary>Changes the default-locale name of a directory-owned unit; other locale names stay as the product set them.</summary>
    internal async Task RenameFromDirectoryAsync(Guid id, string locale, string oldName, string newName, CancellationToken ct)
    {
        await WriteNamesAsync(id, new Dictionary<string, string> { [locale] = newName }, ct);
        await BumpVersionAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitRenamed, "org_unit", id.ToString(), TargetOrgUnitId: id,
            Changes: new { locale, oldName, newName, owner = OrgOwners.Directory }), ct);
    }

    /// <summary>Sets a directory-owned unit active again when its value reappears in the directory.</summary>
    internal async Task ReactivateFromDirectoryAsync(Guid id, CancellationToken ct)
    {
        await session.ExecAsync("update tenancy.org_unit set status = 'active' where id = @id", new { id }, ct);
        await BumpVersionAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitCreated, "org_unit", id.ToString(), TargetOrgUnitId: id,
            Changes: new { reactivated = true, owner = OrgOwners.Directory }), ct);
        await outbox.EnqueueAsync(new OrganizationUnitCreated(id, null), ct);
    }

    /// <summary>Deactivates a directory-owned unit that has no active children or memberships left (spec 002 US5 scenario 6).</summary>
    internal async Task DeactivateFromDirectoryAsync(Guid id, CancellationToken ct)
    {
        await session.ExecAsync("update tenancy.org_unit set status = 'inactive' where id = @id", new { id }, ct);
        await BumpVersionAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitDeactivated, "org_unit", id.ToString(), TargetOrgUnitId: id,
            Changes: new { owner = OrgOwners.Directory }), ct);
        await outbox.EnqueueAsync(new OrganizationUnitDeactivated(id), ct);
    }

    private static void RefuseIfDirectoryOwned(OrgUnitView unit)
    {
        if (unit.Owner == OrgOwners.Directory)
        {
            throw AppException.Conflict("conflict.org.directory_owned");
        }
    }

    public async Task<OrgUnitView> UpdateAsync(Guid id, UpdateOrgUnitRequest request, CancellationToken ct)
    {
        var unit = await LoadAsync(id, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.OrgUnit(id), ct);
        if (unit.Owner == OrgOwners.Directory)
        {
            // The directory owns the type and the default-locale name; names in other locales stay editable (spec 002 US5 edge cases).
            var defaultLocale = (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            if ((request.TypeKey is { } t && t != unit.TypeKey) || (request.Names?.ContainsKey(defaultLocale) ?? false))
            {
                throw AppException.Conflict("conflict.org.directory_owned");
            }
        }

        if (request.TypeKey is { } type && type != unit.TypeKey)
        {
            await ValidateTypeAsync(type, ct);
            await session.ExecAsync("update tenancy.org_unit set type_key = @type where id = @id", new { id, type }, ct);
        }

        if (request.Names is { Count: > 0 } names)
        {
            await ValidateNamesAsync(names, requireDefault: false, ct);
            await WriteNamesAsync(id, names, ct);
        }

        await BumpVersionAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitRenamed, "org_unit", id.ToString(), TargetOrgUnitId: id,
            Changes: new { oldNames = unit.Names, newNames = request.Names, oldType = unit.TypeKey, newType = request.TypeKey }), ct);
        return (await LoadAsync(id, ct))!;
    }

    /// <summary>Moves a subtree in one transaction (spec 002 US2). Source and destination must both be in the caller's scope.</summary>
    public async Task<int> MoveAsync(Guid id, Guid? newParentId, CancellationToken ct)
    {
        var unit = await LoadAsync(id, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.OrgUnit(id), ct);
        RefuseIfDirectoryOwned(unit);

        OrgUnitView? newParent = null;
        if (newParentId is { } np)
        {
            newParent = await LoadAsync(np, ct) ?? throw AppException.Validation("newParentId", "validation.org.parent_not_found");
            await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.OrgUnit(np), ct);
            if (newParent.Status != "active")
            {
                throw AppException.Validation("newParentId", "validation.org.parent_inactive");
            }
        }
        else
        {
            await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.Tenant, ct);
        }

        return await MoveCoreAsync(unit, newParentId, newParent, ct);
    }

    /// <summary>Moves a subtree after the caller has authorized it. Used by the API and by directory mapping.</summary>
    internal async Task<int> MoveCoreAsync(OrgUnitView unit, Guid? newParentId, OrgUnitView? newParent, CancellationToken ct)
    {
        var id = unit.Id;
        if (unit.ParentId == newParentId)
        {
            return 0;
        }

        // Lock the subtree so concurrent moves serialize.
        var subtree = await session.QueryAsync(
            """
            select u.id from tenancy.org_unit u
            join tenancy.org_closure c on c.descendant_id = u.id and c.ancestor_id = @id
            for update of u
            """, new { id }, r => r.GetGuid(0), ct);

        if (newParentId is { } target && subtree.Contains(target))
        {
            throw AppException.Validation("newParentId", "validation.org.cycle");
        }

        var height = await session.ScalarAsync<int>("select coalesce(max(depth), 0) from tenancy.org_closure where ancestor_id = @id", new { id }, ct);
        var newLevel = (newParent?.Level ?? 0) + 1;
        if (newLevel + height > MaxDepth)
        {
            throw AppException.Validation("newParentId", "validation.org.max_depth");
        }

        await session.ExecAsync(
            """
            delete from tenancy.org_closure c
            where c.descendant_id in (select descendant_id from tenancy.org_closure where ancestor_id = @id)
              and c.ancestor_id not in (select descendant_id from tenancy.org_closure where ancestor_id = @id)
            """, new { id }, ct);
        if (newParentId is not null)
        {
            await session.ExecAsync(
                """
                insert into tenancy.org_closure (tenant_id, ancestor_id, descendant_id, depth)
                select sup.tenant_id, sup.ancestor_id, sub.descendant_id, sup.depth + sub.depth + 1
                from tenancy.org_closure sup
                cross join tenancy.org_closure sub
                where sup.descendant_id = @parent and sub.ancestor_id = @id
                """, new { id, parent = newParentId }, ct);
        }
        await session.ExecAsync("update tenancy.org_unit set parent_id = @parent where id = @id", new { id, parent = newParentId }, ct);
        await BumpVersionAsync(ct);

        await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitMoved, "org_unit", id.ToString(), TargetOrgUnitId: id,
            Changes: new { oldParentId = unit.ParentId, newParentId, affectedUnits = subtree.Count }), ct);
        await outbox.EnqueueAsync(new OrganizationUnitMoved(id, unit.ParentId, newParentId, subtree.Count), ct);
        return subtree.Count;
    }

    /// <summary>Deactivation (spec 002 US4). Units are never hard-deleted.</summary>
    public async Task DeactivateAsync(Guid id, DeactivateOrgUnitRequest request, OrgMembershipService memberships, CancellationToken ct)
    {
        var unit = await LoadAsync(id, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.OrgUnit(id), ct);
        RefuseIfDirectoryOwned(unit);
        if (unit.Status != "active")
        {
            return;
        }

        var children = await session.QueryAsync(
            "select id from tenancy.org_unit where parent_id = @id and status = 'active' order by code", new { id }, r => r.GetGuid(0), ct);
        if (children.Count > 0 && request.ChildrenAction is not ("deactivate" or "move_to_parent"))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.org.active_children", null,
                new Dictionary<string, object?> { ["activeChildren"] = children });
        }

        var affected = request.ChildrenAction == "deactivate"
            ? await session.QueryAsync("select descendant_id from tenancy.org_closure where ancestor_id = @id", new { id }, r => r.GetGuid(0), ct)
            : [id];

        var activeMemberships = await session.ScalarAsync<long>(
            "select count(*) from tenancy.org_membership where org_unit_id = any(@ids) and valid_to is null",
            new { ids = affected.ToArray() }, ct);
        if (activeMemberships > 0 && request.MembershipsAction is not ("end" or "move"))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.org.active_memberships", null,
                new Dictionary<string, object?> { ["activeMemberships"] = activeMemberships });
        }

        if (request.ChildrenAction == "move_to_parent")
        {
            foreach (var child in children)
            {
                await MoveAsync(child, unit.ParentId, ct);
            }
        }

        if (activeMemberships > 0)
        {
            await memberships.HandleDeactivationAsync(affected, request.MembershipsAction!, request.MoveMembershipsTo, ct);
        }

        await session.ExecAsync("update tenancy.org_unit set status = 'inactive' where id = any(@ids)", new { ids = affected.ToArray() }, ct);
        await BumpVersionAsync(ct);
        foreach (var unitId in affected)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.OrgUnitDeactivated, "org_unit", unitId.ToString(), TargetOrgUnitId: unitId,
                Changes: new { childrenAction = request.ChildrenAction, membershipsAction = request.MembershipsAction }), ct);
            await outbox.EnqueueAsync(new OrganizationUnitDeactivated(unitId), ct);
        }
    }

    public async Task<IReadOnlyList<object>> ListTypesAsync(CancellationToken ct)
    {
        var custom = await session.QueryAsync("select key, names::text from tenancy.org_unit_type order by key", null,
            r => (object)new { key = r.GetString(0), builtIn = false, names = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) }, ct);
        return BuiltInUnitTypes.Rank.Keys.Select(k => (object)new { key = k, builtIn = true, names = (object?)null }).Concat(custom).ToList();
    }

    public async Task CreateTypeAsync(CreateUnitTypeRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.Tenant, ct);
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Key ?? string.Empty, "^[a-z][a-z0-9_]{1,39}$"))
        {
            throw AppException.Validation("key", "validation.org.type_key_format");
        }
        if (BuiltInUnitTypes.Rank.ContainsKey(request.Key!))
        {
            throw AppException.Conflict("conflict.org.type_exists");
        }
        await ValidateNamesAsync(request.Names, requireDefault: true, ct);
        try
        {
            await session.ExecAsync("insert into tenancy.org_unit_type (tenant_id, key, names) values (@t, @k, @n)",
                new { t = context.RequireTenant(), k = request.Key, n = Jsonb.From(request.Names) }, ct);
        }
        catch (PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.org.type_exists");
        }
    }

    internal async Task<OrgUnitView?> LoadAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select u.id, u.parent_id, u.type_key, u.code, u.status,
                   (select jsonb_object_agg(n.locale, n.name) from tenancy.org_unit_name n where n.org_unit_id = u.id)::text as names,
                   (select max(depth) + 1 from tenancy.org_closure c where c.descendant_id = u.id) as level, u.owner
            from tenancy.org_unit u where u.id = @id
            """, new { id }, MapUnit, ct);

    private Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        session.ScalarAsync<bool>("select exists (select 1 from tenancy.org_unit where id = @id)", new { id }, ct);

    private static OrgUnitView MapUnit(NpgsqlDataReader r)
    {
        var names = r.Get<string>("names");
        return new OrgUnitView(
            r.GetGuid(0),
            r.Get<Guid?>("parent_id"),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            names is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(names)!,
            r.GetInt32(r.GetOrdinal("level")),
            r.GetString(r.GetOrdinal("owner")));
    }

    private async Task ValidateTypeAsync(string? typeKey, CancellationToken ct)
    {
        if (typeKey is null)
        {
            throw AppException.Validation("typeKey", "validation.org.type_unknown");
        }
        if (BuiltInUnitTypes.Rank.ContainsKey(typeKey))
        {
            return;
        }
        var custom = await session.ScalarAsync<bool>("select exists (select 1 from tenancy.org_unit_type where key = @k)", new { k = typeKey }, ct);
        if (!custom)
        {
            throw AppException.Validation("typeKey", "validation.org.type_unknown");
        }
    }

    private async Task ValidateNamesAsync(IReadOnlyDictionary<string, string>? names, bool requireDefault, CancellationToken ct)
    {
        var locale = await settings.GetLocaleSettingsAsync(ct);
        var errors = new ValidationErrors();
        if (names is null || names.Count == 0)
        {
            errors.Add("names", "validation.org.name_required");
        }
        else
        {
            if (requireDefault && (!names.TryGetValue(locale.DefaultLocale, out var d) || string.IsNullOrWhiteSpace(d)))
            {
                errors.Add($"names.{locale.DefaultLocale}", "validation.org.default_name_required");
            }
            foreach (var (l, n) in names)
            {
                if (!locale.EnabledLocales.Contains(l))
                {
                    errors.Add($"names.{l}", "validation.locale.not_enabled");
                }
                else if (string.IsNullOrWhiteSpace(n) || n.Length > 200)
                {
                    errors.Add($"names.{l}", "validation.org.name_length");
                }
            }
        }
        errors.ThrowIfAny();
    }

    private async Task WriteNamesAsync(Guid id, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        foreach (var (locale, name) in names)
        {
            await session.ExecAsync(
                """
                insert into tenancy.org_unit_name (tenant_id, org_unit_id, locale, name) values (@t, @id, @l, @n)
                on conflict (tenant_id, org_unit_id, locale) do update set name = excluded.name
                """, new { t = context.RequireTenant(), id, l = locale, n = name.Trim() }, ct);
        }
    }

    internal Task BumpVersionAsync(CancellationToken ct) =>
        session.ExecAsync("update tenancy.hierarchy_version set version = version + 1", null, ct);

    /// <summary>Unusual placements are allowed with a warning (spec 002 section 6, decided 2026-11-04).</summary>
    private static IReadOnlyList<string> PlacementWarnings(string? parentType, string childType) =>
        parentType is not null
        && BuiltInUnitTypes.Rank.TryGetValue(parentType, out var parentRank)
        && BuiltInUnitTypes.Rank.TryGetValue(childType, out var childRank)
        && childRank < parentRank
            ? ["warning.org.unusual_placement"]
            : [];
}
