using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
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
    bool CanWriteArticle);

/// <summary>
/// Search gaps for tenant wide analytics viewers, search administrators and knowledge editors (section 3), under
/// <c>analytics.advanced</c>. Query text is tenant wide, so a scoped analytics viewer is refused.
/// </summary>
public sealed class SearchGaps(IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, ISearchAnalyticsFeed feed, IClock clock)
{
    public const int DefaultDays = 30;

    public async Task<SearchGapsView> GetAsync(DateOnly? from, DateOnly? to, string? locale, CancellationToken ct)
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
        var report = await feed.ReportAsync(new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), locale, ct);
        return new SearchGapsView(start, end, locale, 5, report.Searches, report.ZeroResults, report.Clicked, report.ClickThrough, report.QueryTextHidden,
            report.ZeroResultQueries, report.NoClickQueries, report.TopQueries, bestBets, articles);
    }
}
