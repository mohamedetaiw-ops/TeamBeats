using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public sealed record MemberRequest(Guid UserId, string? Role);

/// <summary>A new space, or the settings of one. Kind and template are read only when the space is created.</summary>
public sealed record SpaceRequest(
    string? Name,
    string? Description,
    string? Kind,
    string? Visibility,
    string? Template,
    bool? AllowJoin,
    string? Classification,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    IReadOnlyList<MemberRequest>? Members,
    int? Version);

public sealed record PersonRef(Guid UserId, string DisplayName);

/// <summary>What the viewer may do in one space; decided on the server so the page only shows it.</summary>
public sealed record SpaceRights(string? Role, bool Reads, bool Contributes, bool Owns, bool Manages, bool CanJoin, bool CanLeave);

public sealed record SpaceView(
    Guid Id,
    string Kind,
    string Visibility,
    string Name,
    string Description,
    bool AllowJoin,
    string Classification,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    string Status,
    int MemberCount,
    IReadOnlyList<PersonRef> Owners,
    SpaceRights Rights,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>A space in the directory or in "my spaces". CanOpen is false for a private space the viewer is not in.</summary>
public sealed record SpaceSummary(Guid Id, string Kind, string Visibility, string Name, string Description, string Status, int MemberCount, string? Role, bool CanOpen,
    DateTimeOffset UpdatedAt);

public sealed record SpaceMemberView(Guid UserId, string DisplayName, string Role, bool Guest, DateTimeOffset AddedAt);

internal sealed record SpaceRow(
    Guid Id,
    string Kind,
    string Visibility,
    string Name,
    string Description,
    bool AllowJoin,
    string Classification,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    string Template,
    Guid? OrgUnitId,
    string Status,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version)
{
    public const string Columns =
        "id, kind, visibility, name, description, allow_join, classification, starts_on, ends_on, template, org_unit_id, status, created_by, created_at, updated_at, row_version";

    public static SpaceRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8), r.GetString(9), r.Get<Guid?>("org_unit_id"),
        r.GetString(11), r.GetGuid(12), r.GetFieldValue<DateTimeOffset>(13), r.GetFieldValue<DateTimeOffset>(14), r.GetInt32(15));

    public bool Active => Status == SpaceStatuses.Active;

    /// <summary>
    /// The space for authorization (spec 004): public spaces are readable by the tenant; private and secret ones only
    /// through the space roles. No owning unit, so no org-scoped role reaches into a space (section 7).
    /// </summary>
    public ResourceDescriptor Descriptor => new(SpacesService.ResourceType, Id, null,
        Visibility == SpaceVisibilities.Public ? ResourceVisibility.Tenant : ResourceVisibility.Restricted, Classification);
}

/// <summary>The viewer's standing in a space.</summary>
internal sealed record SpaceStanding(SpaceRow Space, string? Role, bool Reads, bool Contributes, bool Owns, bool Manages, bool Guest)
{
    /// <summary>Secret spaces are known only to their members and spaces administrators (FR-003); private ones are listed.</summary>
    public bool Sees => Reads || Role is not null || Manages || Space.Visibility == SpaceVisibilities.Private;
}

/// <summary>
/// Spaces, their members and their directory (spec 029 US1, FR-001 to FR-003, FR-013, FR-014). Who reads, contributes
/// to and owns a space is decided by authorization (spec 004) on the space resource, from the space roles Spaces assigns
/// there; space_member is kept in step for lists. Spaces administrators (<c>spaces.manage</c>) see every space's
/// metadata, archive and restore any space and can give an ownerless space an owner, but read no content without a
/// space role.
/// </summary>
public sealed class SpacesService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning grants,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    SpaceSettings settings,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string ResourceType = "space";
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 2000;
    public const int MaxMembers = 500;
    public const string AddedTemplate = "spaces.member.added";
    public const string OwnerlessTemplate = "spaces.space.ownerless";
    public const string OwnershipTemplate = "spaces.space.ownership";

    public static string Link(Guid id) => $"/spaces/{id}";

    internal Task RequireAsync(CancellationToken ct) => entitlements.DemandAsync(Capabilities.SpacesProjects, ct);

    internal Task<SpaceRow?> RowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {SpaceRow.Columns} from spaces.space where id = @id", new { id }, SpaceRow.Read, ct);

    internal async Task<SpaceStanding> StandingAsync(SpaceRow space, CancellationToken ct)
    {
        var user = context.RequireUser();
        var member = await session.QuerySingleOrDefaultAsync("select role, guest from spaces.space_member where space_id = @id and user_id = @user",
            new { id = space.Id, user }, r => (Role: r.GetString(0), Guest: r.GetBoolean(1)), ct);
        var target = AuthzTarget.Resource(space.Descriptor);
        var reads = (await authz.CheckAsync(Permissions.SpacesView, target, ct)).Allowed;
        var contributes = space.Active && (await authz.CheckAsync(Permissions.SpacesContribute, target, ct)).Allowed;
        var owns = (await authz.CheckAsync(Permissions.SpacesOwn, target, ct)).Allowed;
        var manages = (await authz.CheckAsync(Permissions.SpacesManage, AuthzTarget.Tenant, ct)).Allowed;
        return new SpaceStanding(space, member.Role, reads, contributes, owns, manages, member.Guest);
    }

    /// <summary>A space the viewer may know about; anything else is not found, so secret spaces stay invisible.</summary>
    internal async Task<SpaceStanding> VisibleAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var space = await RowAsync(id, ct) ?? throw AppException.NotFound();
        var standing = await StandingAsync(space, ct);
        return standing.Sees ? standing : throw AppException.NotFound();
    }

    /// <summary>A space whose content the viewer may read. A private space they are not in is refused with a reason the page shows.</summary>
    internal async Task<SpaceStanding> ReadableAsync(Guid id, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, ct);
        return standing.Reads ? standing : throw AppException.Denied("authz.denied.spaces_private");
    }

    /// <summary>A space the viewer may change; archived spaces are read-only (FR-013).</summary>
    internal async Task<SpaceStanding> ContributableAsync(Guid id, CancellationToken ct)
    {
        var standing = await ReadableAsync(id, ct);
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        return standing.Contributes ? standing : throw AppException.Denied("authz.denied.spaces_contribute");
    }

    private async Task<SpaceStanding> OwnedAsync(Guid id, bool orManager, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, ct);
        return standing.Owns || (orManager && standing.Manages) ? standing : throw AppException.Denied("authz.denied.spaces_owner");
    }

    /// <summary>Whether another person may read the space, so nobody is told about a space they cannot open (FR-003).</summary>
    internal Task<bool> CanReadAsync(SpaceRow space, Guid userId, CancellationToken ct) =>
        grants.UserHoldsAsync(userId, Permissions.SpacesView, AuthzTarget.Resource(space.Descriptor), ct);

    // ---- Views ----

    private async Task<SpaceView> ViewAsync(SpaceStanding standing, CancellationToken ct)
    {
        var s = standing.Space;
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.space_member where space_id = @id", new { id = s.Id }, ct);
        var ownerIds = await session.QueryAsync("select user_id from spaces.space_member where space_id = @id and role = 'owner' order by added_at",
            new { id = s.Id }, r => r.GetGuid(0), ct);
        var names = await users.GetManyAsync(ownerIds, ct);
        var owners = ownerIds.Select(o => new PersonRef(o, names.TryGetValue(o, out var u) ? u.DisplayName : "")).ToList();
        var canJoin = standing.Role is null && !standing.Guest && s.Active && s.Visibility == SpaceVisibilities.Public && s.AllowJoin && standing.Reads && count < MaxMembers;
        var canLeave = standing.Role is not null && !(standing.Role == SpaceRoles.Owner && ownerIds.Count == 1);
        // Name, description and owners are what the directory shows; tasks, files and discussion need read access.
        return new SpaceView(s.Id, s.Kind, s.Visibility, s.Name, s.Description, s.AllowJoin, s.Classification, s.StartsOn, s.EndsOn, s.Status, count, owners,
            new SpaceRights(standing.Role, standing.Reads, standing.Contributes, standing.Owns, standing.Manages, canJoin, canLeave), s.CreatedAt, s.UpdatedAt, s.Version);
    }

    public async Task<SpaceView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync(await VisibleAsync(id, ct), ct);

    /// <summary>
    /// The directory (US1): with <paramref name="mine"/> the spaces the viewer belongs to; otherwise every active public
    /// and private space and the secret ones they are in. Spaces administrators also see secret and archived spaces
    /// with <paramref name="all"/>.
    /// </summary>
    public async Task<IReadOnlyList<SpaceSummary>> ListAsync(bool mine, bool all, string? q, CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = context.RequireUser();
        var manages = (await authz.CheckAsync(Permissions.SpacesManage, AuthzTarget.Tenant, ct)).Allowed;
        var everything = all && manages;
        var search = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var rows = await session.QueryAsync(
            $"""
            select {string.Join(", ", SpaceRow.Columns.Split(", ").Select(c => "s." + c))}, m.role, m.guest,
                   (select count(*)::int from spaces.space_member x where x.space_id = s.id)
            from spaces.space s
            left join spaces.space_member m on m.space_id = s.id and m.user_id = @user
            where (@search::text is null or s.name ilike @search or s.description ilike @search)
              and (case
                when @mine then m.user_id is not null
                when @everything then true
                else s.status = 'active' and (s.visibility <> 'secret' or m.user_id is not null)
              end)
            order by s.updated_at desc, s.id
            limit 500
            """, new { user, search, mine, everything },
            r => (Row: SpaceRow.Read(r), Role: r.Get<string>("role"), Guest: !r.IsDBNull(17) && r.GetBoolean(17), Count: r.GetInt32(18)), ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var readable = (await authz.FilterAsync(Permissions.SpacesView, rows.Select(r => r.Row.Descriptor), ct)).Select(d => d.ResourceId).ToHashSet();
        var me = await memberships.GetAsync(user, ct);
        var guest = me?.MembershipType == MembershipTypes.Guest;
        return rows
            // A guest sees only the spaces they are invited to (US4 scenario 2).
            .Where(r => !guest || r.Role is not null)
            .Select(r => new SpaceSummary(r.Row.Id, r.Row.Kind, r.Row.Visibility, r.Row.Name, r.Row.Description, r.Row.Status, r.Count, r.Role, readable.Contains(r.Row.Id), r.Row.UpdatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<SpaceMemberView>> MembersAsync(Guid id, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, ct);
        if (!standing.Reads && !standing.Manages)
        {
            throw AppException.Denied("authz.denied.spaces_private");
        }
        var rows = await session.QueryAsync(
            "select user_id, role, guest, added_at from spaces.space_member where space_id = @id order by case role when 'owner' then 0 when 'member' then 1 else 2 end, added_at",
            new { id }, r => (User: r.GetGuid(0), Role: r.GetString(1), Guest: r.GetBoolean(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.User).ToList(), ct);
        return rows.Select(r => new SpaceMemberView(r.User, names.TryGetValue(r.User, out var u) ? u.DisplayName : "", r.Role, r.Guest, r.At)).ToList();
    }

    // ---- Creating and changing ----

    private void ValidateFields(SpaceRequest request, string kind, ValidationErrors errors)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength)
        {
            errors.Add("name", "validation.spaces.name");
        }
        if ((request.Description?.Length ?? 0) > MaxDescriptionLength)
        {
            errors.Add("description", "validation.spaces.description");
        }
        if (request.Visibility is not { } visibility || !SpaceVisibilities.All.Contains(visibility))
        {
            errors.Add("visibility", "validation.spaces.visibility");
        }
        if (request.Classification is { } classification && !SpaceClassifications.All.Contains(classification))
        {
            errors.Add("classification", "validation.spaces.classification");
        }
        if (kind == SpaceKinds.Team && (request.StartsOn is not null || request.EndsOn is not null))
        {
            errors.Add("startsOn", "validation.spaces.team_dates");
        }
        if (request.StartsOn is { } start && request.EndsOn is { } end && end < start)
        {
            errors.Add("endsOn", "validation.spaces.dates");
        }
    }

    /// <summary>Whether the viewer may create spaces: the permission, the package, and the tenant's creation units (FR-017).</summary>
    internal async Task<bool> MayCreateAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SpacesProjects, ct) || !(await authz.CheckAsync(Permissions.SpacesCreate, AuthzTarget.Tenant, ct)).Allowed)
        {
            return false;
        }
        if ((await authz.CheckAsync(Permissions.SpacesManage, AuthzTarget.Tenant, ct)).Allowed)
        {
            return true;
        }
        var rules = await settings.CurrentAsync(ct);
        if (rules.CreationUnits.Count == 0)
        {
            return true;
        }
        var allowed = await hierarchy.GetSubtreesAsync(rules.CreationUnits, ct);
        return (await hierarchy.GetActiveMembershipsAsync(context.RequireUser(), ct)).Any(m => allowed.Contains(m.OrgUnitId));
    }

    /// <summary>The people to add with their roles: active, non-guest members of the tenant (guests come by invitation).</summary>
    private async Task<Dictionary<Guid, string>> PeopleAsync(IReadOnlyList<MemberRequest> requested, Guid creator, ValidationErrors errors, CancellationToken ct)
    {
        var people = new Dictionary<Guid, string>();
        for (var i = 0; i < requested.Count; i++)
        {
            var role = requested[i].Role ?? SpaceRoles.Member;
            if (!SpaceRoles.All.Contains(role))
            {
                errors.Add($"members[{i}].role", "validation.spaces.role");
            }
            if (requested[i].UserId != creator)
            {
                people[requested[i].UserId] = role;
            }
        }
        if (people.Count + 1 > MaxMembers)
        {
            errors.Add("members", "validation.spaces.member_count");
            return people;
        }
        var known = await memberships.GetManyAsync(people.Keys.ToList(), ct);
        var identities = await users.GetManyAsync(people.Keys.ToList(), ct);
        foreach (var id in people.Keys)
        {
            if (!known.TryGetValue(id, out var m) || m.Status != MembershipStatus.Active || m.MembershipType == MembershipTypes.Guest
                || !identities.TryGetValue(id, out var u) || u.IsTest)
            {
                errors.Add("members", "validation.spaces.member");
                break;
            }
        }
        return people;
    }

    public async Task<SpaceView> CreateAsync(SpaceRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.SpacesCreate, AuthzTarget.Tenant, ct);
        if (!await MayCreateAsync(ct))
        {
            throw AppException.Denied("authz.denied.spaces_create_unit");
        }
        var creator = context.RequireUser();
        var errors = new ValidationErrors();
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var templateKey = request.Template ?? request.Kind ?? SpaceKinds.Project;
        var (templateKind, definition) = await settings.TemplateAsync(templateKey, locale, ct);
        if (definition is null)
        {
            errors.Add("template", "validation.spaces.template");
        }
        var kind = request.Kind ?? templateKind ?? SpaceKinds.Project;
        if (!SpaceKinds.All.Contains(kind) || (templateKind is not null && templateKind != kind))
        {
            errors.Add("kind", "validation.spaces.kind");
        }
        ValidateFields(request, kind, errors);
        var people = await PeopleAsync(request.Members ?? [], creator, errors, ct);
        errors.ThrowIfAny();

        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':spaces.count', 0))", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from spaces.space where status = 'active'", null, ct);
        if (await entitlements.GetLimitAsync(Capabilities.SpacesMaxSpaces, ct) is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.spaces.space_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.SpacesMaxSpaces, ["limit"] = max });
        }

        var id = Ids.New();
        var now = clock.UtcNow;
        var unit = (await hierarchy.GetActiveMembershipsAsync(creator, ct)).FirstOrDefault(m => m.IsPrimary)?.OrgUnitId;
        await session.ExecAsync(
            """
            insert into spaces.space (tenant_id, id, kind, visibility, name, description, allow_join, classification, starts_on, ends_on, template, org_unit_id,
              created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @kind, @visibility, @name, @description, @allowJoin, @classification, @startsOn, @endsOn, @template, @unit,
              @creator, @now, @now)
            """, new
            {
                id, kind, visibility = request.Visibility, name = request.Name!.Trim(), description = request.Description?.Trim() ?? "", allowJoin = request.AllowJoin ?? false,
                classification = request.Classification ?? SpaceClassifications.Internal, startsOn = request.StartsOn, endsOn = request.EndsOn, template = templateKey, unit,
                creator, now,
            }, ct);
        await StartFromTemplateAsync(id, definition!, creator, now, ct);

        await AddMemberRowAsync(id, creator, SpaceRoles.Owner, creator, now, ct);
        foreach (var (user, role) in people)
        {
            await AddMemberRowAsync(id, user, role, creator, now, ct);
        }
        var row = (await RowAsync(id, ct))!;
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesCreated, ResourceType, id.ToString(),
            Changes: new { kind, visibility = request.Visibility, template = templateKey, members = people.Count + 1 }), ct);
        await outbox.EnqueueAsync(new SpaceCreated(id, kind, request.Visibility!), ct);
        await outbox.EnqueueAsync(new SearchItemChanged(SpacesSearchSource.Name, id), ct);
        await TellAddedAsync(row, people.Keys.ToList(), ct);
        return await ViewAsync(await StandingAsync(row, ct), ct);
    }

    /// <summary>Copies the template's lists, columns and starter tasks into the new space (US1 scenario 1).</summary>
    private async Task StartFromTemplateAsync(Guid spaceId, TemplateDefinition definition, Guid creator, DateTimeOffset now, CancellationToken ct)
    {
        var columns = new List<List<Guid>>();
        var lists = new List<Guid>();
        for (var i = 0; i < (definition.Lists?.Count ?? 0); i++)
        {
            var list = definition.Lists![i];
            var listId = Ids.New();
            lists.Add(listId);
            await session.ExecAsync("insert into spaces.task_list (tenant_id, id, space_id, name, position) values (platform.current_tenant_id(), @listId, @spaceId, @name, @i)",
                new { listId, spaceId, name = list.Name, i }, ct);
            var ids = new List<Guid>();
            for (var c = 0; c < (list.Columns?.Count ?? 0); c++)
            {
                var columnId = Ids.New();
                ids.Add(columnId);
                await session.ExecAsync(
                    "insert into spaces.task_column (tenant_id, id, list_id, name, position, done) values (platform.current_tenant_id(), @columnId, @listId, @name, @c, @done)",
                    new { columnId, listId, name = list.Columns![c].Name, c, done = list.Columns[c].Done }, ct);
            }
            columns.Add(ids);
        }
        var position = 0;
        foreach (var task in definition.Tasks ?? [])
        {
            await session.ExecAsync(
                """
                insert into spaces.task (tenant_id, id, space_id, list_id, column_id, title, position, created_by, created_at, updated_at)
                values (platform.current_tenant_id(), @id, @spaceId, @listId, @columnId, @title, @position, @creator, @now, @now)
                """, new { id = Ids.New(), spaceId, listId = lists[task.List], columnId = columns[task.List][task.Column], title = task.Title, position = position++, creator, now }, ct);
        }
    }

    public async Task<SpaceView> UpdateAsync(Guid id, SpaceRequest request, CancellationToken ct)
    {
        var standing = await OwnedAsync(id, false, ct);
        var s = standing.Space;
        if (!s.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        if (request.Version != s.Version)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.changed", null, new Dictionary<string, object?> { ["version"] = s.Version });
        }
        var errors = new ValidationErrors();
        if (request.Kind is { } kind && kind != s.Kind)
        {
            errors.Add("kind", "validation.spaces.kind_fixed");
        }
        ValidateFields(request, s.Kind, errors);
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update spaces.space set name = @name, description = @description, visibility = @visibility, allow_join = @allowJoin, classification = @classification,
              starts_on = @startsOn, ends_on = @endsOn, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, name = request.Name!.Trim(), description = request.Description?.Trim() ?? "", visibility = request.Visibility, allowJoin = request.AllowJoin ?? false,
                classification = request.Classification ?? s.Classification, startsOn = request.StartsOn, endsOn = request.EndsOn, now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesChanged, ResourceType, id.ToString(),
            Changes: new { visibility = new { from = s.Visibility, to = request.Visibility }, classification = request.Classification ?? s.Classification }), ct);
        if (request.Visibility != s.Visibility)
        {
            await outbox.EnqueueAsync(new ResourcePermissionChanged(ResourceType, id), ct);
        }
        await outbox.EnqueueAsync(new SearchItemChanged(SpacesSearchSource.Name, id), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Archives a space: it becomes read-only and leaves My tasks; owners and spaces administrators restore it (FR-013).</summary>
    public async Task<SpaceView> ArchiveAsync(Guid id, bool archive, CancellationToken ct)
    {
        var standing = await OwnedAsync(id, true, ct);
        if (standing.Space.Active == archive)
        {
            var now = clock.UtcNow;
            await session.ExecAsync(
                """
                update spaces.space set status = @status, archived_by = @by, archived_at = @at, updated_at = @now, row_version = row_version + 1 where id = @id
                """, new
                {
                    id, status = archive ? SpaceStatuses.Archived : SpaceStatuses.Active, by = archive ? context.RequireUser() : (Guid?)null,
                    at = archive ? now : (DateTimeOffset?)null, now,
                }, ct);
            await audit.WriteAsync(new AuditEntry(archive ? AuditActions.SpacesArchived : AuditActions.SpacesRestored, ResourceType, id.ToString(),
                Changes: new { kind = standing.Space.Kind, visibility = standing.Space.Visibility }), ct);
            if (archive)
            {
                await outbox.EnqueueAsync(new SpaceArchived(id), ct);
            }
            await outbox.EnqueueAsync(new SearchItemChanged(SpacesSearchSource.Name, id), ct);
        }
        return await GetAsync(id, ct);
    }

    // ---- Members ----

    private async Task AddMemberRowAsync(Guid spaceId, Guid userId, string role, Guid? by, DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into spaces.space_member (tenant_id, space_id, user_id, role, added_by, added_at)
            values (platform.current_tenant_id(), @spaceId, @userId, @role, @by, @now)
            on conflict (tenant_id, space_id, user_id) do update set role = excluded.role
            """, new { spaceId, userId, role, by, now }, ct);
        await grants.SetResourceRoleAsync(userId, ResourceType, spaceId, ResourceRoles.Space(role), by, ct);
        await outbox.EnqueueAsync(new SpaceMembershipChanged(spaceId, userId, role), ct);
    }

    private async Task RemoveMemberRowAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        await session.ExecAsync("delete from spaces.space_member where space_id = @spaceId and user_id = @userId", new { spaceId, userId }, ct);
        await grants.SetResourceRoleAsync(userId, ResourceType, spaceId, null, context.ActorId, ct);
        await outbox.EnqueueAsync(new SpaceMembershipChanged(spaceId, userId, null), ct);
    }

    private Task<int> OwnerCountAsync(Guid spaceId, CancellationToken ct) =>
        session.ScalarAsync<int>("select count(*)::int from spaces.space_member where space_id = @spaceId and role = 'owner'", new { spaceId }, ct);

    private async Task TellAddedAsync(SpaceRow space, IReadOnlyCollection<Guid> people, CancellationToken ct)
    {
        var recipients = people.Where(p => p != context.ActorId).ToList();
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SpaceActivity, AddedTemplate, Link(space.Id),
            new Dictionary<string, string> { ["space"] = space.Name }, DedupeKey: $"spaces:{space.Id:N}:added"), ct);
    }

    /// <summary>Adds a person or changes their role. Owners do it; a spaces administrator may too, to give an ownerless space an owner (FR-014).</summary>
    public async Task<IReadOnlyList<SpaceMemberView>> SetMemberAsync(Guid id, MemberRequest request, CancellationToken ct)
    {
        var standing = await OwnedAsync(id, true, ct);
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        var errors = new ValidationErrors();
        var current = await session.ScalarAsync<string>("select role from spaces.space_member where space_id = @id and user_id = @user", new { id, user = request.UserId }, ct);
        if (current is null)
        {
            var count = await session.ScalarAsync<int>("select count(*)::int from spaces.space_member where space_id = @id", new { id }, ct);
            if (count >= MaxMembers)
            {
                throw AppException.Validation("members", "validation.spaces.member_count");
            }
            await PeopleAsync([request], Guid.Empty, errors, ct);
        }
        else if (request.Role is { } r && !SpaceRoles.All.Contains(r))
        {
            errors.Add("role", "validation.spaces.role");
        }
        errors.ThrowIfAny();
        var role = request.Role ?? SpaceRoles.Member;
        if (current == SpaceRoles.Owner && role != SpaceRoles.Owner && await OwnerCountAsync(id, ct) == 1)
        {
            throw AppException.Conflict("conflict.spaces.last_owner");
        }
        if (current != role)
        {
            await AddMemberRowAsync(id, request.UserId, role, context.RequireUser(), clock.UtcNow, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesMemberAdded, ResourceType, id.ToString(),
                Changes: new { member = request.UserId, role, previous = current }), ct);
            if (current is null)
            {
                await TellAddedAsync(standing.Space, [request.UserId], ct);
            }
        }
        return await MembersAsync(id, ct);
    }

    /// <summary>Removes a person (an owner does it), or the viewer leaves. The last owner cannot go; tasks stay assigned (US7 scenario 2).</summary>
    public async Task RemoveMemberAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var self = userId == context.RequireUser();
        var standing = self ? await VisibleAsync(id, ct) : await OwnedAsync(id, true, ct);
        var current = await session.ScalarAsync<string>("select role from spaces.space_member where space_id = @id and user_id = @userId", new { id, userId }, ct);
        if (current is null)
        {
            if (self)
            {
                return;
            }
            throw AppException.NotFound();
        }
        if (current == SpaceRoles.Owner && await OwnerCountAsync(id, ct) == 1)
        {
            throw AppException.Conflict("conflict.spaces.last_owner");
        }
        await RemoveMemberRowAsync(id, userId, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesMemberRemoved, ResourceType, standing.Space.Id.ToString(),
            Changes: new { member = userId, role = current, self }), ct);
    }

    /// <summary>Joins a public space that allows it, as a member (US1 scenario 3).</summary>
    public async Task<SpaceView> JoinAsync(Guid id, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, ct);
        var view = await ViewAsync(standing, ct);
        if (standing.Role is not null)
        {
            return view;
        }
        if (!view.Rights.CanJoin)
        {
            throw AppException.Denied("authz.denied.spaces_join");
        }
        var user = context.RequireUser();
        await AddMemberRowAsync(id, user, SpaceRoles.Member, user, clock.UtcNow, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesMemberAdded, ResourceType, id.ToString(), Changes: new { member = user, role = SpaceRoles.Member, joined = true }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// The last owner left the company (edge case, FR-014): ownership passes to the space creator's manager when they are
    /// an active employee, otherwise the spaces administrators are asked to choose an owner.
    /// </summary>
    internal async Task ReassignOwnerlessAsync(Guid leaver, Func<Guid, Task<Guid?>> managerOf, CancellationToken ct)
    {
        var spaces = await session.QueryAsync(
            """
            select s.id from spaces.space s join spaces.space_member m on m.space_id = s.id and m.user_id = @leaver and m.role = 'owner'
            where s.status = 'active'
            """, new { leaver }, r => r.GetGuid(0), ct);
        foreach (var spaceId in spaces)
        {
            var owners = await session.QueryAsync("select user_id from spaces.space_member where space_id = @spaceId and role = 'owner'", new { spaceId }, r => r.GetGuid(0), ct);
            var active = await memberships.GetManyAsync(owners, ct);
            if (owners.Any(o => o != leaver && active.TryGetValue(o, out var m) && m.Status == MembershipStatus.Active))
            {
                continue;
            }
            var space = (await RowAsync(spaceId, ct))!;
            var manager = await managerOf(space.CreatedBy);
            if (manager is { } next && next != leaver && await memberships.GetAsync(next, ct) is { Status: MembershipStatus.Active } nm && nm.MembershipType != MembershipTypes.Guest)
            {
                await AddMemberRowAsync(spaceId, next, SpaceRoles.Owner, null, clock.UtcNow, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.SpacesOwnerReassigned, ResourceType, spaceId.ToString(),
                    Changes: new { from = leaver, to = next, reason = "creator_manager" }), ct);
                await notifications.SendAsync(new NotificationRequest([next], NotificationCategories.SpaceActivity, OwnershipTemplate, Link(spaceId),
                    new Dictionary<string, string> { ["space"] = space.Name }, DedupeKey: $"spaces:{spaceId:N}:ownership"), ct);
                continue;
            }
            var administrators = (await grants.TenantHoldersAsync(Permissions.SpacesManage, ct)).Where(a => a != leaver).ToList();
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesOwnerReassigned, ResourceType, spaceId.ToString(),
                Changes: new { from = leaver, to = (Guid?)null, reason = "administrators_asked" }), ct);
            if (administrators.Count > 0)
            {
                await notifications.SendAsync(new NotificationRequest(administrators, NotificationCategories.SpaceActivity, OwnerlessTemplate, Link(spaceId),
                    new Dictionary<string, string> { ["space"] = space.Name }, DedupeKey: $"spaces:{spaceId:N}:ownerless"), ct);
            }
        }
    }
}
