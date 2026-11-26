using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

public sealed record ExperienceTemplateRow(string Template, string Version, int Pages, int Published);

public sealed record ExperienceCount(string Key, int Pages);

/// <summary>Pages per template owned by one unit; <see cref="Unit"/> is <c>none</c> for pages of the whole tenant.</summary>
public sealed record ExperienceScopeRow(string Unit, IReadOnlyDictionary<string, string> Names, IReadOnlyList<ExperienceCount> Templates);

/// <summary>People who used the analytics in the period and of them who changed or reset a personal layout; null under the minimum.</summary>
public sealed record PersonalizationCounts(int People, int? Customized, int? Reset);

public sealed record PersonalizationGroup(string Group, IReadOnlyDictionary<string, string> Names, PersonalizationCounts Counts);

public sealed record PersonalizationReport(bool Enabled, PersonalizationCounts Total, IReadOnlyList<PersonalizationGroup> Units);

/// <summary>p75 of the Core Web Vitals over the samples of one group; a vital with no sample is null.</summary>
public sealed record VitalsRow(string Dimension, string Group, int Samples, double? Lcp, double? Inp, double? Cls);

/// <summary>A published page variant (page, release, device class) whose p75 misses a spec 032 target.</summary>
public sealed record VitalsFlag(Guid PageId, string Template, int? Release, string DeviceClass, int Samples, double? Lcp, double? Inp, double? Cls, IReadOnlyList<string> Missed);

public sealed record PerformanceReport(int Samples, IReadOnlyList<VitalsRow> Rows, IReadOnlyList<VitalsFlag> Flags);

/// <summary>A task flow on the outcome event contract; the rate is null when fewer people than the minimum started it.</summary>
public sealed record OutcomeFlow(string Flow, int Started, int Completed, int Abandoned, double? CompletionRate, string? DropOffStep);

/// <summary>
/// Usefulness of one personalized or recommending surface (spec 021 R3, FR-024): people it was shown to, and of them who
/// opened or dismissed something; the counts and rate are null when fewer people than the minimum saw it.
/// </summary>
public sealed record RecommendationUsefulness(string Surface, int People, int? Opened, int? Dismissed, double? OpenRate);

public sealed record ExperienceAnalyticsView(DateOnly From, DateOnly To, int MinGroupSize, IReadOnlyList<ExperienceTemplateRow> Templates,
    IReadOnlyList<ExperienceCount> Layouts, IReadOnlyList<ExperienceScopeRow> Scopes, PersonalizationReport Personalization, PerformanceReport Performance,
    IReadOnlyList<OutcomeFlow> Outcomes, IReadOnlyList<RecommendationUsefulness>? Recommendations = null);

/// <summary>
/// Experience dashboards (spec 021 US9, FR-017 to FR-021, T015): template usage, layout distribution and template version
/// adoption by owning unit from the spec 032 page inventory; personalization enabled against use and resets with the
/// minimum group size; p75 LCP, INP and CLS by device class, theme, unit, page type and release with published variants
/// that miss the spec 032 targets flagged; and task flows on the one outcome event contract. For whole tenant viewers,
/// with <c>analytics.experience</c>.
/// </summary>
public sealed class ExperienceAnalytics(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context,
    AnalyticsPolicies policies, ITemplateUsage templates, IOrganizationHierarchy organization, IClock clock)
{
    public const int DefaultDays = 30;

    /// <summary>The fewest samples behind a flagged variant, so one slow visit does not flag a page.</summary>
    public const int MinSamples = 5;

    /// <summary>The most vitals samples read for one view.</summary>
    public const int MaxSamples = 200_000;

    /// <summary>Spec 032 FR-058 targets, at p75.</summary>
    public const double LcpTarget = 2500, InpTarget = 200, ClsTarget = 0.1;

    public async Task<ExperienceAnalyticsView> GetAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExperience, ct);
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
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
        var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var min = (await policies.GetAsync(ct)).MinGroupSize;

        var pages = await templates.PagesAsync(ct);
        var templateRows = pages.GroupBy(p => (p.TemplateId, p.TemplateVersion)).OrderBy(g => g.Key.TemplateId, StringComparer.Ordinal).ThenBy(g => g.Key.TemplateVersion, StringComparer.Ordinal)
            .Select(g => new ExperienceTemplateRow(g.Key.TemplateId, g.Key.TemplateVersion, g.Count(), g.Count(p => p.Release is not null))).ToList();
        var layouts = pages.GroupBy(p => p.Layout ?? ItemAnalytics.NoUnit, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ExperienceCount(g.Key, g.Count())).ToList();
        var unitIds = pages.Select(p => p.OrgUnitId).OfType<Guid>().Distinct().ToList();
        var personalization = await PersonalizationAsync(since, until, min, ct);
        var groupUnits = personalization.Units.Select(u => Guid.TryParse(u.Group, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty);
        var names = await organization.GetNamesAsync(unitIds.Concat(groupUnits).Distinct().ToList(), ct);
        var none = new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> Name(string group) => Guid.TryParse(group, out var id) ? names.GetValueOrDefault(id) ?? none : none;
        var scopes = pages.GroupBy(p => p.OrgUnitId?.ToString() ?? ItemAnalytics.NoUnit, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ExperienceScopeRow(g.Key, Name(g.Key), g.GroupBy(p => p.TemplateId, StringComparer.Ordinal).OrderByDescending(t => t.Count())
                .ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => new ExperienceCount(t.Key, t.Count())).ToList())).ToList();
        personalization = personalization with { Units = personalization.Units.Select(u => u with { Names = Name(u.Group) }).ToList() };
        var performance = await PerformanceAsync(pages, since, until, ct);
        var outcomes = await OutcomesAsync(since, until, min, ct);
        var recommendations = await RecommendationsAsync(since, until, min, ct);
        return new ExperienceAnalyticsView(start, end, min, templateRows, layouts, scopes, personalization, performance, outcomes, recommendations);
    }

    /// <summary>
    /// Per surface, the people something was shown to and, of them, who opened or dismissed it. Counted by person, not by
    /// event, so nobody weighs more for using it often, and never broken down further than the surface.
    /// </summary>
    private async Task<IReadOnlyList<RecommendationUsefulness>> RecommendationsAsync(DateTimeOffset from, DateTimeOffset to, int min, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select surface, count(*) filter (where shown), count(*) filter (where shown and opened), count(*) filter (where shown and dismissed)
            from (
              select fields->>'surface' as surface, user_key,
                     bool_or(fields->>'outcome' = 'shown') as shown, bool_or(fields->>'outcome' = 'opened') as opened,
                     bool_or(fields->>'outcome' = 'dismissed') as dismissed
              from analytics.event where name = @name and user_key is not null and occurred_at >= @from and occurred_at < @to
              group by 1, 2) people
            group by surface
            """, new { name = AnalyticsEvents.RecommendationOutcome, from, to },
            r => (Surface: r.GetString(0), Shown: (int)r.GetInt64(1), Opened: (int)r.GetInt64(2), Dismissed: (int)r.GetInt64(3)), ct);
        return [.. AnalyticsEvents.RecommendationSurfaces.Order(StringComparer.Ordinal).Select(surface =>
        {
            var row = rows.FirstOrDefault(r => r.Surface == surface);
            return row.Shown < min || row.Shown == 0
                ? new RecommendationUsefulness(surface, row.Shown, null, null, null)
                : new RecommendationUsefulness(surface, row.Shown, row.Opened, row.Dismissed, Math.Round((double)row.Opened / row.Shown, 3));
        })];
    }

    private sealed record Personal(string Unit, bool Customized, bool Reset);

    private async Task<PersonalizationReport> PersonalizationAsync(DateTimeOffset from, DateTimeOffset to, int min, CancellationToken ct)
    {
        var enabled = await entitlements.IsEnabledAsync(Capabilities.ExperiencePersonalization, ct);
        // Each person once, in the unit of their latest event in the period.
        var people = await session.QueryAsync(
            """
            select (array_agg(org_unit_id::text order by occurred_at desc))[1],
                   bool_or(name = @changed and fields->>'action' = 'customize'), bool_or(name = @changed and fields->>'action' = 'reset')
            from analytics.event where user_key is not null and occurred_at >= @from and occurred_at < @to group by user_key
            """, new { from, to, changed = AnalyticsEvents.PersonalizationChanged },
            r => new Personal(r.IsDBNull(0) ? ItemAnalytics.NoUnit : r.GetString(0), r.GetBoolean(1), r.GetBoolean(2)), ct);
        var merged = ItemAnalytics.Merge(people.GroupBy(p => p.Unit, StringComparer.Ordinal), min);
        var none = new Dictionary<string, string>();
        var groups = merged.Where(g => g.Value.Count >= min)
            .OrderBy(g => g.Key == ItemAnalytics.Other).ThenByDescending(g => g.Value.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PersonalizationGroup(g.Key, none, Counts(g.Value, min))).ToList();
        return new PersonalizationReport(enabled, Counts(people, min), groups);
    }

    private static PersonalizationCounts Counts(IReadOnlyCollection<Personal> people, int min) =>
        people.Count < min ? new(people.Count, null, null) : new(people.Count, people.Count(p => p.Customized), people.Count(p => p.Reset));

    private sealed record Sample(Guid PageId, int? Release, string Device, string? Theme, double? Lcp, double? Inp, double? Cls);

    private async Task<PerformanceReport> PerformanceAsync(IReadOnlyList<PageInventoryItem> pages, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            $"select fields::text from analytics.event where name = @name and occurred_at >= @from and occurred_at < @to order by occurred_at desc limit {MaxSamples}",
            new { name = AnalyticsEvents.PageVitals, from, to }, r => r.GetString(0), ct);
        var samples = new List<Sample>(rows.Count);
        foreach (var json in rows)
        {
            // Fields are stored as the writer checked them: numbers as numbers, the rest as strings.
            var f = (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [])
                .ToDictionary(p => p.Key, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
            if (!Guid.TryParse(f.GetValueOrDefault("pageId"), out var page) || f.GetValueOrDefault("deviceClass") is not { } device)
            {
                continue;
            }
            samples.Add(new Sample(page, int.TryParse(f.GetValueOrDefault("release"), out var release) ? release : null, device, f.GetValueOrDefault("theme"),
                Number(f, "lcp"), Number(f, "inp"), Number(f, "cls")));
        }
        return Performance(pages, samples.Select(s => (s.PageId, s.Release, s.Device, s.Theme, s.Lcp, s.Inp, s.Cls)).ToList());
    }

    private static double? Number(Dictionary<string, string> f, string key) =>
        double.TryParse(f.GetValueOrDefault(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>p75 by device class, theme, unit, page type and release, and the published variants that miss a target.</summary>
    internal static PerformanceReport Performance(IReadOnlyList<PageInventoryItem> pages,
        IReadOnlyList<(Guid PageId, int? Release, string Device, string? Theme, double? Lcp, double? Inp, double? Cls)> samples)
    {
        var byId = pages.ToDictionary(p => p.PageId);
        var known = samples.Where(s => byId.ContainsKey(s.PageId)).ToList();
        // Nearest rank, like the service analytics; milliseconds to the whole, CLS to three places.
        static double? P75(IEnumerable<double?> values)
        {
            var list = values.OfType<double>().Order().ToList();
            return list.Count == 0 ? null : Math.Round(list[Math.Clamp((int)Math.Ceiling(0.75 * list.Count) - 1, 0, list.Count - 1)], 3);
        }
        var dimensions = new (string Name, Func<(Guid PageId, int? Release, string Device, string? Theme, double? Lcp, double? Inp, double? Cls), string> Key)[]
        {
            ("device", s => s.Device),
            ("theme", s => s.Theme ?? ItemAnalytics.NoUnit),
            ("unit", s => byId[s.PageId].OrgUnitId?.ToString() ?? ItemAnalytics.NoUnit),
            ("template", s => byId[s.PageId].TemplateId),
            ("release", s => s.Release?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ItemAnalytics.NoUnit),
        };
        var rows = dimensions.SelectMany(d => known.GroupBy(d.Key, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new VitalsRow(d.Name, g.Key, g.Count(), P75(g.Select(s => s.Lcp)), P75(g.Select(s => s.Inp)), P75(g.Select(s => s.Cls))))).ToList();
        var flags = new List<VitalsFlag>();
        foreach (var g in known.Where(s => s.Release is not null && s.Release == byId[s.PageId].Release).GroupBy(s => (s.PageId, s.Release, s.Device)))
        {
            if (g.Count() < MinSamples)
            {
                continue;
            }
            var (lcp, inp, cls) = (P75(g.Select(s => s.Lcp)), P75(g.Select(s => s.Inp)), P75(g.Select(s => s.Cls)));
            var missed = new List<string>();
            if (lcp > LcpTarget) missed.Add("lcp");
            if (inp > InpTarget) missed.Add("inp");
            if (cls > ClsTarget) missed.Add("cls");
            if (missed.Count > 0)
            {
                flags.Add(new VitalsFlag(g.Key.PageId, byId[g.Key.PageId].TemplateId, g.Key.Release, g.Key.Device, g.Count(), lcp, inp, cls, missed));
            }
        }
        return new PerformanceReport(known.Count, rows, flags.OrderByDescending(f => f.Missed.Count).ThenByDescending(f => f.Lcp).ToList());
    }

    private async Task<IReadOnlyList<OutcomeFlow>> OutcomesAsync(DateTimeOffset from, DateTimeOffset to, int min, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select fields->>'flow', fields->>'outcome', fields->>'step', count(*), count(distinct user_key) from analytics.event
            where name = @name and occurred_at >= @from and occurred_at < @to group by 1, 2, 3
            """, new { name = AnalyticsEvents.TaskOutcome, from, to },
            r => (Flow: r.GetString(0), Outcome: r.GetString(1), Step: r.IsDBNull(2) ? null : r.GetString(2), Events: (int)r.GetInt64(3), People: (int)r.GetInt64(4)), ct);
        var starters = await session.QueryAsync(
            """
            select fields->>'flow', count(distinct user_key) from analytics.event
            where name = @name and occurred_at >= @from and occurred_at < @to and fields->>'outcome' = 'start' group by 1
            """, new { name = AnalyticsEvents.TaskOutcome, from, to }, r => (Flow: r.GetString(0), People: (int)r.GetInt64(1)), ct);
        return Flows(rows.Select(r => (r.Flow, r.Outcome, r.Step, r.Events)).ToList(), starters.ToDictionary(s => s.Flow, s => s.People, StringComparer.Ordinal), min);
    }

    /// <summary>Counts per flow; the completion rate needs the minimum of people starting it, and the drop-off step the minimum of abandons there.</summary>
    internal static IReadOnlyList<OutcomeFlow> Flows(IReadOnlyList<(string Flow, string Outcome, string? Step, int Events)> rows, IReadOnlyDictionary<string, int> starters, int min) =>
        rows.GroupBy(r => r.Flow, StringComparer.Ordinal).Select(g =>
        {
            int Sum(string outcome) => g.Where(r => r.Outcome == outcome).Sum(r => r.Events);
            var (started, completed, abandoned) = (Sum("start"), Sum("complete"), Sum("abandon"));
            var rate = starters.GetValueOrDefault(g.Key) >= min && started > 0 ? Math.Round(Math.Min(1, (double)completed / started), 4) : (double?)null;
            var drop = g.Where(r => r.Outcome == "abandon" && r.Step is not null).GroupBy(r => r.Step!, StringComparer.Ordinal)
                .Select(s => (Step: s.Key, Count: s.Sum(r => r.Events))).Where(s => s.Count >= min).OrderByDescending(s => s.Count).ThenBy(s => s.Step, StringComparer.Ordinal)
                .Select(s => s.Step).FirstOrDefault();
            return new OutcomeFlow(g.Key, started, completed, abandoned, rate, drop);
        }).OrderByDescending(f => f.Started).ThenBy(f => f.Flow, StringComparer.Ordinal).ToList();
}
