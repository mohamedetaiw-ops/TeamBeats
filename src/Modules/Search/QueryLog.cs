using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

public sealed record SearchClickRequest(Guid? QueryId, int? Position);

/// <summary>Whether the tenant keeps the words people search for in the query log.</summary>
public sealed record QueryTextSetting(bool Enabled, DateTimeOffset? UpdatedAt);

public sealed record QueryTextRequest(bool? Enabled);

/// <summary>
/// The pseudonymous search query log (spec 012 FR-013, US8 scenario 2) and the feed it gives the search dashboard of
/// spec 021. Each first page of results is recorded with the person's analytics key, never their user ID, and the
/// first click on a result is added to it. Queries are kept as their normalized words, unless search administrators
/// turn query text off: then no words are kept and those already kept are cleared. The words of a query that looks like
/// personal data (an email address, a phone or ID number) are never kept, and the feed shows a query only when at least
/// five different people searched for it in the period. Events are kept for 13 months, the analytics default for raw events.
/// </summary>
public sealed partial class SearchQueryLog(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAnalyticsPseudonyms pseudonyms, IOrganizationHierarchy hierarchy, IAuditWriter audit, IClock clock) : ISearchAnalyticsFeed
{
    public const int MinPeople = 5;
    public const int MaxRows = 20;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(396);
    public static readonly TimeSpan ClickWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefineWindow = TimeSpan.FromMinutes(5);

    /// <summary>Records a first page of results and returns its ID for the click, or null when the person is not a user.</summary>
    internal async Task<Guid?> RecordAsync(string query, int results, CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || context.ActorId is not { } user)
        {
            return null;
        }
        var id = Ids.New();
        // A query that looks like personal data is counted but its words are never kept.
        var key = !LooksPersonal(query) && await QueryTextAsync(ct) ? Truncate(SearchText.QueryKey(query)) : null;
        key = key is { Length: > 0 } ? key : null;
        var person = await pseudonyms.KeyAsync(user, ct);
        var memberships = await hierarchy.GetActiveMembershipsAsync(user, ct);
        var unit = (memberships.FirstOrDefault(m => m.IsPrimary) ?? memberships.FirstOrDefault())?.OrgUnitId;
        var now = clock.UtcNow;
        // A refinement: the person's last search, within five minutes, had other words and no click.
        await session.ExecAsync(
            """
            insert into search.query_event (tenant_id, id, at, person, locale, query_key, results, org_unit_id, refined_from)
            values (platform.current_tenant_id(), @id, @now, @person, @locale, @key::text, @results, @unit::uuid,
              (select p.id from (select id, query_key, clicked_at from search.query_event where person = @person and at > @since and at <= @now
                 order by at desc limit 1) p where p.clicked_at is null and p.query_key is distinct from @key::text))
            """, new { id, now, person, locale = SearchText.QueryLanguage(query), key, results, unit, since = now - RefineWindow }, ct);
        return id;
    }

    /// <summary>Adds the first click on a result to the person's own recent query. Later clicks and other people's queries change nothing.</summary>
    public async Task ClickAsync(SearchClickRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (request.QueryId is not { } id || request.Position is not (>= 1 and <= SearchService.PageSize * SearchService.MaxPages))
        {
            throw AppException.Validation("queryId", "validation.search.click");
        }
        await session.ExecAsync(
            "update search.query_event set clicked_at = @now, click_position = @position where id = @id and person = @person and clicked_at is null and at > @since",
            new { id, person = await pseudonyms.KeyAsync(user, ct), position = request.Position, now = clock.UtcNow, since = clock.UtcNow - ClickWindow }, ct);
    }

    public async Task<SearchQueryReport> ReportAsync(DateTimeOffset from, DateTimeOffset to, string? locale, CancellationToken ct = default,
        IReadOnlyCollection<Guid>? units = null)
    {
        var scoped = units is not null;
        var unitIds = units?.ToArray() ?? [];
        var args = new { from, to, locale, scoped, units = unitIds, min = MinPeople };
        const string Where = "at >= @from and at < @to and (@locale::text is null or locale = @locale) and (not @scoped or org_unit_id = any(@units))";
        var totals = await session.QueryAsync(
            $"""
            select count(*)::int, count(*) filter (where results = 0)::int, count(*) filter (where clicked_at is not null)::int,
              count(*) filter (where refined_from is not null)::int
            from search.query_event where {Where}
            """, args, r => (Searches: r.GetInt32(0), Zero: r.GetInt32(1), Clicked: r.GetInt32(2), Refined: r.GetInt32(3)), ct);
        var (searches, zero, clicked, refined) = totals[0];
        var byUnit = await session.QueryAsync(
            $"""
            select org_unit_id, count(*)::int, count(distinct person)::int, count(*) filter (where results = 0)::int, count(*) filter (where clicked_at is not null)::int
            from search.query_event where {Where} group by 1 order by 2 desc, 1
            """, args, r => new SearchUnitRow(r.IsDBNull(0) ? null : r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)), ct);
        var hidden = !await QueryTextAsync(ct);
        IReadOnlyList<SearchQueryRow> Rows(IEnumerable<SearchQueryRow> rows) => [.. rows.Where(r => !LooksPersonal(r.Query)).Take(MaxRows)];
        var rows = hidden ? [] : await session.QueryAsync(
            $"""
            select query_key, count(*)::int, count(distinct person)::int, count(*) filter (where results = 0)::int, count(*) filter (where clicked_at is not null)::int
            from search.query_event where {Where} and query_key is not null
            group by query_key having count(distinct person) >= @min
            order by count(*) desc, query_key limit 500
            """, args,
            r => new SearchQueryRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)), ct);
        var refinements = hidden ? [] : await session.QueryAsync(
            $"""
            select p.query_key, e.query_key, count(*)::int, count(distinct e.person)::int
            from (select * from search.query_event where {Where}) e join search.query_event p on p.id = e.refined_from
            where e.query_key is not null and p.query_key is not null
            group by 1, 2 having count(distinct e.person) >= @min
            order by 3 desc, 1, 2 limit 100
            """, args, r => new SearchRefinementRow(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)), ct);
        return new SearchQueryReport(searches, zero, clicked, searches == 0 ? 0 : Math.Round(clicked / (double)searches, 4), hidden,
            Rows(rows),
            Rows(rows.Where(r => r.ZeroResults > 0).OrderByDescending(r => r.ZeroResults).ThenBy(r => r.Query, StringComparer.Ordinal)),
            Rows(rows.Where(r => r.Clicks == 0 && r.ZeroResults < r.Searches).OrderByDescending(r => r.Searches).ThenBy(r => r.Query, StringComparer.Ordinal)),
            refined, byUnit,
            [.. refinements.Where(r => !LooksPersonal(r.From) && !LooksPersonal(r.To)).Take(MaxRows)]);
    }

    public async Task<QueryTextSetting> GetSettingAsync(CancellationToken ct)
    {
        await DemandManageAsync(ct);
        var rows = await session.QueryAsync("select query_text, updated_at from search.setting", null, r => new QueryTextSetting(r.GetBoolean(0), r.GetFieldValue<DateTimeOffset>(1)), ct);
        return rows.Count > 0 ? rows[0] : new QueryTextSetting(true, null);
    }

    public async Task<QueryTextSetting> SetSettingAsync(QueryTextRequest request, CancellationToken ct)
    {
        await DemandManageAsync(ct);
        if (request.Enabled is not { } enabled)
        {
            throw AppException.Validation("enabled", "validation.required");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into search.setting (tenant_id, ai_summary, query_text, updated_by, updated_at) values (platform.current_tenant_id(), false, @enabled, @by, @now)
            on conflict (tenant_id) do update set query_text = @enabled, updated_by = @by, updated_at = @now
            """, new { enabled, by = context.RequireUser(), now }, ct);
        var cleared = enabled ? 0 : await session.ExecAsync("update search.query_event set query_key = null where query_key is not null", null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchQueryTextChanged, "search_setting", "query_text", Changes: new { enabled, cleared }), ct);
        return new QueryTextSetting(enabled, now);
    }

    /// <summary>Removes query events older than the retention period. Returns how many went.</summary>
    internal Task<int> PruneAsync(CancellationToken ct) =>
        session.ExecAsync("delete from search.query_event where at < @cutoff", new { cutoff = clock.UtcNow - Retention }, ct);

    private async Task<bool> QueryTextAsync(CancellationToken ct) => await session.ScalarAsync<bool?>("select query_text from search.setting", null, ct) != false;

    private async Task DemandManageAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SearchCore, ct);
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
    }

    private static string Truncate(string key) => key.Length <= 200 ? key : key[..200];

    /// <summary>An email address, or a run of six or more digits (a phone, staff or ID number), is never shown.</summary>
    internal static bool LooksPersonal(string query) => Email().IsMatch(query) || Digits().IsMatch(query.Replace(" ", string.Empty, StringComparison.Ordinal));

    [GeneratedRegex(@"[^\s@]+@[^\s@]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"[0-9٠-٩]{6,}")]
    private static partial Regex Digits();
}
