using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

public sealed record BestBetRequest(string? Query, string? Locale, string? Source, Guid? ItemId);

/// <summary>A best bet as administrators see it. <see cref="Title"/> is null when the item is no longer in the index.</summary>
public sealed record BestBetView(Guid Id, string Query, string? Locale, string Source, Guid ItemId, string? Kind, string? Title, DateTimeOffset CreatedAt);

public sealed record SynonymSetRequest(string? Locale, List<string>? Terms);

public sealed record SynonymSetView(Guid Id, string? Locale, IReadOnlyList<string> Terms, DateTimeOffset UpdatedAt);

/// <summary>A best bet that applies to a query: the target and its kind, before the owner's permission check.</summary>
internal sealed record BestBetTarget(string Source, Guid ItemId, string Kind, DateTimeOffset? UpdatedAt);

/// <summary>
/// Best bets and synonyms (spec 012 US4, FR-010), managed with <c>search.manage</c> and audited (section 13). Queries
/// and words are normalized as the search box normalizes them, so a rule written "إجازة" also applies to "اجازه".
/// </summary>
public sealed class SearchTuning(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock)
{
    public const int MaxBestBets = 500;
    public const int MaxSynonymSets = 500;
    public const int MaxTermLength = 60;
    public static readonly IReadOnlyList<string> Locales = ["ar", "en"];

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SearchCore, ct);
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
    }

    public async Task<IReadOnlyList<BestBetView>> ListBestBetsAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        return await session.QueryAsync(
            """
            select b.id, b.query, b.locale, b.source, b.item_id, u.kind, u.title, b.created_at
            from search.best_bet b
            left join lateral (select kind, title from search.unit u where u.source = b.source and u.item_id = b.item_id order by position, unit_key limit 1) u on true
            order by b.query, b.created_at
            """, null,
            r => new BestBetView(r.GetGuid(0), r.GetString(1), r.Get<string?>("locale"), r.GetString(3), r.GetGuid(4), r.Get<string?>("kind"), r.Get<string?>("title"),
                r.GetFieldValue<DateTimeOffset>(7)), ct);
    }

    public async Task<BestBetView> AddBestBetAsync(BestBetRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var by = context.RequireUser();
        var errors = new ValidationErrors();
        var query = SearchText.QueryKey(request.Query);
        if (query.Length == 0 || (request.Query?.Length ?? 0) > SearchText.MaxQueryLength)
        {
            errors.Add("query", "validation.search.best_bet.query");
        }
        if (request.Locale is not null && !Locales.Contains(request.Locale))
        {
            errors.Add("locale", "validation.search.locale");
        }
        if (string.IsNullOrWhiteSpace(request.Source) || request.ItemId is null)
        {
            errors.Add("target", "validation.search.best_bet.target");
        }
        errors.ThrowIfAny();

        // The target must be an item search knows; whether a searcher may read it is decided at query time.
        var target = await session.QueryAsync(
            "select kind, title from search.unit where source = @source and item_id = @item order by position, unit_key limit 1",
            new { source = request.Source, item = request.ItemId }, r => (Kind: r.GetString(0), Title: r.GetString(1)), ct);
        if (target.Count == 0)
        {
            throw AppException.Validation("target", "validation.search.best_bet.target");
        }
        if (await session.ScalarAsync<long>("select count(*) from search.best_bet", null, ct) >= MaxBestBets)
        {
            throw AppException.Validation("query", "validation.search.best_bet.limit");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        var inserted = await session.ExecAsync(
            """
            insert into search.best_bet (tenant_id, id, query, locale, source, item_id, created_at, created_by)
            values (platform.current_tenant_id(), @id, @query, @locale, @source, @item, @now, @by)
            on conflict do nothing
            """, new { id, query, locale = request.Locale, source = request.Source, item = request.ItemId, now, by }, ct);
        if (inserted == 0)
        {
            throw AppException.Conflict("validation.search.best_bet.duplicate");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchBestBetChanged, "search_best_bet", id.ToString(),
            Changes: new { change = "added", query, locale = request.Locale, source = request.Source, item = request.ItemId }), ct);
        return new BestBetView(id, query, request.Locale, request.Source!, request.ItemId!.Value, target[0].Kind, target[0].Title, now);
    }

    public async Task RemoveBestBetAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var removed = await session.QueryAsync("delete from search.best_bet where id = @id returning query, locale, source, item_id", new { id },
            r => new { query = r.GetString(0), locale = r.Get<string?>("locale"), source = r.GetString(2), item = r.GetGuid(3) }, ct);
        if (removed.Count == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchBestBetChanged, "search_best_bet", id.ToString(),
            Changes: new { change = "removed", removed[0].query, removed[0].locale, removed[0].source, removed[0].item }), ct);
    }

    public async Task<IReadOnlyList<SynonymSetView>> ListSynonymsAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        return await session.QueryAsync("select id, locale, terms, updated_at from search.synonym_set order by locale nulls first, terms[1]", null,
            r => new SynonymSetView(r.GetGuid(0), r.Get<string?>("locale"), r.GetFieldValue<string[]>(2), r.GetFieldValue<DateTimeOffset>(3)), ct);
    }

    private static (string? Locale, string[] Terms) ValidateSet(SynonymSetRequest request)
    {
        var errors = new ValidationErrors();
        if (request.Locale is not null && !Locales.Contains(request.Locale))
        {
            errors.Add("locale", "validation.search.locale");
        }
        // Each entry is one word, normalized; phrases would need phrase search, which the keyword query does not do.
        var raw = request.Terms ?? [];
        var terms = raw.Select(t => SearchText.Terms(t)).ToList();
        if (raw.Any(t => t is null || t.Length > MaxTermLength) || terms.Any(t => t.Count != 1))
        {
            errors.Add("terms", "validation.search.synonyms.word");
        }
        var distinct = terms.Where(t => t.Count == 1).Select(t => t[0]).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length is < 2 or > 20)
        {
            errors.Add("terms", "validation.search.synonyms.count");
        }
        errors.ThrowIfAny();
        return (request.Locale, distinct);
    }

    public async Task<SynonymSetView> AddSynonymsAsync(SynonymSetRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var by = context.RequireUser();
        var (locale, terms) = ValidateSet(request);
        if (await session.ScalarAsync<long>("select count(*) from search.synonym_set", null, ct) >= MaxSynonymSets)
        {
            throw AppException.Validation("terms", "validation.search.synonyms.limit");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            "insert into search.synonym_set (tenant_id, id, locale, terms, updated_at, updated_by) values (platform.current_tenant_id(), @id, @locale, @terms, @now, @by)",
            new { id, locale, terms, now, by }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchSynonymsChanged, "search_synonyms", id.ToString(), Changes: new { change = "added", locale, terms }), ct);
        return new SynonymSetView(id, locale, terms, now);
    }

    public async Task<SynonymSetView> UpdateSynonymsAsync(Guid id, SynonymSetRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var by = context.RequireUser();
        var (locale, terms) = ValidateSet(request);
        var now = clock.UtcNow;
        var changed = await session.ExecAsync("update search.synonym_set set locale = @locale, terms = @terms, updated_at = @now, updated_by = @by where id = @id",
            new { id, locale, terms, now, by }, ct);
        if (changed == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchSynonymsChanged, "search_synonyms", id.ToString(), Changes: new { change = "changed", locale, terms }), ct);
        return new SynonymSetView(id, locale, terms, now);
    }

    public async Task RemoveSynonymsAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var removed = await session.QueryAsync("delete from search.synonym_set where id = @id returning locale", new { id }, r => r.Get<string?>("locale"), ct);
        if (removed.Count == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SearchSynonymsChanged, "search_synonyms", id.ToString(), Changes: new { change = "removed", locale = removed[0] }), ct);
    }

    /// <summary>The synonym sets that apply to a query in <paramref name="language"/>: those for that language and those for every language.</summary>
    internal async Task<IReadOnlyList<IReadOnlyCollection<string>>> SynonymsForAsync(IReadOnlyList<string> terms, string language, CancellationToken ct)
    {
        if (terms.Count == 0)
        {
            return [];
        }
        var sets = await session.QueryAsync(
            "select terms from search.synonym_set where (locale is null or locale = @language) and terms && @terms",
            new { language, terms = terms.ToArray() }, r => r.GetFieldValue<string[]>(0), ct);
        return [.. sets.Select(s => (IReadOnlyCollection<string>)s)];
    }

    /// <summary>The best bets for a query in <paramref name="language"/>, oldest first, whose item is still in the index.</summary>
    internal async Task<IReadOnlyList<BestBetTarget>> BestBetsForAsync(string queryKey, string language, CancellationToken ct)
    {
        if (queryKey.Length == 0)
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select b.source, b.item_id, u.kind, u.updated_at
            from search.best_bet b
            join lateral (select kind, max(updated_at) over () as updated_at from search.unit u where u.source = b.source and u.item_id = b.item_id limit 1) u on true
            where b.query = @queryKey and (b.locale is null or b.locale = @language)
            order by b.created_at, b.id
            """, new { queryKey, language }, r => new BestBetTarget(r.GetString(0), r.GetGuid(1), r.GetString(2), r.Get<DateTimeOffset?>("updated_at")), ct);
    }
}
