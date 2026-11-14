using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Search.Contracts;
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
    IAnalyticsPseudonyms pseudonyms, IAuditWriter audit, IClock clock) : ISearchAnalyticsFeed
{
    public const int MinPeople = 5;
    public const int MaxRows = 20;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(396);
    public static readonly TimeSpan ClickWindow = TimeSpan.FromHours(1);

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
        await session.ExecAsync(
            """
            insert into search.query_event (tenant_id, id, at, person, locale, query_key, results)
            values (platform.current_tenant_id(), @id, @now, @person, @locale, @key, @results)
            """, new { id, now = clock.UtcNow, person = await pseudonyms.KeyAsync(user, ct), locale = SearchText.QueryLanguage(query), key = key is { Length: > 0 } ? key : null, results },
            ct);
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

    public async Task<SearchQueryReport> ReportAsync(DateTimeOffset from, DateTimeOffset to, string? locale, CancellationToken ct = default)
    {
        var args = new { from, to, locale };
        const string Where = "at >= @from and at < @to and (@locale::text is null or locale = @locale)";
        var totals = await session.QueryAsync(
            $"select count(*)::int, count(*) filter (where results = 0)::int, count(*) filter (where clicked_at is not null)::int from search.query_event where {Where}",
            args, r => (Searches: r.GetInt32(0), Zero: r.GetInt32(1), Clicked: r.GetInt32(2)), ct);
        var (searches, zero, clicked) = totals[0];
        var hidden = !await QueryTextAsync(ct);
        IReadOnlyList<SearchQueryRow> Rows(IEnumerable<SearchQueryRow> rows) => [.. rows.Where(r => !LooksPersonal(r.Query)).Take(MaxRows)];
        var rows = hidden ? [] : await session.QueryAsync(
            $"""
            select query_key, count(*)::int, count(distinct person)::int, count(*) filter (where results = 0)::int, count(*) filter (where clicked_at is not null)::int
            from search.query_event where {Where} and query_key is not null
            group by query_key having count(distinct person) >= @min
            order by count(*) desc, query_key limit 500
            """, new { from, to, locale, min = MinPeople },
            r => new SearchQueryRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)), ct);
        return new SearchQueryReport(searches, zero, clicked, searches == 0 ? 0 : Math.Round(clicked / (double)searches, 4), hidden,
            Rows(rows),
            Rows(rows.Where(r => r.ZeroResults > 0).OrderByDescending(r => r.ZeroResults).ThenBy(r => r.Query, StringComparer.Ordinal)),
            Rows(rows.Where(r => r.Clicks == 0 && r.ZeroResults < r.Searches).OrderByDescending(r => r.Searches).ThenBy(r => r.Query, StringComparer.Ordinal)));
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
