using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>An agent in the marketplace (spec 020 US6) as one person sees it.</summary>
public sealed record MarketplaceAgent(
    Guid Id,
    Dictionary<string, string>? Name,
    Dictionary<string, string>? Description,
    string? Icon,
    string? Category,
    Guid DepartmentId,
    IReadOnlyDictionary<string, string>? Department,
    bool CanUse,
    string? Request,
    bool Favourite,
    double? Rating,
    int Ratings,
    int? MyScore);

public sealed record MarketplaceView(bool Paused, IReadOnlyList<MarketplaceAgent> Agents);

public sealed record RatingRequest(int? Score, string? Comment);

/// <summary>A rating with its comment, for those who may read comments (open question 3).</summary>
public sealed record RatingView(Guid UserId, int Score, string? Comment, DateTimeOffset UpdatedAt);

public sealed record RatingsView(double? Average, int Count, IReadOnlyList<RatingView> Comments);

public sealed record AccessRequestView(Guid Id, Guid AgentId, Guid UserId, string Status, DateTimeOffset CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? UserName = null);

public sealed record AccessDecision(bool? Grant);

public static class AccessRequestStatuses
{
    public const string Pending = "pending";
    public const string Granted = "granted";
    public const string Denied = "denied";
}

/// <summary>
/// The agent marketplace (spec 020 US6, FR-010): published agents the person may use, and those whose owners offer them
/// on request, by category and department, in the person's language. An agent hidden outside its audience never
/// appears there. A request becomes an inbox task for the owners; the decision is audited and told to the requester.
/// Favourites lead the assistant's agent list. Ratings are 1 to 5; everyone sees the average, and comments are read
/// only by the owners, the AI Policy Administrator and their author.
/// </summary>
public sealed class AgentMarketplace(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IOrganizationHierarchy org, IAuditWriter audit, INotificationSender notifications, IUserDirectory users, IClock clock)
{
    public const int MaxResults = 200;
    public const int MaxCommentLength = 1000;
    public static readonly TimeSpan RatingRetention = TimeSpan.FromDays(730);

    private static AgentDefinition Definition(string json) => JsonSerializer.Deserialize<AgentDefinition>(json, AgentPolicyService.Json)!;

    private async Task<Guid> UserAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        await entitlements.DemandAsync(Capabilities.AiAgentsMarketplace, ct);
        return context.RequireUser();
    }

    private sealed record Listed(Guid Id, Guid Unit, Guid Owner, Guid? Backup, AgentDefinition Definition);

    private async Task<List<Listed>> PublishedAsync(Guid? id, CancellationToken ct) =>
        await session.QueryAsync(
            """
            select a.id, a.org_unit_id, a.owner_user_id, a.backup_owner_user_id, v.definition::text from agents.agent a
            join agents.agent_version v on v.agent_id = a.id and v.number = a.current_version
            where a.status = 'published' and (@id::uuid is null or a.id = @id) order by a.updated_at desc, a.id limit 500
            """, new { id }, r => new Listed(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.Get<Guid?>("backup_owner_user_id"), Definition(r.GetString(4))), ct);

    /// <summary>The units the person belongs to, with their ancestors, which is what an org unit audience is matched against.</summary>
    private async Task<HashSet<Guid>> WithinAsync(Guid user, CancellationToken ct)
    {
        var memberships = await org.GetActiveMembershipsAsync(user, ct);
        var paths = await org.GetPathsAsync(memberships.Select(m => m.OrgUnitId).Distinct(), ct);
        return paths.Values.SelectMany(p => p).ToHashSet();
    }

    private static bool InAudience(Listed a, HashSet<Guid> within, HashSet<Guid> granted) =>
        a.Definition.Audience?.Scope == AgentAudience.Tenant || (a.Definition.Audience?.OrgUnits ?? []).Any(within.Contains) || granted.Contains(a.Id);

    private static bool Matches(AgentDefinition d, string q) =>
        (d.Name?.Values.AsEnumerable() ?? []).Concat(d.Description?.Values.AsEnumerable() ?? []).Any(text => text.Contains(q, StringComparison.CurrentCultureIgnoreCase));

    public async Task<MarketplaceView> ListAsync(string? q, string? category, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        // With every agent stopped the marketplace says so instead of offering agents that cannot answer (edge cases).
        if (await session.ScalarAsync<bool?>("select active from agents.kill_switch", null, ct) == true)
        {
            return new MarketplaceView(true, []);
        }
        var within = await WithinAsync(user, ct);
        var granted = (await session.QueryAsync("select agent_id from agents.agent_access_grant where user_id = @user", new { user }, r => r.GetGuid(0), ct)).ToHashSet();
        var term = q?.Trim();
        var listed = (await PublishedAsync(null, ct))
            .Where(a => InAudience(a, within, granted) || a.Definition.Audience?.Requestable == true)
            .Where(a => category is null || a.Definition.Category == category)
            .Where(a => string.IsNullOrEmpty(term) || Matches(a.Definition, term))
            .Take(MaxResults)
            .ToList();
        if (listed.Count == 0)
        {
            return new MarketplaceView(false, []);
        }
        var ids = listed.Select(a => a.Id).ToArray();
        var favourites = (await session.QueryAsync("select agent_id from agents.agent_favourite where user_id = @user", new { user }, r => r.GetGuid(0), ct)).ToHashSet();
        var ratings = (await session.QueryAsync(
            "select agent_id, avg(score)::float8, count(*)::int, max(case when user_id = @user then score end) from agents.agent_rating where agent_id = any(@ids) group by agent_id",
            new { ids, user }, r => (Id: r.GetGuid(0), Average: r.GetDouble(1), Count: r.GetInt32(2), Mine: r.IsDBNull(3) ? (int?)null : r.GetInt32(3)), ct))
            .ToDictionary(r => r.Id);
        var requests = (await session.QueryAsync(
            "select distinct on (agent_id) agent_id, status from agents.agent_access_request where user_id = @user and agent_id = any(@ids) order by agent_id, created_at desc",
            new { user, ids }, r => (Id: r.GetGuid(0), Status: r.GetString(1)), ct)).ToDictionary(r => r.Id, r => r.Status);
        var departments = await org.GetNamesAsync(listed.Select(a => a.Unit).Distinct(), ct);
        return new MarketplaceView(false, listed.Select(a =>
        {
            var canUse = InAudience(a, within, granted);
            var rating = ratings.GetValueOrDefault(a.Id);
            return new MarketplaceAgent(a.Id, a.Definition.Name, a.Definition.Description, a.Definition.Icon, a.Definition.Category, a.Unit,
                departments.GetValueOrDefault(a.Unit), canUse, canUse ? null : requests.GetValueOrDefault(a.Id), favourites.Contains(a.Id),
                rating.Count > 0 ? Math.Round(rating.Average, 1) : null, rating.Count, rating.Mine);
        }).ToList());
    }

    /// <summary>A published agent the person may see in the marketplace, with whether they may use it; anything else is not found.</summary>
    private async Task<(Listed Agent, bool CanUse)> VisibleAsync(Guid id, Guid user, CancellationToken ct)
    {
        var found = (await PublishedAsync(id, ct)).SingleOrDefault() ?? throw AppException.NotFound();
        var granted = await session.ScalarAsync<bool>("select exists (select 1 from agents.agent_access_grant where agent_id = @id and user_id = @user)", new { id, user }, ct);
        var canUse = InAudience(found, await WithinAsync(user, ct), granted ? [id] : []);
        if (!canUse && found.Definition.Audience?.Requestable != true)
        {
            throw AppException.NotFound();
        }
        return (found, canUse);
    }

    public async Task SetFavouriteAsync(Guid id, bool favourite, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (!favourite)
        {
            // Only the person's own favourite can be removed; anything else is not found.
            if (await session.ExecAsync("delete from agents.agent_favourite where user_id = @user and agent_id = @id", new { user, id }, ct) == 0)
            {
                throw AppException.NotFound();
            }
            return;
        }
        if (!(await VisibleAsync(id, user, ct)).CanUse)
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync(
            """
            insert into agents.agent_favourite (tenant_id, user_id, agent_id, created_at) values (platform.current_tenant_id(), @user, @id, @now)
            on conflict do nothing
            """, new { user, id, now = clock.UtcNow }, ct);
    }

    /// <summary>A rating of an agent the person may use (US6 scenario 5); a second rating replaces the first.</summary>
    public async Task RateAsync(Guid id, RatingRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (!(await VisibleAsync(id, user, ct)).CanUse)
        {
            throw AppException.NotFound();
        }
        var errors = new ValidationErrors();
        if (request.Score is not (>= 1 and <= 5))
        {
            errors.Add("score", "validation.agent.rating_score");
        }
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > MaxCommentLength })
        {
            errors.Add("comment", "validation.agent.rating_comment");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into agents.agent_rating (tenant_id, user_id, agent_id, score, comment, updated_at)
            values (platform.current_tenant_id(), @user, @id, @score, @comment, @now)
            on conflict (tenant_id, user_id, agent_id) do update set score = excluded.score, comment = excluded.comment, updated_at = excluded.updated_at
            """, new { user, id, score = request.Score, comment, now = clock.UtcNow }, ct);
    }

    private sealed record Owners(Guid Unit, Guid Owner, Guid? Backup);

    /// <summary>
    /// Whether the person may read an agent's comments and decide its access requests: an owner, the backup owner, or a
    /// designer of its unit.
    /// </summary>
    private async Task<bool> OwnsAsync(Guid agentId, Guid user, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select org_unit_id, owner_user_id, backup_owner_user_id from agents.agent where id = @agentId",
            new { agentId }, r => new Owners(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("backup_owner_user_id")), ct);
        if (row is not { } agent)
        {
            return false;
        }
        return agent.Owner == user || agent.Backup == user || (await authz.CheckAsync(Permissions.AgentDesign, AuthzTarget.OrgUnit(agent.Unit), ct)).Allowed;
    }

    /// <summary>
    /// Ratings of an agent. The average and count are for anyone who can see the agent in the marketplace; comments are
    /// for its owners and the AI Policy Administrator, and otherwise the person's own comment only.
    /// </summary>
    public async Task<RatingsView> RatingsAsync(Guid id, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var all = await OwnsAsync(id, user, ct) || await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct);
        if (!all)
        {
            await VisibleAsync(id, user, ct);
        }
        else if ((await PublishedAsync(id, ct)).Count == 0)
        {
            // Ratings belong to published agents; an unknown agent, or one of another tenant, is not found.
            throw AppException.NotFound();
        }
        var rows = await session.QueryAsync("select user_id, score, comment, updated_at from agents.agent_rating where agent_id = @id order by updated_at desc",
            new { id }, r => new RatingView(r.GetGuid(0), r.GetInt32(1), r.Get<string?>("comment"), r.GetFieldValue<DateTimeOffset>(3)), ct);
        return new RatingsView(rows.Count > 0 ? Math.Round(rows.Average(r => r.Score), 1) : null, rows.Count,
            rows.Where(r => r.Comment is not null && (all || r.UserId == user)).ToList());
    }

    /// <summary>Asks the owners for access (US6 scenario 3). One request waits at a time; asking again returns it.</summary>
    public async Task<AccessRequestView> RequestAccessAsync(Guid id, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var (agent, canUse) = await VisibleAsync(id, user, ct);
        if (canUse)
        {
            throw AppException.Conflict("conflict.agent.already_in_audience");
        }
        var pending = await session.QuerySingleOrDefaultAsync(
            "select id, agent_id, user_id, status, created_at, decided_by, decided_at from agents.agent_access_request where agent_id = @id and user_id = @user and status = 'pending'",
            new { id, user }, Read, ct);
        if (pending is not null)
        {
            return pending;
        }
        var requestId = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.agent_access_request (tenant_id, id, agent_id, user_id, status, created_at)
            values (platform.current_tenant_id(), @requestId, @id, @user, 'pending', @now)
            """, new { requestId, id, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentAccessRequested, "agent", id.ToString(), TargetOrgUnitId: agent.Unit,
            Changes: new { request = requestId, requester = user }), ct);
        return new AccessRequestView(requestId, id, user, AccessRequestStatuses.Pending, now, null, null);
    }

    private static AccessRequestView Read(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"));

    /// <summary>Requests of one agent waiting for its owners, for the agent page.</summary>
    public async Task<IReadOnlyList<AccessRequestView>> PendingAsync(Guid agentId, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (!await OwnsAsync(agentId, user, ct))
        {
            throw AppException.NotFound();
        }
        var pending = await session.QueryAsync(
            "select id, agent_id, user_id, status, created_at, decided_by, decided_at from agents.agent_access_request where agent_id = @agentId and status = 'pending' order by created_at",
            new { agentId }, Read, ct);
        // The owners decide by name; the directory reads the people of this tenant only.
        var names = await users.GetManyAsync(pending.Select(p => p.UserId).ToList(), ct);
        return pending.Select(p => p with { UserName = names.TryGetValue(p.UserId, out var u) ? u.DisplayName : null }).ToList();
    }

    /// <summary>An owner grants or denies a request; a grant lets the person use the agent, and the requester is told either way.</summary>
    public async Task<AccessRequestView> DecideAsync(Guid requestId, AccessDecision decision, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        if (decision.Grant is not { } grant)
        {
            throw AppException.Validation("grant", "validation.required");
        }
        var request = await session.QuerySingleOrDefaultAsync(
            "select id, agent_id, user_id, status, created_at, decided_by, decided_at from agents.agent_access_request where id = @requestId",
            new { requestId }, Read, ct);
        if (request is null || !await OwnsAsync(request.AgentId, user, ct))
        {
            throw AppException.NotFound();
        }
        if (request.Status != AccessRequestStatuses.Pending)
        {
            throw AppException.Conflict("conflict.agent.access_decided");
        }
        var now = clock.UtcNow;
        var status = grant ? AccessRequestStatuses.Granted : AccessRequestStatuses.Denied;
        await session.ExecAsync("update agents.agent_access_request set status = @status, decided_by = @user, decided_at = @now where id = @requestId",
            new { status, user, now, requestId }, ct);
        if (grant)
        {
            await session.ExecAsync(
                """
                insert into agents.agent_access_grant (tenant_id, agent_id, user_id, granted_by, granted_at)
                values (platform.current_tenant_id(), @agent, @requester, @user, @now) on conflict do nothing
                """, new { agent = request.AgentId, requester = request.UserId, user, now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(grant ? AuditActions.AgentAccessGranted : AuditActions.AgentAccessDenied, "agent", request.AgentId.ToString(),
            Changes: new { request = requestId, requester = request.UserId }), ct);
        await notifications.SendAsync(new NotificationRequest([request.UserId], NotificationCategories.RequestUpdate,
            grant ? "agents.access_granted" : "agents.access_denied", "/agents/marketplace", DedupeKey: $"agent-access-{requestId}"), ct);
        return request with { Status = status, DecidedBy = user, DecidedAt = now };
    }

    /// <summary>Ratings older than two years go (section 8).</summary>
    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("delete from agents.agent_rating where updated_at < @cutoff", new { cutoff = now - RatingRetention }, ct);
}

/// <summary>Access requests waiting for the person as an agent owner, as inbox tasks (US6 scenario 3).</summary>
public sealed class AgentAccessTasks(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz) : IPersonalTaskSource
{
    public const string TaskSource = "agent_access";

    public static string Link(Guid agentId) => $"/admin/agents/{agentId}#access";

    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAgentsMarketplace, ct))
        {
            return [];
        }
        var design = await authz.CoverageAsync(Permissions.AgentDesign, ct);
        var rows = await session.QueryAsync(
            """
            select r.id, a.id, v.definition::text, r.created_at from agents.agent_access_request r
            join agents.agent a on a.id = r.agent_id
            join agents.agent_version v on v.agent_id = a.id and v.number = a.current_version
            where r.status = 'pending' and a.status = 'published'
              and (a.owner_user_id = @userId or a.backup_owner_user_id = @userId or @all or a.org_unit_id = any(@units))
            order by r.created_at limit 50
            """, new { userId, all = design.WholeTenant, units = design.Units.ToArray() },
            r => (Request: r.GetGuid(0), Agent: r.GetGuid(1), Definition: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        return rows.Select(r => new PersonalTask(TaskSource, r.Request,
            (IReadOnlyDictionary<string, string>?)JsonSerializer.Deserialize<AgentDefinition>(r.Definition, AgentPolicyService.Json)!.Name ?? new Dictionary<string, string>(),
            Link(r.Agent), r.At, null)).ToList();
    }
}
