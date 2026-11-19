using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>A headline figure of an area dashboard; null when hidden behind the minimum group size or when there was nothing to count.</summary>
public sealed record AreaFigure(string Key, double? Value, bool Share = false);

/// <summary>
/// One row of an area section. <see cref="Key"/> is an ISO week start, a stable key or an ID; <see cref="Names"/> or
/// <see cref="Label"/> name it when it has a name. Null figures are hidden; the Other row gathers groups that are too small.
/// </summary>
public sealed record AreaRow(string Key, IReadOnlyDictionary<string, string>? Names, string? Label, DateOnly? Date, long? Count, long? People, double? Amount, double? Rate);

/// <summary>
/// One table of an area dashboard. <see cref="Group"/> says how rows are named: <c>week</c>, <c>key</c> (a fixed key with a
/// resource message), <c>name</c> (names per language, else a key) or <c>label</c> (text as written). <see cref="Columns"/>
/// lists the figures shown, from <c>count</c>, <c>people</c>, <c>amount</c>, <c>rate</c> and <c>date</c>. <see cref="Link"/>
/// is a page per row, with <c>{key}</c> replaced; the target page authorizes again.
/// </summary>
public sealed record AreaSection(string Key, string Group, IReadOnlyList<string> Columns, IReadOnlyList<AreaRow> Rows, string? Link = null);

/// <summary>
/// The knowledge, engagement, HR and security trend dashboards (spec 021 section 2, FR-002), one shape for all four:
/// headline figures and sections, each read from the module that owns the data.
/// </summary>
public sealed record AreaView(string Area, DateOnly From, DateOnly To, int MinGroupSize, IReadOnlyList<AreaFigure> Totals, IReadOnlyList<AreaSection> Sections);

public static class AreaGroups
{
    public const string Week = "week";
    public const string Key = "key";
    public const string Name = "name";
    public const string Label = "label";
}

/// <summary>
/// The area dashboards under <c>analytics.advanced</c>. Who may see each follows section 3: tenant wide
/// <c>analytics.view</c> for all four; also knowledge editors (<c>kb.edit</c> anywhere) for knowledge, HR administrators
/// of the whole tenant (<c>hr.manage</c>) for HR, and security officers (<c>security.view</c>) for security trends.
/// Groups of fewer people than the minimum group size show no figures; small named groups fold into Other with
/// complementary suppression, as on the AI usage dashboard.
/// </summary>
public sealed class AreaDashboards(IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, AnalyticsPolicies policies,
    IDbSession session, IKnowledgeFigures knowledge, IAssistantKnowledgeFigures questions, IEngagementFigures engagement, IHrFigures hr,
    ISecurityTrendFigures security, IGuestFigures guests, IClock clock)
{
    public const string Knowledge = "knowledge";
    public const string Engagement = "engagement";
    public const string Hr = "hr";
    public const string Security = "security";
    public const int DefaultDays = 30;
    public const int TopRows = 25;
    public const int ReviewWindowDays = 30;

    private async Task<(DateOnly From, DateOnly To, int Min)> PrepareAsync(DateOnly? from, DateOnly? to, Func<Task<bool>> allowed, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed && !await allowed())
        {
            throw AppException.Denied();
        }
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (start > end || end.DayNumber - start.DayNumber > Workspace.MaxDays)
        {
            throw AppException.Validation("from", "validation.analytics.range");
        }
        return (start, end, (await policies.GetAsync(ct)).MinGroupSize);
    }

    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public async Task<AreaView> KnowledgeAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (start, end, min) = await PrepareAsync(from, to, async () => (await authz.CoverageAsync(Permissions.KbEdit, ct)).Any, ct);
        var articles = await knowledge.ArticlesAsync(ct);
        var ids = articles.Select(a => a.ItemId).ToArray();
        var reach = (await session.QueryAsync(
            """
            select item_id, sum(views)::bigint, sum(readers)::bigint from analytics.item_daily
            where source = 'content' and item_id = any(@ids) and day >= @start and day <= @end group by 1
            """, new { ids, start, end }, r => (Id: r.GetGuid(0), Views: r.GetInt64(1), Readers: r.GetInt64(2)), ct)).ToDictionary(r => r.Id);
        var asked = await questions.SummaryAsync(Start(start), Start(end.AddDays(1)), TopRows, ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var ratings = articles.Sum(a => a.Ratings);
        var shownQuestions = Shown(asked.People, min);
        var shownUnanswered = Shown(asked.UnansweredPeople, min);
        var rows = articles
            .Select(a => (Article: a, Reach: reach.GetValueOrDefault(a.ItemId)))
            .OrderByDescending(x => x.Reach.Views).ThenByDescending(x => x.Article.Ratings).ThenBy(x => x.Article.ItemId).Take(TopRows)
            .Select(x => new AreaRow(x.Article.ItemId.ToString(), x.Article.Titles, null, null, x.Reach.Views, x.Reach.Readers, x.Article.Ratings,
                x.Article.Ratings >= min ? AiUsageAnalytics.Share(x.Article.Helpful, x.Article.Ratings) : null))
            .ToList();
        var reviews = articles.Where(a => a.ReviewDueAt is { } due && DateOnly.FromDateTime(due.UtcDateTime) <= today.AddDays(ReviewWindowDays))
            .OrderBy(a => a.ReviewDueAt).Take(TopRows)
            .Select(a => new AreaRow(a.ItemId.ToString(), a.Titles, null, DateOnly.FromDateTime(a.ReviewDueAt!.Value.UtcDateTime), null, null, null, null))
            .ToList();
        var cited = Fold(asked.Cited.Select(c => new GroupFigure(c.ItemId.ToString(), c.Answers, c.People, Label: c.Title)).ToList(), min);
        return new AreaView(Knowledge, start, end, min,
        [
            new("articles", articles.Count),
            new("ratings", ratings),
            new("helpfulRate", Shown(ratings, min) ? AiUsageAnalytics.Share(articles.Sum(a => a.Helpful), ratings) : null, true),
            new("reviewsDue", articles.Count(a => a.ReviewDueAt is { } due && DateOnly.FromDateTime(due.UtcDateTime) <= today)),
            new("questions", shownQuestions ? asked.Questions : null),
            new("unanswered", shownUnanswered ? asked.Unanswered : null),
            new("unansweredRate", shownQuestions && shownUnanswered ? AiUsageAnalytics.Share(asked.Unanswered, asked.Questions) : null, true),
        ],
        [
            new("articles", AreaGroups.Name, ["count", "people", "amount", "rate"], rows, "/content/read/{key}"),
            new("reviews", AreaGroups.Name, ["date"], reviews, "/content/items/{key}"),
            new("unansweredWeeks", AreaGroups.Week, ["count", "people"], Weeks(asked.UnansweredWeeks, min)),
            new("outcomes", AreaGroups.Key, ["count", "people"], Fold(asked.Outcomes, min)),
            new("cited", AreaGroups.Label, ["count", "people"], cited),
        ]);
    }

    public async Task<AreaView> EngagementAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (start, end, min) = await PrepareAsync(from, to, () => Task.FromResult(false), ct);
        var e = await engagement.SummaryAsync(Start(start), Start(end.AddDays(1)), TopRows, ct);
        var active = Shown(e.Contributors, min);
        var recognized = Shown(e.Recognized, min);
        return new AreaView(Engagement, start, end, min,
        [
            new("posts", active ? e.Posts : null),
            new("comments", active ? e.Comments : null),
            new("reactions", active ? e.Reactions : null),
            new("contributors", active ? e.Contributors : null),
            new("recognitions", recognized ? e.Recognitions : null),
            new("recognized", recognized ? e.Recognized : null),
            new("polls", e.Polls),
            new("pollVoters", Shown(e.PollVoters, min) ? e.PollVoters : null),
        ],
        [
            new("activityWeeks", AreaGroups.Week, ["count", "people"], Weeks(e.ActivityWeeks, min)),
            new("communities", AreaGroups.Name, ["count", "people"], Fold(e.Communities, min), "/communities/{key}"),
            new("recognitionWeeks", AreaGroups.Week, ["count", "people"], Weeks(e.RecognitionWeeks, min)),
            new("recognitionValues", AreaGroups.Name, ["count", "people"], Fold(e.RecognitionValues, min)),
            new("surveys", AreaGroups.Label, ["count"], Fold(e.Surveys, min)),
        ]);
    }

    public async Task<AreaView> HrAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (start, end, min) = await PrepareAsync(from, to, async () => (await authz.CheckAsync(Permissions.HrManage, AuthzTarget.Tenant, ct)).Allowed, ct);
        var h = await hr.SummaryAsync(Start(start), Start(end.AddDays(1)), ct);
        var leave = Shown(h.LeavePeople, min);
        var exits = Shown(h.ExitInterviews, min);
        return new AreaView(Hr, start, end, min,
        [
            new("leaveRequests", leave ? h.LeaveRequests : null),
            new("leavePeople", leave ? h.LeavePeople : null),
            new("leaveDays", leave ? h.LeaveDays : null),
            new("onboardingJourneys", Sum(h.Onboarding, min)),
            new("exitInterviews", exits ? h.ExitInterviews : null),
        ],
        [
            new("leaveTypes", AreaGroups.Name, ["count", "people", "amount"], Fold(h.LeaveTypes, min)),
            new("leaveWeeks", AreaGroups.Week, ["count", "people", "amount"], Weeks(h.LeaveWeeks, min)),
            new("attendance", AreaGroups.Key, ["count", "people"], Fold(h.Attendance, min)),
            new("onboarding", AreaGroups.Name, ["count", "rate"], Fold(h.Onboarding, min, rate: true)),
            new("exitReasons", AreaGroups.Key, ["count"], exits ? Fold(h.ExitReasons, min) : []),
            new("requests", AreaGroups.Key, ["count", "people"], Fold(h.Requests, min)),
        ]);
    }

    public async Task<AreaView> SecurityAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var (start, end, min) = await PrepareAsync(from, to, async () => (await authz.CheckAsync(Permissions.SecurityView, AuthzTarget.Tenant, ct)).Allowed
            || (await authz.CheckAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct)).Allowed, ct);
        var s = await security.SummaryAsync(Start(start), Start(end.AddDays(1)), ct);
        var g = await guests.SummaryAsync(Start(start), Start(end.AddDays(1)), ct);
        // Alerts and guests are counts of events and accounts the security pages already list; they are not hidden.
        static AreaRow Plain(GroupFigure f) => new(f.Key, null, null, null, f.Count, null, null, null);
        return new AreaView(Security, start, end, min,
        [
            new("alerts", s.Alerts),
            new("openAlerts", s.OpenAlerts),
            new("guests", g.Active),
            new("guestsAdded", g.AddedWeeks.Sum(w => w.Count)),
            new("weakerChecks", s.WeakerChecks.Count),
            new("checks", s.Checks),
        ],
        [
            new("alertWeeks", AreaGroups.Week, ["count"], [.. s.AlertWeeks.Select(Plain)]),
            new("alertKinds", AreaGroups.Key, ["count"], [.. s.AlertKinds.Select(Plain)], "/admin/security/alerts"),
            new("guestWeeks", AreaGroups.Week, ["count"], [.. g.AddedWeeks.Select(Plain)]),
            new("weakerChecks", AreaGroups.Key, [], [.. s.WeakerChecks.Select(k => new AreaRow(k, null, null, null, null, null, null, null))], "/admin/security/posture"),
        ]);
    }

    /// <summary>A figure is shown when nobody is behind it (it is zero) or at least <paramref name="min"/> people are.</summary>
    internal static bool Shown(long people, int min) => people == 0 || people >= min;

    private static long? Sum(IReadOnlyList<GroupFigure> groups, int min) =>
        Shown(groups.Sum(g => g.People), min) ? groups.Sum(g => g.Count) : null;

    /// <summary>Weeks stay in order; a week of fewer people than <paramref name="min"/> shows no figures.</summary>
    internal static IReadOnlyList<AreaRow> Weeks(IReadOnlyList<GroupFigure> weeks, int min) =>
        [.. weeks.Select(w => w.People >= min
            ? new AreaRow(w.Key, null, null, null, w.Count, w.People, w.Amount, null)
            : new AreaRow(w.Key, null, null, null, null, null, null, null))];

    /// <summary>
    /// Groups of fewer people than <paramref name="min"/> fold into Other; when Other would hold one group, the next
    /// smallest joins it so the small one cannot be worked out from the totals. Other shows its count only, since people
    /// across groups may overlap, and nothing when it holds a single group. With <paramref name="rate"/>, the amount is a
    /// share and goes to the rate column.
    /// </summary>
    internal static IReadOnlyList<AreaRow> Fold(IReadOnlyList<GroupFigure> rows, int min, bool rate = false)
    {
        var small = rows.Where(r => r.People < min).ToList();
        var large = rows.Where(r => r.People >= min).OrderBy(r => r.Count).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        if (small.Count == 1 && large.Count > 0)
        {
            small.Add(large[0]);
            large.RemoveAt(0);
        }
        var groups = large.OrderByDescending(r => r.Count).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new AreaRow(r.Key, r.Names, r.Label, null, r.Count, r.People, rate ? null : r.Amount, rate ? r.Amount : null)).ToList();
        if (small.Count > 0)
        {
            groups.Add(new AreaRow(ItemAnalytics.Other, null, null, null, small.Count > 1 ? small.Sum(r => r.Count) : null, null, null, null));
        }
        return groups;
    }
}
