using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

/// <summary>
/// The retrieval contract (spec 012 US3, FR-007). Passages, not items, are ranked: keyword and semantic passage lists
/// are fused by reciprocal rank, each owner keeps only what the person may read now, and the AI classification policy
/// of the use case keeps back passages above its ceiling, counted as withheld (US3 scenario 2). At most
/// <see cref="PerItem"/> passages come from one item, so one long document does not crowd out the rest.
/// </summary>
public sealed class SearchRetrieval(IDbSession session, ITenantContext context, IEntitlementService entitlements, SearchService search,
    IAiClassificationPolicy classification) : ISearchRetrieval
{
    public const int Candidates = 200;
    public const int PerItem = 2;

    private sealed record Passage(Guid Id, string Source, Guid ItemId, string Kind, string Title, string Body, string Link, string Locale, string Classification,
        int? Version, DateTimeOffset? UpdatedAt);

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length == 0 || query.Length > SearchText.MaxQueryLength)
        {
            errors.Add("query", "validation.search.query");
        }
        if (!AiUseCases.All.Contains(request.UseCase))
        {
            errors.Add("useCase", "validation.search.use_case");
        }
        if (request.TopK is < 1 or > ISearchRetrieval.MaxTopK)
        {
            errors.Add("topK", "validation.search.top_k");
        }
        if (request.Kinds is { } kinds && kinds.Any(k => !SearchKinds.All.Contains(k)))
        {
            errors.Add("kinds", "validation.search.kind");
        }
        errors.ThrowIfAny();
        await entitlements.DemandAsync(Capabilities.SearchCore, ct);
        context.RequireUser();
        var topK = request.TopK ?? ISearchRetrieval.DefaultTopK;
        var ceiling = await classification.CeilingAsync(request.UseCase, ct);

        var terms = SearchText.Terms(query);
        var groups = await search.GroupsAsync(terms, SearchText.QueryLanguage(query), ct);
        var keyword = terms.Count == 0 ? [] : await KeywordAsync(SearchText.TsQuery(groups, all: false), request.Kinds, ct);
        var (semantic, degraded) = await SemanticAsync(query, request.Kinds, ct);

        var byId = keyword.Concat(semantic).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var scores = SearchText.Fuse([[.. keyword.Select(p => p.Id)], [.. semantic.Select(p => p.Id)]]);
        var ranked = scores.OrderByDescending(s => s.Value).ThenBy(s => s.Key).Select(s => (Passage: byId[s.Key], Score: s.Value)).ToList();
        var readable = await search.ReadableAsync(ranked.Select(r => (r.Passage.Source, r.Passage.ItemId)), ct);

        var passages = new List<RetrievedPassage>();
        var perItem = new Dictionary<(string, Guid), int>();
        var withheld = 0;
        foreach (var (p, score) in ranked)
        {
            if (passages.Count == topK)
            {
                break;
            }
            if (!readable.Contains((p.Source, p.ItemId)))
            {
                continue;
            }
            var key = (p.Source, p.ItemId);
            if (perItem.GetValueOrDefault(key) >= PerItem)
            {
                continue;
            }
            if (ceiling is not { } max || Level(p.Classification) > max)
            {
                withheld++;
                continue;
            }
            perItem[key] = perItem.GetValueOrDefault(key) + 1;
            passages.Add(new RetrievedPassage(p.Source, p.ItemId, p.Kind, p.Title, p.Body, p.Link, p.Locale, p.Classification, p.Version, p.UpdatedAt, score));
        }
        return new RetrievalResult(passages, withheld, degraded);
    }

    private static DataClassification Level(string value) => value switch
    {
        "public" => DataClassification.Public,
        "internal" => DataClassification.Internal,
        "confidential" => DataClassification.Confidential,
        _ => DataClassification.Restricted,
    };

    private const string Columns = "id, source, item_id, kind, title, body, link, locale, classification, version, updated_at";

    private static Passage Map(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetString(6), r.GetString(7), r.GetString(8), r.Get<int?>("version"), r.Get<DateTimeOffset?>("updated_at"));

    private Task<List<Passage>> KeywordAsync(string tsquery, IReadOnlyList<string>? kinds, CancellationToken ct) =>
        session.QueryAsync(
            $"""
            with q as (select to_tsquery('simple', @tsquery) as s, to_tsquery('english', @tsquery) as e)
            select {Columns} from search.unit u, q
            where (u.tsv @@ q.s or u.tsv @@ q.e) and (@kinds::text[] is null or u.kind = any(@kinds))
            order by greatest(ts_rank_cd(u.tsv, q.s), ts_rank_cd(u.tsv, q.e)) desc, u.id
            limit @limit
            """, new { tsquery, kinds = kinds?.ToArray(), limit = Candidates }, Map, ct);

    private async Task<(List<Passage> Passages, bool Degraded)> SemanticAsync(string query, IReadOnlyList<string>? kinds, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SearchSemantic, ct))
        {
            return ([], false);
        }
        var (embedded, degraded) = await search.EmbedQueryAsync(query, ct);
        if (embedded is null)
        {
            return ([], degraded);
        }
        var distance = SearchService.Distance(embedded.Value.Vector);
        var rows = await session.QueryAsync(
            $"""
            select {Columns} from search.unit
            where embedding_model = @model and embedding_dims = @dims and {distance} <= @maxDistance and (@kinds::text[] is null or kind = any(@kinds))
            order by {distance}
            limit @limit
            """, new { model = embedded.Value.Model, dims = embedded.Value.Vector.Length, vector = embedded.Value.Vector, maxDistance = SearchService.MaxSemanticDistance,
                kinds = kinds?.ToArray(), limit = Candidates }, Map, ct);
        return (rows, false);
    }
}
