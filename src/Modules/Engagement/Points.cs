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

public sealed record PointsSettingsRequest(bool? Enabled, bool? Leaderboard, int? RecognitionReceived, int? Post, int? PollVote, int? DailyCap, int? GiverDailyLimit);

/// <summary>The tenant's points setup. InPackage is false when the package has no points; nothing about points shows then.</summary>
public sealed record PointsSettingsView(bool InPackage, bool Enabled, bool Leaderboard, int RecognitionReceived, int Post, int PollVote, int DailyCap, int GiverDailyLimit, int Version);

public sealed record MyPoints(int Today, int ThisMonth, int Total, int DailyCap, bool Leaderboard);

public sealed record LeaderboardEntry(int Rank, Person Person, int Points);

public sealed record LeaderboardView(string Period, DateOnly From, DateOnly To, Guid? Unit, IReadOnlyList<LeaderboardEntry> Entries);

public static class PointActions
{
    public const string RecognitionReceived = "recognition_received";
    public const string Post = "post";
    public const string PollVote = "poll_vote";
}

/// <summary>
/// Points (spec 023 FR-012, FR-013). Points come from configured actions (a shout-out received, a post, a vote in a named
/// poll), each person earns at most the daily cap, and the tenant can switch points off, after which no points,
/// counts or leaderboards appear anywhere and nothing more is recorded. Points have no monetary value and no
/// redemption. Leaderboards are per period and org unit, off until an administrator turns them on, and leave out
/// people who opted out. Anonymous posts and votes in anonymous polls earn nothing, since a points entry would name
/// the person.
/// </summary>
public sealed class Points(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IAuditWriter audit,
    IClock clock) : IMessageHandler<PostPublished>
{
    public const int LeaderboardSize = 20;

    public static readonly IReadOnlySet<string> Periods = new HashSet<string>(StringComparer.Ordinal) { "week", "month", "quarter" };

    private PointsSettingsView? loaded;

    public static string ETag(int version) => $"\"p{version}\"";

    private async Task<PointsSettingsView> SettingsAsync(CancellationToken ct)
    {
        if (loaded is not null)
        {
            return loaded;
        }
        var inPackage = await entitlements.IsEnabledAsync(Capabilities.EngagementPoints, ct);
        var row = await session.QuerySingleOrDefaultAsync(
            "select enabled, leaderboard, recognition_received, post, poll_vote, daily_cap, giver_daily_limit, version from engagement.points_setting", null,
            r => new PointsSettingsView(inPackage, r.GetBoolean(0), r.GetBoolean(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7)), ct);
        // Spec defaults: shout-out received 10, post 2, poll vote 1, daily cap 50, 5 counted shout-outs per giver; leaderboards off.
        loaded = row ?? new PointsSettingsView(inPackage, true, false, 10, 2, 1, 50, 5, 0);
        return loaded;
    }

    /// <summary>Whether points are on: in the package and not switched off by the tenant.</summary>
    internal async Task<bool> OnAsync(CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        return s.InPackage && s.Enabled;
    }

    internal async Task<bool> LeaderboardOnAsync(CancellationToken ct) => await OnAsync(ct) && (await SettingsAsync(ct)).Leaderboard;

    internal async Task<int> GiverDailyLimitAsync(CancellationToken ct) => (await SettingsAsync(ct)).GiverDailyLimit;

    /// <summary>Today in the tenant's time zone, which decides the daily cap and the periods.</summary>
    internal async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var zone = (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
        }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, tz).DateTime);
    }

    /// <summary>Gives a person the points for an action once per source, never past their daily cap.</summary>
    internal async Task AwardAsync(Guid userId, string action, Guid sourceId, CancellationToken ct)
    {
        if (!await OnAsync(ct))
        {
            return;
        }
        var s = await SettingsAsync(ct);
        var points = action switch
        {
            PointActions.RecognitionReceived => s.RecognitionReceived,
            PointActions.Post => s.Post,
            PointActions.PollVote => s.PollVote,
            _ => 0,
        };
        if (points <= 0)
        {
            return;
        }
        var today = await TodayAsync(ct);
        var earned = await session.ScalarAsync<long>("select coalesce(sum(points), 0) from engagement.point_entry where user_id = @userId and earned_on = @today",
            new { userId, today }, ct);
        var give = (int)Math.Min(points, s.DailyCap - earned);
        if (give <= 0)
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into engagement.point_entry (tenant_id, id, user_id, action, source_id, points, earned_on, created_at)
            values (platform.current_tenant_id(), @id, @userId, @action, @sourceId, @give, @today, @now)
            on conflict (tenant_id, action, source_id, user_id) do nothing
            """, new { id = Ids.New(), userId, action, sourceId, give, today, now = clock.UtcNow }, ct);
    }

    /// <summary>Takes back the points from a source, when a shout-out is removed.</summary>
    internal async Task RevokeAsync(string action, Guid sourceId, CancellationToken ct) =>
        await session.ExecAsync("delete from engagement.point_entry where action = @action and source_id = @sourceId", new { action, sourceId }, ct);

    /// <summary>A published post earns its author points, unless it was posted anonymously.</summary>
    public async Task HandleAsync(PostPublished message, CancellationToken ct)
    {
        var author = await session.QuerySingleOrDefaultAsync(
            "select author_id from engagement.post where id = @id and status = 'published' and not anonymous and author_id is not null",
            new { id = message.PostId }, r => (Guid?)r.GetGuid(0), ct);
        if (author is { } user)
        {
            await AwardAsync(user, PointActions.Post, message.PostId, ct);
        }
    }

    // ---- Settings ----

    public async Task<PointsSettingsView> GetSettingsAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        return await SettingsAsync(ct);
    }

    public async Task<PointsSettingsView> SetSettingsAsync(PointsSettingsRequest request, string? ifMatch, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.EngagementPoints, ct);
        var current = await SettingsAsync(ct);
        if (ifMatch != ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.points_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(current.Version) });
        }
        var errors = new ValidationErrors();
        int Check(int? value, int fallback, int min, int max, string field)
        {
            var v = value ?? fallback;
            if (v < min || v > max)
            {
                errors.Add(field, "validation.engagement.points_value");
            }
            return v;
        }
        var received = Check(request.RecognitionReceived, current.RecognitionReceived, 0, 1000, "recognitionReceived");
        var post = Check(request.Post, current.Post, 0, 1000, "post");
        var vote = Check(request.PollVote, current.PollVote, 0, 1000, "pollVote");
        var cap = Check(request.DailyCap, current.DailyCap, 1, 10000, "dailyCap");
        var giver = Check(request.GiverDailyLimit, current.GiverDailyLimit, 1, 100, "giverDailyLimit");
        errors.ThrowIfAny();
        var enabled = request.Enabled ?? current.Enabled;
        var leaderboard = request.Leaderboard ?? current.Leaderboard;
        var version = current.Version + 1;
        await session.ExecAsync(
            """
            insert into engagement.points_setting (tenant_id, enabled, leaderboard, recognition_received, post, poll_vote, daily_cap, giver_daily_limit, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @enabled, @leaderboard, @received, @post, @vote, @cap, @giver, @version, @user, @now)
            on conflict (tenant_id) do update set enabled = excluded.enabled, leaderboard = excluded.leaderboard, recognition_received = excluded.recognition_received,
              post = excluded.post, poll_vote = excluded.poll_vote, daily_cap = excluded.daily_cap, giver_daily_limit = excluded.giver_daily_limit,
              version = excluded.version, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { enabled, leaderboard, received, post, vote, cap, giver, version, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementPointsConfigChanged, "points_setting", null,
            Changes: new { version, enabled, leaderboard, recognitionReceived = received, post, pollVote = vote, dailyCap = cap, giverDailyLimit = giver }), ct);
        loaded = new PointsSettingsView(current.InPackage, enabled, leaderboard, received, post, vote, cap, giver, version);
        return loaded;
    }

    // ---- Reading ----

    private async Task RequireOnAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.EngagementRecognition, ct);
        // Switched off, points do not exist for anyone: not found rather than zero (US4 scenario 4).
        if (!await OnAsync(ct))
        {
            throw AppException.NotFound();
        }
    }

    public async Task<MyPoints> MineAsync(CancellationToken ct)
    {
        await RequireOnAsync(ct);
        var user = context.RequireUser();
        var today = await TodayAsync(ct);
        var month = new DateOnly(today.Year, today.Month, 1);
        var sums = await session.QuerySingleOrDefaultAsync(
            """
            select coalesce(sum(points) filter (where earned_on = @today), 0)::int, coalesce(sum(points) filter (where earned_on >= @month), 0)::int, coalesce(sum(points), 0)::int
            from engagement.point_entry where user_id = @user
            """, new { user, today, month }, r => (Today: r.GetInt32(0), Month: r.GetInt32(1), Total: r.GetInt32(2)), ct);
        var s = await SettingsAsync(ct);
        return new MyPoints(sums.Today, sums.Month, sums.Total, s.DailyCap, s.Leaderboard);
    }

    internal static (DateOnly From, DateOnly To) PeriodOf(string period, DateOnly today) => period switch
    {
        // Weeks start on Sunday, the working week in the launch markets.
        "week" => (today.AddDays(-(int)today.DayOfWeek), today.AddDays(-(int)today.DayOfWeek + 6)),
        "quarter" => (new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1), new DateOnly(today.Year, ((today.Month - 1) / 3 * 3) + 1, 1).AddMonths(3).AddDays(-1)),
        _ => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
    };

    public async Task<LeaderboardView> LeaderboardAsync(string? period, Guid? unit, CancellationToken ct)
    {
        await RequireOnAsync(ct);
        if (!(await SettingsAsync(ct)).Leaderboard)
        {
            throw AppException.NotFound();
        }
        await authz.DemandAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct);
        var p = period ?? "month";
        if (!Periods.Contains(p))
        {
            throw AppException.Validation("period", "validation.engagement.leaderboard_period");
        }
        IReadOnlySet<Guid>? members = null;
        if (unit is { } u)
        {
            if (!await hierarchy.UnitExistsAsync(u, ct))
            {
                throw AppException.Validation("unit", "validation.engagement.leaderboard_unit");
            }
            members = await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync([u], ct), ct);
        }
        var (from, to) = PeriodOf(p, await TodayAsync(ct));
        var rows = await session.QueryAsync(
            """
            select e.user_id, sum(e.points)::int as total from engagement.point_entry e
            where e.earned_on between @from and @to
              and (@members::uuid[] is null or e.user_id = any(@members))
              and not exists (select 1 from engagement.recognition_preference p where p.user_id = e.user_id and p.leaderboard_opt_out)
            group by e.user_id order by total desc, e.user_id limit 100
            """, new { from, to, members = members?.ToArray() }, r => (User: r.GetGuid(0), Total: r.GetInt32(1)), ct);
        var people = await users.GetManyAsync(rows.Select(r => r.User).ToList(), ct);
        var entries = rows.Where(r => people.TryGetValue(r.User, out var x) && x.Status == "active").Take(LeaderboardSize)
            .Select((r, i) => new LeaderboardEntry(i + 1, new Person(r.User, people[r.User].DisplayName, true), r.Total)).ToList();
        return new LeaderboardView(p, from, to, unit, entries);
    }
}
