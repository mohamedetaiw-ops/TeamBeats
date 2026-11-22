using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// The search dashboard (spec 021 US3, FR-007): searches, searches with no results and with a click, and the top, zero
/// result and no click queries for the period and locale. Queries come from the spec 012 feed, which lists a query only
/// when at least five people searched for it and never when it looks like personal data or the tenant turned query
/// text off. The flags say which follow-up actions the viewer may take; the target pages authorize again.
/// </summary>
public sealed record SearchGapsView(
    DateOnly From,
    DateOnly To,
    string? Locale,
    int MinPeople,
    int Searches,
    int ZeroResults,
    int Clicked,
    double ClickThrough,
    bool QueryTextHidden,
    IReadOnlyList<SearchQueryRow> ZeroResultQueries,
    IReadOnlyList<SearchQueryRow> NoClickQueries,
    IReadOnlyList<SearchQueryRow> TopQueries,
    bool CanAddBestBet,
    bool CanWriteArticle,
    Guid? UnitId = null,
    IReadOnlyDictionary<string, string>? UnitNames = null,
    int Refined = 0,
    IReadOnlyList<SearchUnitGroup>? Units = null,
    IReadOnlyList<SearchRefinementRow>? Refinements = null);

/// <summary>
/// Searches by the searcher's primary unit at search time (spec 021 section 2). <see cref="Key"/> is the unit ID,
/// <c>none</c> for people with no unit, or <c>other</c> for units of fewer than five people folded together, which
/// shows its searches only and nothing when it holds a single unit.
/// </summary>
public sealed record SearchUnitGroup(string Key, IReadOnlyDictionary<string, string>? Names, int? Searches, int? People, int? ZeroResults, int? Clicks);

/// <summary>
/// Search gaps for tenant wide analytics viewers, search administrators and knowledge editors (section 3), under
/// <c>analytics.advanced</c>. Query text is tenant wide, so a scoped analytics viewer is refused.
/// </summary>
public sealed class SearchGaps(IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, ISearchAnalyticsFeed feed, IOrganizationHierarchy hierarchy, IClock clock)
{
    public const int DefaultDays = 30;
    public const string NoUnit = "none";

    public async Task<SearchGapsView> GetAsync(DateOnly? from, DateOnly? to, string? locale, CancellationToken ct, Guid? unitId = null)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
        var bestBets = (await authz.CheckAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct)).Allowed;
        var articles = (await authz.CoverageAsync(Permissions.KbEdit, ct)).Any;
        if (!bestBets && !articles && !(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        if (locale is not null && (locale.Length != 2 || !locale.All(char.IsAsciiLetterLower)))
        {
            throw AppException.Validation("locale", "validation.analytics.locale");
        }
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (start > end || end.DayNumber - start.DayNumber > Workspace.MaxDays)
        {
            throw AppException.Validation("from", "validation.analytics.range");
        }
        // A unit filter covers the unit and the units under it.
        IReadOnlyCollection<Guid>? units = null;
        IReadOnlyDictionary<string, string>? unitNames = null;
        if (unitId is { } unit)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                throw AppException.NotFound();
            }
            units = await hierarchy.GetSubtreesAsync([unit], ct);
            unitNames = (await hierarchy.GetNamesAsync([unit], ct)).GetValueOrDefault(unit);
        }
        var report = await feed.ReportAsync(new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), locale, ct, units);
        var rows = report.Units ?? [];
        var names = await hierarchy.GetNamesAsync(rows.Where(r => r.UnitId is not null).Select(r => r.UnitId!.Value), ct);
        return new SearchGapsView(start, end, locale, MinPeople, report.Searches, report.ZeroResults, report.Clicked, report.ClickThrough, report.QueryTextHidden,
            report.ZeroResultQueries, report.NoClickQueries, report.TopQueries, bestBets, articles, unitId, unitNames, report.Refined,
            FoldUnits(rows, names), report.Refinements ?? []);
    }

    public const int MinPeople = 5;

    /// <summary>
    /// Units searched by fewer than <see cref="MinPeople"/> people fold into Other; when only one would, the next smallest
    /// joins it so the small one cannot be worked out from the totals (complementary suppression).
    /// </summary>
    internal static IReadOnlyList<SearchUnitGroup> FoldUnits(IReadOnlyList<SearchUnitRow> rows, IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> names)
    {
        var small = rows.Where(r => r.People < MinPeople).ToList();
        var large = rows.Where(r => r.People >= MinPeople).OrderBy(r => r.Searches).ThenBy(r => r.UnitId).ToList();
        if (small.Count == 1 && large.Count > 0)
        {
            small.Add(large[0]);
            large.RemoveAt(0);
        }
        var groups = large.OrderByDescending(r => r.Searches).ThenBy(r => r.UnitId)
            .Select(r => new SearchUnitGroup(r.UnitId?.ToString() ?? NoUnit, r.UnitId is { } u ? names.GetValueOrDefault(u) : null, r.Searches, r.People, r.ZeroResults, r.Clicks))
            .ToList();
        if (small.Count > 0)
        {
            groups.Add(new SearchUnitGroup(ItemAnalytics.Other, null, small.Count > 1 ? small.Sum(r => r.Searches) : null, null, null, null));
        }
        return groups;
    }
}
