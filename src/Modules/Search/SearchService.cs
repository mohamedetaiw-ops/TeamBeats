using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

public sealed record SearchResult(Guid ItemId, string Source, string Kind, string Title, string Snippet, string Link, string Locale, DateTimeOffset? UpdatedAt,
    bool Recommended = false);

/// <summary>
/// One page of results (spec 012 US1). <see cref="Total"/> and <see cref="Facets"/> count only items the person may read;
/// they stop at <see cref="SearchService.MaxCandidates"/> and <see cref="More"/> says there were more. <see cref="Degraded"/>
/// is true when semantic retrieval should have run and could not (FR-014). <see cref="Alternatives"/> holds services and
/// people that match any word when nothing matched all of them (US1 scenario 5).
/// </summary>
public sealed record SearchPage(string Query, int Total, bool More, int Page, int PageSize, IReadOnlyList<SearchResult> Results,
    IReadOnlyDictionary<string, int> Facets, bool Degraded, IReadOnlyList<SearchResult> Alternatives);

public sealed record Suggestion(Guid ItemId, string Source, string Kind, string Title, string Link);

/// <summary>A candidate item before permission filtering, with its fused score.</summary>
internal sealed record Candidate(string Source, Guid ItemId, string Kind, DateTimeOffset? UpdatedAt, double Score);

/// <summary>
/// Search and type-ahead (spec 012 US1, FR-001, FR-002, FR-005, FR-009, FR-014). Keyword candidates from PostgreSQL full
/// text and semantic candidates from pgvector are fused by reciprocal rank, then each owner decides which candidates the
/// person may read now; totals, facets and suggestions are counted after that check, so a hidden item never shows in
/// any number (two-stage permission filtering of the plan).
/// </summary>
public sealed class SearchService(IDbSession session, ITenantContext context, IEntitlementService entitlements, IEnumerable<ISearchSource> sources,
    IAiGateway gateway, SearchTuning tuning, ILogger<SearchService> logger)
{
    public const int PageSize = 20;
    public const int MaxPages = 50;
    public const int MaxCandidates = 1000;
    public const int SemanticCandidates = 300;
    public const int MaxSuggestions = 8;

    /// <summary>
    /// Passages farther than this cosine distance from the query are not semantic matches, so a query about nothing in the
    /// index still finds nothing (US1 scenario 5). The default until the retrieval configuration of the plan holds it.
    /// </summary>
    public const double MaxSemanticDistance = 0.7;

    private readonly Dictionary<string, ISearchSource> bySource = sources.ToDictionary(s => s.Source, StringComparer.Ordinal);

    private static string Validate(string? q, string? kind, int? page)
    {
        var errors = new ValidationErrors();
        var query = (q ?? string.Empty).Trim();
        if (query.Length == 0 || query.Length > SearchText.MaxQueryLength)
        {
            errors.Add("q", "validation.search.query");
        }
        if (kind is not null && !SearchKinds.All.Contains(kind))
        {
            errors.Add("kind", "validation.search.kind");
        }
        if (page is < 1 or > MaxPages)
        {
            errors.Add("page", "validation.search.page");
        }
        errors.ThrowIfAny();
        return query;
    }

    public async Task<SearchPage> SearchAsync(string? q, string? kind, DateTimeOffset? from, DateTimeOffset? to, int? page, CancellationToken ct)
    {
        var query = Validate(q, kind, page);
        await entitlements.DemandAsync(Capabilities.SearchCore, ct);
        context.RequireUser();
        var terms = SearchText.Terms(query);
        var language = SearchText.QueryLanguage(query);
        var groups = await GroupsAsync(terms, language, ct);
        var number = page ?? 1;

        var keyword = terms.Count == 0 ? [] : await KeywordAsync(SearchText.TsQuery(groups, all: true), from, to, MaxCandidates, ct);
        var (semantic, degraded) = await SemanticAsync(query, from, to, ct);
        var candidates = Fuse(keyword, semantic);
        var more = keyword.Count >= MaxCandidates;

        // Best bets go first, marked as recommended, when the person may read them (US4 scenario 1). They are counted
        // like any other result, once.
        var bets = (await tuning.BestBetsForAsync(SearchText.QueryKey(query), language, ct))
            .Select(b => new Candidate(b.Source, b.ItemId, b.Kind, b.UpdatedAt, double.MaxValue)).ToList();
        var recommended = bets.Select(b => (b.Source, b.ItemId)).ToHashSet();
        candidates = [.. bets, .. candidates.Where(c => !recommended.Contains((c.Source, c.ItemId)))];
        var permitted = await PermittedAsync(candidates, ct);

        var facets = permitted.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var filtered = kind is null ? permitted : [.. permitted.Where(c => c.Kind == kind)];
        var pageItems = filtered.Skip((number - 1) * PageSize).Take(PageSize).ToList();
        var results = (await ResultsAsync(pageItems, terms, ct))
            .Select(r => recommended.Contains((r.Source, r.ItemId)) ? r with { Recommended = true } : r).ToList();

        IReadOnlyList<SearchResult> alternatives = [];
        if (filtered.Count == 0 && terms.Count > 0)
        {
            var any = await KeywordAsync(SearchText.TsQuery(groups, all: false), null, null, 100, ct);
            var offered = (await PermittedAsync([.. any.Where(c => c.Kind is SearchKinds.Service or SearchKinds.Person)], ct)).Take(5).ToList();
            alternatives = await ResultsAsync(offered, terms, ct);
        }
        return new SearchPage(query, filtered.Count, more, number, PageSize, results, facets, degraded, alternatives);
    }

    /// <summary>The query terms, each with its synonyms (US4 scenario 2).</summary>
    internal async Task<IReadOnlyList<IReadOnlyList<string>>> GroupsAsync(IReadOnlyList<string> terms, string language, CancellationToken ct) =>
        SearchText.Expand(terms, await tuning.SynonymsForAsync(terms, language, ct));

    /// <summary>Type-ahead on titles (FR-009): words typed so far, the last one as a prefix, readable items only.</summary>
    public async Task<IReadOnlyList<Suggestion>> SuggestAsync(string? q, CancellationToken ct)
    {
        var query = Validate(q, null, null);
        await entitlements.DemandAsync(Capabilities.SearchCore, ct);
        context.RequireUser();
        var terms = SearchText.Terms(query);
        if (terms.Count == 0 || terms.Sum(t => t.Length) < 2)
        {
            return [];
        }
        var tsquery = string.Join(" & ", terms.Select((t, i) => "'" + t.Replace("'", "''", StringComparison.Ordinal) + "':" + (i == terms.Count - 1 ? "*" : string.Empty) + "A"));
        var rows = await session.QueryAsync(
            """
            select source, item_id, kind, title, link, max(ts_rank_cd(tsv, to_tsquery('simple', @tsquery))) as rank
            from search.unit where tsv @@ to_tsquery('simple', @tsquery)
            group by source, item_id, kind, title, link
            order by rank desc, title limit 40
            """, new { tsquery }, r => (Source: r.GetString(0), Item: r.GetGuid(1), Kind: r.GetString(2), Title: r.GetString(3), Link: r.GetString(4)), ct);
        var readable = await ReadableAsync(rows.Select(r => (r.Source, r.Item)), ct);
        return [.. rows.Where(r => readable.Contains((r.Source, r.Item))).DistinctBy(r => (r.Source, r.Item)).Take(MaxSuggestions)
            .Select(r => new Suggestion(r.Item, r.Source, r.Kind, r.Title, r.Link))];
    }

    internal async Task<List<Candidate>> KeywordAsync(string tsquery, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct) =>
        await session.QueryAsync(
            """
            with q as (select to_tsquery('simple', @tsquery) as s, to_tsquery('english', @tsquery) as e)
            select u.source, u.item_id, min(u.kind) as kind, max(u.updated_at) as updated_at, max(greatest(ts_rank_cd(u.tsv, q.s), ts_rank_cd(u.tsv, q.e))) as rank
            from search.unit u, q
            where (u.tsv @@ q.s or u.tsv @@ q.e)
              and (@from::timestamptz is null or u.updated_at >= @from) and (@to::timestamptz is null or u.updated_at <= @to)
            group by u.source, u.item_id
            order by rank desc, u.item_id
            limit @limit
            """, new { tsquery, from, to, limit }, r => new Candidate(r.GetString(0), r.GetGuid(1), r.GetString(2), r.Get<DateTimeOffset?>("updated_at"), r.GetFloat(4)), ct);

    /// <summary>Nearest passages to the query's embedding, by item. Off without <c>search.semantic</c>; degraded when the gateway fails.</summary>
    internal async Task<(List<Candidate> Items, bool Degraded)> SemanticAsync(string query, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SearchSemantic, ct))
        {
            return ([], false);
        }
        var (embedded, degraded) = await EmbedQueryAsync(query, ct);
        if (embedded is null)
        {
            return ([], degraded);
        }
        var distance = Distance(embedded.Value.Vector);
        var rows = await session.QueryAsync(
            $"""
            select source, item_id, kind, updated_at from search.unit
            where embedding_model = @model and embedding_dims = @dims and {distance} <= @maxDistance
              and (@from::timestamptz is null or updated_at >= @from) and (@to::timestamptz is null or updated_at <= @to)
            order by {distance}
            limit @limit
            """, new { model = embedded.Value.Model, dims = embedded.Value.Vector.Length, vector = embedded.Value.Vector, from, to, limit = SemanticCandidates, maxDistance = MaxSemanticDistance },
            r => new Candidate(r.GetString(0), r.GetGuid(1), r.GetString(2), r.Get<DateTimeOffset?>("updated_at"), 0), ct);
        return ([.. rows.DistinctBy(c => (c.Source, c.ItemId))], false);
    }

    /// <summary>
    /// The query's embedding through the gateway, or null with whether that is an outage (FR-014): a package or policy
    /// that turns AI off is a setting, not an outage.
    /// </summary>
    internal async Task<((float[] Vector, string Model)? Embedded, bool Degraded)> EmbedQueryAsync(string query, CancellationToken ct)
    {
        EmbeddingResult embedded;
        try
        {
            // Query text sent for embedding is classified Internal (section 11).
            embedded = await gateway.EmbedAsync(new EmbeddingRequest([query], DataClassification.Internal), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Query embedding failed; search is keyword only");
            return (null, true);
        }
        if (!embedded.IsOk)
        {
            var setting = embedded.Denial is AiDenials.NotInPackage or AiDenials.ModelNotAllowed or AiDenials.ClassificationBlocked;
            return (null, !setting);
        }
        return ((embedded.Vectors![0], embedded.Model!), false);
    }

    /// <summary>The cosine distance expression to <c>@vector</c>; the production size uses its approximate index, other sizes are scanned exactly.</summary>
    internal static string Distance(float[] vector) => vector.Length == 1536
        ? "(embedding::extensions.vector(1536)) operator(extensions.<=>) @vector::real[]::extensions.vector(1536)"
        : "embedding operator(extensions.<=>) @vector::real[]::extensions.vector";

    internal static List<Candidate> Fuse(IReadOnlyList<Candidate> keyword, IReadOnlyList<Candidate> semantic)
    {
        var byKey = keyword.Concat(semantic).GroupBy(c => (c.Source, c.ItemId)).ToDictionary(g => g.Key, g => g.First());
        var scores = SearchText.Fuse([[.. keyword.Select(c => (c.Source, c.ItemId))], [.. semantic.Select(c => (c.Source, c.ItemId))]]);
        return [.. scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key.ItemId).Select(s => byKey[s.Key] with { Score = s.Value })];
    }

    /// <summary>The candidates each owner says the person may read now, in order.</summary>
    internal async Task<List<Candidate>> PermittedAsync(IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        var readable = await ReadableAsync(candidates.Select(c => (c.Source, c.ItemId)), ct);
        return [.. candidates.Where(c => readable.Contains((c.Source, c.ItemId)))];
    }

    internal async Task<HashSet<(string Source, Guid ItemId)>> ReadableAsync(IEnumerable<(string Source, Guid ItemId)> items, CancellationToken ct)
    {
        var allowed = new HashSet<(string, Guid)>();
        foreach (var group in items.Distinct().GroupBy(i => i.Source, StringComparer.Ordinal))
        {
            if (!bySource.TryGetValue(group.Key, out var owner))
            {
                continue;
            }
            var ids = group.Select(g => g.ItemId).ToList();
            foreach (var id in await owner.ReadableAsync(ids, ct))
            {
                allowed.Add((group.Key, id));
            }
        }
        return allowed;
    }

    /// <summary>The best unit of each item for display: the passage that matches the most terms, else the first one.</summary>
    internal async Task<IReadOnlyList<SearchResult>> ResultsAsync(IReadOnlyList<Candidate> items, IReadOnlyList<string> terms, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }
        var tsquery = terms.Count == 0 ? string.Empty : SearchText.TsQuery([.. terms.Select(t => (IReadOnlyList<string>)[t])], all: false);
        var rows = await session.QueryAsync(
            """
            select distinct on (source, item_id) source, item_id, kind, title, body, link, locale, updated_at
            from search.unit
            where item_id = any(@ids)
            order by source, item_id,
              case when @tsquery = '' then 0 else greatest(ts_rank_cd(tsv, to_tsquery('simple', @tsquery)), ts_rank_cd(tsv, to_tsquery('english', @tsquery))) end desc,
              position, unit_key
            """, new { ids = items.Select(i => i.ItemId).Distinct().ToArray(), tsquery },
            r => new SearchResult(r.GetGuid(1), r.GetString(0), r.GetString(2), r.GetString(3), SearchText.Snippet(r.GetString(4), terms), r.GetString(5),
                r.GetString(6), r.Get<DateTimeOffset?>("updated_at")), ct);
        var byKey = rows.ToDictionary(r => (r.Source, r.ItemId));
        return [.. items.Where(i => byKey.ContainsKey((i.Source, i.ItemId))).Select(i => byKey[(i.Source, i.ItemId)])];
    }
}
