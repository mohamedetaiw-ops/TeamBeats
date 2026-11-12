using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public static class CommunityKinds
{
    public const string Open = "open";
    public const string Approval = "approval";
    public const string Closed = "closed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Open, Approval, Closed };
}

public static class MemberRoles
{
    public const string Member = "member";
    public const string Moderator = "moderator";
    public const string Owner = "owner";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Member, Moderator, Owner };
}

public sealed record CommunityRequest(Dictionary<string, string>? Names, Dictionary<string, string>? Descriptions, string? Kind, Guid? OrgUnitId);

public sealed record ChannelRequest(string? Key, Dictionary<string, string>? Names);

public sealed record MemberRequest(Guid UserId, string? Role);

public sealed record ChannelView(Guid Id, string Key, IReadOnlyDictionary<string, string> Names);

/// <summary>What the viewer may do in a community; worked out on the server so the page only shows it.</summary>
public sealed record CommunityRights(bool Member, bool Pending, bool CanJoin, bool CanLeave, bool CanPost, bool CanModerate, bool CanManage, DateTimeOffset? MutedUntil);

public sealed record CommunityView(
    Guid Id,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    string Kind,
    Guid? OrgUnitId,
    string Status,
    int MemberCount,
    IReadOnlyList<ChannelView> Channels,
    CommunityRights Rights,
    int Version);

/// <summary>What the viewer may do in the communities area as a whole (the directory page shows the create action from it).</summary>
/// <summary>What the viewer may do across communities: create one, and open the moderation queue and keyword filter.</summary>
public sealed record DirectoryRights(bool CanCreate, bool CanModerate, bool CanManageKeywords);

public sealed record MemberView(Guid UserId, string DisplayName, string Role, string Status, string Source);

internal sealed record CommunityRow(Guid Id, Dictionary<string, string> Names, Dictionary<string, string> Descriptions, string Kind, Guid? OrgUnitId, string Status,
    DateTimeOffset CreatedAt, int Version)
{
    public const string Columns = "id, names::text, descriptions::text, kind, org_unit_id, status, created_at, row_version";

    public static CommunityRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), EngagementJson.Read<Dictionary<string, string>>(r.GetString(1)),
        EngagementJson.Read<Dictionary<string, string>>(r.GetString(2)), r.GetString(3), r.Get<Guid?>("org_unit_id"), r.GetString(5),
        r.GetFieldValue<DateTimeOffset>(6), r.GetInt32(7));

    public AuthzTarget Scope => OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
}

/// <summary>The viewer's standing in one community.</summary>
internal sealed record Standing(CommunityRow Community, bool Member, bool Pending, string? Role, bool Moderates, bool Manages, bool Participates, bool Stored,
    DateTimeOffset? MutedUntil = null)
{
    /// <summary>A muted member reads but neither posts nor comments until the mute ends (FR-021).</summary>
    public bool Muted => MutedUntil is not null;

    /// <summary>
    /// Who reads a community's content: its members in every kind, anyone who takes part in an open community, and
    /// the moderators in scope, who must see what they moderate. Everyone else does not even see a closed community.
    /// </summary>
    public bool Reads => Member || Moderates || (Participates && Community.Kind == CommunityKinds.Open);

    /// <summary>Whether the community is listed for the viewer at all (FR-001: closed communities are invisible).</summary>
    public bool Sees => Reads || (Participates && Community.Kind == CommunityKinds.Approval) || Pending;

    public bool Posts => Community.Status == "active" && Participates && !Muted && (Member || (Community.Kind == CommunityKinds.Open && Moderates));
}

/// <summary>
/// Communities and their members and channels (spec 023 US1, FR-001 and FR-002). Membership is stored by the community
/// except for department communities, whose members are the people of the linked org unit and the units below it, read
/// from Organization (spec 002) whenever they are needed, so moves apply at once. Owners and moderators are always
/// stored. Taking part needs <c>engagement.participate</c>; moderating needs the moderator relation or
/// <c>engagement.moderate</c> in the community's scope; managing needs the owner relation or <c>engagement.manage</c>.
/// </summary>
public sealed class Communities(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning grants,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 1000;
    public const int MaxChannels = 30;
    public const string GeneralChannel = "general";

    internal async Task RequireAsync(CancellationToken ct) => await entitlements.DemandAsync(Capabilities.EngagementCommunities, ct);

    internal async Task<CommunityRow?> RowAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {CommunityRow.Columns} from engagement.community where id = @id", new { id }, CommunityRow.Read, ct);

    /// <summary>Whether a person is in a department community through the org unit (or a unit below it).</summary>
    private async Task<bool> InUnitAsync(Guid userId, Guid unitId, CancellationToken ct)
    {
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(userId, ct))
        {
            if (await hierarchy.IsAncestorOrSelfAsync(unitId, m.OrgUnitId, ct))
            {
                return true;
            }
        }
        return false;
    }

    internal async Task<Standing> StandingAsync(CommunityRow c, CancellationToken ct)
    {
        var user = context.RequireUser();
        var stored = await session.QuerySingleOrDefaultAsync(
            "select role, status from engagement.community_member where community_id = @id and user_id = @user", new { id = c.Id, user },
            r => (Role: r.GetString(0), Status: r.GetString(1)), ct);
        var participates = (await authz.CheckAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct)).Allowed;
        var member = stored.Status == "active" || (c.OrgUnitId is { } unit && await InUnitAsync(user, unit, ct));
        var role = stored.Status == "active" ? stored.Role : member ? MemberRoles.Member : null;
        var moderates = role is MemberRoles.Owner or MemberRoles.Moderator || (await authz.CheckAsync(Permissions.EngagementModerate, c.Scope, ct)).Allowed;
        var manages = role == MemberRoles.Owner || (await authz.CheckAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct)).Allowed;
        var mutes = await session.QueryAsync("select until from engagement.mute where community_id = @id and user_id = @user and until > @now",
            new { id = c.Id, user, now = clock.UtcNow }, r => r.GetFieldValue<DateTimeOffset>(0), ct);
        DateTimeOffset? mutedUntil = mutes.Count > 0 ? mutes[0] : null;
        return new Standing(c, member, stored.Status == "pending", role, moderates, manages, participates, stored.Status == "active", mutedUntil);
    }

    /// <summary>A community the viewer can see; anything else is not found, so closed communities stay invisible.</summary>
    internal async Task<Standing> VisibleAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var c = await RowAsync(id, ct) ?? throw AppException.NotFound();
        var standing = await StandingAsync(c, ct);
        return standing.Sees || standing.Manages ? standing : throw AppException.NotFound();
    }

    /// <summary>A community whose content the viewer may read.</summary>
    internal async Task<Standing> ReadableAsync(Guid id, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, ct);
        return standing.Reads ? standing : throw AppException.NotFound();
    }

    /// <summary>
    /// Whether another person could read the community, for mentions (FR-004) and notifications: nobody is told about
    /// content they cannot open.
    /// </summary>
    internal async Task<bool> CanReadAsync(CommunityRow c, Guid userId, CancellationToken ct)
    {
        var stored = await session.ScalarAsync<string>(
            "select role from engagement.community_member where community_id = @id and user_id = @userId and status = 'active'", new { id = c.Id, userId }, ct);
        if (stored is not null || (c.OrgUnitId is { } unit && await InUnitAsync(userId, unit, ct)))
        {
            return await grants.UserHoldsAsync(userId, Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
        }
        if (c.Kind == CommunityKinds.Open && await grants.UserHoldsAsync(userId, Permissions.EngagementParticipate, AuthzTarget.Tenant, ct))
        {
            return true;
        }
        return await grants.UserHoldsAsync(userId, Permissions.EngagementModerate, c.Scope, ct);
    }

    private async Task<IReadOnlyList<ChannelView>> ChannelsAsync(Guid communityId, CancellationToken ct) =>
        await session.QueryAsync("select id, key, names::text from engagement.channel where community_id = @communityId order by position, key",
            new { communityId }, r => new ChannelView(r.GetGuid(0), r.GetString(1), EngagementJson.Read<Dictionary<string, string>>(r.GetString(2))), ct);

    private async Task<int> MemberCountAsync(CommunityRow c, CancellationToken ct)
    {
        var stored = (await session.QueryAsync("select user_id from engagement.community_member where community_id = @id and status = 'active'", new { id = c.Id },
            r => r.GetGuid(0), ct)).ToHashSet();
        if (c.OrgUnitId is { } unit)
        {
            stored.UnionWith(await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync([unit], ct), ct));
        }
        return stored.Count;
    }

    private async Task<CommunityView> ViewAsync(Standing s, CancellationToken ct)
    {
        var c = s.Community;
        var rights = new CommunityRights(s.Member, s.Pending,
            CanJoin: c.Status == "active" && s.Participates && !s.Member && !s.Pending && c.OrgUnitId is null && c.Kind != CommunityKinds.Closed,
            CanLeave: s.Stored,
            CanPost: s.Posts, CanModerate: s.Moderates, CanManage: s.Manages, MutedUntil: s.MutedUntil);
        return new CommunityView(c.Id, c.Names, c.Descriptions, c.Kind, c.OrgUnitId, c.Status, await MemberCountAsync(c, ct),
            s.Reads || s.Manages ? await ChannelsAsync(c.Id, ct) : [], rights, c.Version);
    }

    public static string ETag(int version) => $"\"v{version}\"";

    // ---- Directory ----

    /// <summary>The communities the viewer can see (closed ones only when they belong), mine first.</summary>
    public async Task<IReadOnlyList<CommunityView>> ListAsync(string? q, CancellationToken ct)
    {
        await RequireAsync(ct);
        var rows = await session.QueryAsync($"select {CommunityRow.Columns} from engagement.community where status = 'active' order by created_at, id", null,
            CommunityRow.Read, ct);
        var text = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var views = new List<CommunityView>();
        foreach (var c in rows)
        {
            if (text is not null && !c.Names.Values.Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var s = await StandingAsync(c, ct);
            if (s.Sees || s.Manages)
            {
                views.Add(await ViewAsync(s, ct));
            }
        }
        return views.OrderByDescending(v => v.Rights.Member).ToList();
    }

    public async Task<CommunityView> GetAsync(Guid id, CancellationToken ct) => await ViewAsync(await VisibleAsync(id, ct), ct);

    public async Task<DirectoryRights> RightsAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var manages = (await authz.CheckAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct)).Allowed;
        // A moderator scoped to one org unit reaches the queue from that community's page; this flag covers the rest.
        var moderates = manages || (await authz.CheckAsync(Permissions.EngagementModerate, AuthzTarget.Tenant, ct)).Allowed
            || await session.ScalarAsync<bool>(
                "select exists (select 1 from engagement.community_member where user_id = @user and status = 'active' and role in ('owner', 'moderator'))",
                new { user = context.RequireUser() }, ct);
        return new DirectoryRights(manages, moderates, manages);
    }

    // ---- Create and change ----

    private async Task ValidateAsync(CommunityRequest request, Guid? id, ValidationErrors errors, CancellationToken ct)
    {
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        if (request.Names is not { Count: > 0 } names || names.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > MaxNameLength || n.Any(char.IsControl)))
        {
            errors.Add("names", "validation.engagement.names");
        }
        else if (names.Keys.Any(l => !enabled.Contains(l)))
        {
            errors.Add("names", "validation.engagement.locale");
        }
        if (request.Descriptions is { } d && (d.Keys.Any(l => !enabled.Contains(l)) || d.Values.Any(v => v is null || v.Trim().Length > MaxDescriptionLength)))
        {
            errors.Add("descriptions", "validation.engagement.description");
        }
        if (request.Kind is not { } kind || !CommunityKinds.All.Contains(kind))
        {
            errors.Add("kind", "validation.engagement.kind");
        }
        if (request.OrgUnitId is { } unit)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("orgUnitId", "validation.engagement.org_unit");
            }
            else if (await session.ScalarAsync<bool>("select exists (select 1 from engagement.community where org_unit_id = @unit and id <> @id)",
                new { unit, id = id ?? Guid.Empty }, ct))
            {
                errors.Add("orgUnitId", "validation.engagement.org_unit_taken");
            }
        }
    }

    private static Dictionary<string, string> Trimmed(IReadOnlyDictionary<string, string>? values) =>
        (values ?? new Dictionary<string, string>()).Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToDictionary(v => v.Key, v => v.Value.Trim(), StringComparer.Ordinal);

    /// <summary>
    /// Engagement administrators create communities of any kind (section 3). The creator becomes the owner. A department
    /// community needs <c>engagement.manage</c> too; its members then follow the unit.
    /// </summary>
    public async Task<CommunityView> CreateAsync(CommunityRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        await ValidateAsync(request, null, errors, ct);
        errors.ThrowIfAny();
        var user = context.RequireUser();
        var id = Ids.New();
        var now = clock.UtcNow;
        var names = Trimmed(request.Names);
        await session.ExecAsync(
            """
            insert into engagement.community (tenant_id, id, names, descriptions, kind, org_unit_id, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @names, @descriptions, @kind, @unit, @user, @now, @now)
            """, new { id, names = Jsonb.From(names), descriptions = Jsonb.From(Trimmed(request.Descriptions)), kind = request.Kind, unit = request.OrgUnitId, user, now }, ct);
        await session.ExecAsync(
            "insert into engagement.community_member (tenant_id, community_id, user_id, role, status, created_at) values (platform.current_tenant_id(), @id, @user, 'owner', 'active', @now)",
            new { id, user, now }, ct);
        await session.ExecAsync(
            "insert into engagement.channel (tenant_id, id, community_id, key, names, position, created_at) values (platform.current_tenant_id(), @channel, @id, @key, @names, 0, @now)",
            new { channel = Ids.New(), id, key = GeneralChannel, names = Jsonb.From(new Dictionary<string, string>()), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementCommunityCreated, "community", id.ToString(),
            Changes: new { names, request.Kind, orgUnitId = request.OrgUnitId }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<CommunityView> UpdateAsync(Guid id, CommunityRequest request, string? ifMatch, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        if (ifMatch != ETag(s.Community.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.community_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(s.Community.Version) });
        }
        var errors = new ValidationErrors();
        await ValidateAsync(request, id, errors, ct);
        // Linking or unlinking an org unit changes who the members are: only engagement administrators may do it.
        if (request.OrgUnitId != s.Community.OrgUnitId && !(await authz.CheckAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct)).Allowed)
        {
            errors.Add("orgUnitId", "validation.engagement.org_unit_manage");
        }
        errors.ThrowIfAny();
        var names = Trimmed(request.Names);
        await session.ExecAsync(
            """
            update engagement.community set names = @names, descriptions = @descriptions, kind = @kind, org_unit_id = @unit, updated_at = @now,
              row_version = row_version + 1 where id = @id
            """, new { id, names = Jsonb.From(names), descriptions = Jsonb.From(Trimmed(request.Descriptions)), kind = request.Kind, unit = request.OrgUnitId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementCommunityChanged, "community", id.ToString(),
            Changes: new { names, request.Kind, orgUnitId = request.OrgUnitId }), ct);
        await ReindexAsync(id, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Archives a community: it stays readable by its members and takes no new posts (edge case for deleted units too).</summary>
    public async Task<CommunityView> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        if (s.Community.Status == "archived")
        {
            throw AppException.Conflict("conflict.engagement.archived");
        }
        await session.ExecAsync("update engagement.community set status = 'archived', updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementCommunityArchived, "community", id.ToString(), Changes: new { reason = "manual" }), ct);
        await ReindexAsync(id, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Asks search to read every post of the community again, after a change to who may read them.</summary>
    internal async Task ReindexAsync(Guid communityId, CancellationToken ct)
    {
        var posts = await session.QueryAsync("select id from engagement.post where community_id = @communityId", new { communityId }, r => r.GetGuid(0), ct);
        foreach (var post in posts)
        {
            await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, post), ct);
        }
    }

    // ---- Membership ----

    /// <summary>
    /// Joins an open community at once, or asks to join an approval one (the owners see the request). Closed and
    /// department communities cannot be joined: owners add people to closed ones, and units decide department ones.
    /// </summary>
    public async Task<CommunityView> JoinAsync(Guid id, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        var c = s.Community;
        if (!s.Participates)
        {
            throw AppException.Denied();
        }
        if (c.Status != "active" || c.OrgUnitId is not null || c.Kind == CommunityKinds.Closed)
        {
            throw AppException.Conflict("conflict.engagement.not_joinable");
        }
        if (s.Member || s.Pending)
        {
            return await ViewAsync(s, ct);
        }
        var status = c.Kind == CommunityKinds.Open ? "active" : "pending";
        await session.ExecAsync(
            """
            insert into engagement.community_member (tenant_id, community_id, user_id, role, status, created_at)
            values (platform.current_tenant_id(), @id, @user, 'member', @status, @now)
            on conflict (tenant_id, community_id, user_id) do nothing
            """, new { id, user = context.RequireUser(), status, now = clock.UtcNow }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<CommunityView> LeaveAsync(Guid id, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        var user = context.RequireUser();
        if (s.Role == MemberRoles.Owner && await session.ScalarAsync<long>(
                "select count(*) from engagement.community_member where community_id = @id and role = 'owner' and status = 'active'", new { id }, ct) <= 1)
        {
            throw AppException.Conflict("conflict.engagement.last_owner");
        }
        await session.ExecAsync("delete from engagement.community_member where community_id = @id and user_id = @user", new { id, user }, ct);
        await session.ExecAsync("delete from engagement.post_follow f using engagement.post p where p.id = f.post_id and p.community_id = @id and f.user_id = @user",
            new { id, user }, ct);
        return await ViewAsync(await StandingAsync(s.Community, ct), ct);
    }

    public async Task<IReadOnlyList<MemberView>> MembersAsync(Guid id, CancellationToken ct)
    {
        var s = await ReadableAsync(id, ct);
        var c = s.Community;
        var stored = await session.QueryAsync("select user_id, role, status from engagement.community_member where community_id = @id", new { id },
            r => (User: r.GetGuid(0), Role: r.GetString(1), Status: r.GetString(2)), ct);
        // Pending requests are for the people who decide them.
        var rows = stored.Where(m => m.Status == "active" || s.Manages).Select(m => (m.User, m.Role, m.Status, Source: "community")).ToList();
        if (c.OrgUnitId is { } unit)
        {
            var known = rows.Select(r => r.User).ToHashSet();
            rows.AddRange((await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync([unit], ct), ct)).Where(u => !known.Contains(u))
                .Select(u => (u, MemberRoles.Member, "active", "org_unit")));
        }
        var names = await users.GetManyAsync(rows.Select(r => r.User).ToList(), ct);
        return rows.Where(r => names.ContainsKey(r.User))
            .Select(r => new MemberView(r.User, names[r.User].DisplayName, r.Role, r.Status, r.Source))
            .OrderBy(m => m.Status == "pending" ? 0 : 1).ThenBy(m => m.Role switch { MemberRoles.Owner => 0, MemberRoles.Moderator => 1, _ => 2 })
            .ThenBy(m => m.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Owners add people, accept requests and make members moderators or owners.</summary>
    public async Task<IReadOnlyList<MemberView>> SetMemberAsync(Guid id, MemberRequest request, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        var role = request.Role ?? MemberRoles.Member;
        if (!MemberRoles.All.Contains(role))
        {
            throw AppException.Validation("role", "validation.engagement.role");
        }
        if (await users.GetAsync(request.UserId, ct) is not { IsTest: false })
        {
            throw AppException.Validation("userId", "validation.engagement.user");
        }
        var before = await session.ScalarAsync<string>("select role from engagement.community_member where community_id = @id and user_id = @u and status = 'active'",
            new { id, u = request.UserId }, ct);
        if (before == MemberRoles.Owner && role != MemberRoles.Owner && await session.ScalarAsync<long>(
                "select count(*) from engagement.community_member where community_id = @id and role = 'owner' and status = 'active'", new { id }, ct) <= 1)
        {
            throw AppException.Conflict("conflict.engagement.last_owner");
        }
        await session.ExecAsync(
            """
            insert into engagement.community_member (tenant_id, community_id, user_id, role, status, created_at)
            values (platform.current_tenant_id(), @id, @u, @role, 'active', @now)
            on conflict (tenant_id, community_id, user_id) do update set role = excluded.role, status = 'active'
            """, new { id, u = request.UserId, role, now = clock.UtcNow }, ct);
        if (before != role)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementMemberChanged, "community", id.ToString(),
                Changes: new { user = request.UserId, from = before, to = role }), ct);
        }
        return await MembersAsync(id, ct);
    }

    /// <summary>Owners remove a member or turn down a request. Department members leave with their unit, not here.</summary>
    public async Task<IReadOnlyList<MemberView>> RemoveMemberAsync(Guid id, Guid userId, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        var before = await session.ScalarAsync<string>("select role from engagement.community_member where community_id = @id and user_id = @userId and status = 'active'",
            new { id, userId }, ct);
        if (before == MemberRoles.Owner && await session.ScalarAsync<long>(
                "select count(*) from engagement.community_member where community_id = @id and role = 'owner' and status = 'active'", new { id }, ct) <= 1)
        {
            throw AppException.Conflict("conflict.engagement.last_owner");
        }
        if (await session.ExecAsync("delete from engagement.community_member where community_id = @id and user_id = @userId", new { id, userId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementMemberChanged, "community", id.ToString(), Changes: new { user = userId, from = before, to = (string?)null }), ct);
        return await MembersAsync(id, ct);
    }

    // ---- Channels ----

    public async Task<CommunityView> AddChannelAsync(Guid id, ChannelRequest request, CancellationToken ct)
    {
        var s = await VisibleAsync(id, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var key = request.Key?.Trim();
        if (key is null || !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z0-9_]{1,40}$"))
        {
            errors.Add("key", "validation.engagement.channel_key");
        }
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        if (request.Names is not { Count: > 0 } names || names.Keys.Any(l => !enabled.Contains(l))
            || names.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > MaxNameLength || n.Any(char.IsControl)))
        {
            errors.Add("names", "validation.engagement.names");
        }
        var count = await session.ScalarAsync<long>("select count(*) from engagement.channel where community_id = @id", new { id }, ct);
        if (count >= MaxChannels)
        {
            errors.Add("key", "validation.engagement.channels_full");
        }
        else if (key is not null && await session.ScalarAsync<bool>("select exists (select 1 from engagement.channel where community_id = @id and key = @key)", new { id, key }, ct))
        {
            errors.Add("key", "validation.engagement.channel_taken");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            "insert into engagement.channel (tenant_id, id, community_id, key, names, position, created_at) values (platform.current_tenant_id(), @channel, @id, @key, @names, @position, @now)",
            new { channel = Ids.New(), id, key, names = Jsonb.From(Trimmed(request.Names)), position = (int)count, now = clock.UtcNow }, ct);
        return await GetAsync(id, ct);
    }
}
