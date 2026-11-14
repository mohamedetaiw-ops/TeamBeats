using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using C = TeamBeats.Modules.Content.Contracts;
using D = TeamBeats.Modules.Documents.Contracts;
using P = TeamBeats.Modules.Policies.Contracts;
using S = TeamBeats.Modules.Services.Contracts;
using Pe = TeamBeats.Modules.People.Contracts;
using T = TeamBeats.Modules.Tenancy.Contracts;
using I = TeamBeats.Modules.Identity.Contracts;

namespace TeamBeats.Modules.Search;

/// <summary>Source names of the R1 owners (spec 012 section 2), as their <see cref="ISearchSource"/> implementations report them.</summary>
public static class SearchSources
{
    public const string People = "people";
    public const string Content = "content";
    public const string Documents = "documents";
    public const string Policies = "policies";
    public const string Services = "services";
}

/// <summary>
/// Keeps the index in step with the owners (spec 012 US2, FR-006): each owner event of section 12 reads the item again
/// through its <see cref="ISearchSource"/> and replaces its rows, so an unpublished, deleted or superseded item leaves
/// the index in the same step. Passages are embedded through the AI gateway with the unit's classification; Restricted
/// units and tenants without <c>search.semantic</c> are keyword only, and a failed embedding leaves the passage keyword only.
/// </summary>
public sealed class SearchIndexer(IDbSession session, IEnumerable<ISearchSource> sources, IAiGateway gateway, IEntitlementService entitlements, RetrievalStore store,
    IClock clock, ILogger<SearchIndexer> logger)
    : IMessageHandler<SearchItemChanged>,
      IMessageHandler<C.ContentPublished>, IMessageHandler<C.ContentUnpublished>, IMessageHandler<C.ContentExpired>,
      IMessageHandler<D.DocumentVersionPublished>, IMessageHandler<D.DocumentDeleted>, IMessageHandler<D.DocumentWithdrawn>, IMessageHandler<D.DocumentDisposed>,
      IMessageHandler<P.PolicyPublished>, IMessageHandler<P.PolicySuperseded>,
      IMessageHandler<S.ServicePublished>, IMessageHandler<S.ServiceRetired>,
      IMessageHandler<Pe.ProfileUpdated>, IMessageHandler<T.TenantMembershipDisabled>, IMessageHandler<T.OrganizationMembershipChanged>,
      IMessageHandler<I.UserProvisioned>, IMessageHandler<I.UserDeprovisioned>
{
    /// <summary>How soon a change or deletion should be in the index (FR-006, section 15 objective).</summary>
    public static readonly TimeSpan LagObjective = TimeSpan.FromMinutes(5);

    /// <summary>Passages embedded per item; the rest of a very large item is found by keyword only (edge cases).</summary>
    public const int MaxEmbeddedPassages = 64;

    private const int EmbedBatch = 16;

    private readonly Dictionary<string, ISearchSource> bySource = sources.ToDictionary(s => s.Source, StringComparer.Ordinal);

    public Task HandleAsync(SearchItemChanged message, CancellationToken ct) => IndexAsync(message.Source, message.ItemId, ct);

    public Task HandleAsync(C.ContentPublished message, CancellationToken ct) => IndexAsync(SearchSources.Content, message.ItemId, ct);

    public Task HandleAsync(C.ContentUnpublished message, CancellationToken ct) => IndexAsync(SearchSources.Content, message.ItemId, ct);

    public Task HandleAsync(C.ContentExpired message, CancellationToken ct) => IndexAsync(SearchSources.Content, message.ItemId, ct);

    public Task HandleAsync(D.DocumentVersionPublished message, CancellationToken ct) => IndexAsync(SearchSources.Documents, message.DocumentId, ct);

    public Task HandleAsync(D.DocumentDeleted message, CancellationToken ct) => IndexAsync(SearchSources.Documents, message.DocumentId, ct);

    public Task HandleAsync(D.DocumentWithdrawn message, CancellationToken ct) => IndexAsync(SearchSources.Documents, message.DocumentId, ct);

    public Task HandleAsync(D.DocumentDisposed message, CancellationToken ct) => IndexAsync(SearchSources.Documents, message.DocumentId, ct);

    public Task HandleAsync(P.PolicyPublished message, CancellationToken ct) => IndexAsync(SearchSources.Policies, message.PolicyId, ct);

    public Task HandleAsync(P.PolicySuperseded message, CancellationToken ct) => IndexAsync(SearchSources.Policies, message.PolicyId, ct);

    public Task HandleAsync(S.ServicePublished message, CancellationToken ct) => IndexAsync(SearchSources.Services, message.ServiceId, ct);

    public Task HandleAsync(S.ServiceRetired message, CancellationToken ct) => IndexAsync(SearchSources.Services, message.ServiceId, ct);

    public Task HandleAsync(Pe.ProfileUpdated message, CancellationToken ct) => IndexAsync(SearchSources.People, message.UserId, ct);

    public Task HandleAsync(T.TenantMembershipDisabled message, CancellationToken ct) => IndexAsync(SearchSources.People, message.UserId, ct);

    public Task HandleAsync(T.OrganizationMembershipChanged message, CancellationToken ct) => IndexAsync(SearchSources.People, message.UserId, ct);

    public Task HandleAsync(I.UserProvisioned message, CancellationToken ct) => IndexAsync(SearchSources.People, message.UserId, ct);

    public Task HandleAsync(I.UserDeprovisioned message, CancellationToken ct) => IndexAsync(SearchSources.People, message.UserId, ct);

    /// <summary>The live index and the shadow index of an open retrieval change (spec 012 US3 scenario 3).</summary>
    internal const string Live = "search.unit";
    internal const string Shadow = "search.shadow_unit";

    /// <summary>
    /// Reads one item from its owner and replaces its rows with the configuration in force, and in the shadow index too
    /// while a retrieval change is open, so the shadow index never falls behind. Returns the number of live passages stored.
    /// </summary>
    public async Task<int> IndexAsync(string source, Guid itemId, CancellationToken ct)
    {
        if (!bySource.TryGetValue(source, out var owner))
        {
            logger.LogWarning("Search has no source named {Source}; item {ItemId} was not indexed", source, itemId);
            return 0;
        }
        var units = await owner.UnitsAsync(itemId, ct);
        var now = clock.UtcNow;
        var count = await WriteAsync(Live, source, itemId, units, await store.ActiveAsync(ct), now, ct);
        await session.ExecAsync(
            """
            insert into search.item_state (tenant_id, source, item_id, changed_at, synced_at, units)
            values (platform.current_tenant_id(), @source, @itemId, @now, @now, @count)
            on conflict (tenant_id, source, item_id) do update set synced_at = excluded.synced_at, units = excluded.units
            """, new { source, itemId, now, count }, ct);
        if (await store.OpenAsync(ct) is { } open)
        {
            await WriteAsync(Shadow, source, itemId, units, open.Settings, now, ct);
        }
        return count;
    }

    /// <summary>Reads one item from its owner into the shadow index only, with the proposed settings. Returns the number of passages stored.</summary>
    internal async Task<int> IndexShadowAsync(string source, Guid itemId, RetrievalSettings settings, CancellationToken ct)
    {
        if (!bySource.TryGetValue(source, out var owner))
        {
            return 0;
        }
        return await WriteAsync(Shadow, source, itemId, await owner.UnitsAsync(itemId, ct), settings, clock.UtcNow, ct);
    }

    private async Task<int> WriteAsync(string table, string source, Guid itemId, IReadOnlyList<SearchUnit> units, RetrievalSettings settings, DateTimeOffset now,
        CancellationToken ct)
    {
        await session.ExecAsync($"delete from {table} where source = @source and item_id = @itemId", new { source, itemId }, ct);

        var rows = new List<Row>();
        foreach (var unit in units.Where(u => SearchKinds.All.Contains(u.Kind)))
        {
            var passages = SearchText.Passages(unit.Text, settings.PassageChars, settings.OverlapChars);
            if (passages.Count == 0)
            {
                passages = [string.Empty];
            }
            rows.AddRange(passages.Select((p, i) => new Row(Ids.New(), unit, i, p)));
        }
        await EmbedAsync(rows, settings.EmbeddingModel, ct);
        foreach (var row in rows)
        {
            var u = row.Unit;
            var english = !SearchText.IsArabic(u.Title + " " + row.Body) && u.Locale.StartsWith("en", StringComparison.Ordinal);
            await session.ExecAsync(
                $"""
                insert into {table} (tenant_id, id, source, item_id, unit_key, position, kind, locale, title, body, link, classification, org_unit_id,
                  version, updated_at, synced_at, tsv, embedding, embedding_dims, embedding_model)
                values (platform.current_tenant_id(), @id, @source, @itemId, @key, @position, @kind, @locale, @title, @body, @link, @classification, @unit,
                  @version, @updated, @now,
                  setweight(to_tsvector('simple', @normTitle), 'A')
                    || case when @english then setweight(to_tsvector('english', @normTitle), 'B') else ''::tsvector end
                    || setweight(to_tsvector(case when @english then 'english' else 'simple' end::regconfig, @normBody), 'C')
                    || case when @english then setweight(to_tsvector('simple', @normBody), 'D') else ''::tsvector end,
                  @embedding::real[]::extensions.vector, @dims, @model)
                """,
                new
                {
                    id = row.Id, source, itemId, key = u.Key, position = row.Position, kind = u.Kind, locale = u.Locale, title = u.Title, body = row.Body,
                    link = u.Link, classification = u.Classification, unit = u.OrgUnitId, version = u.Version, updated = u.UpdatedAt, now,
                    normTitle = SearchText.Normalize(u.Title), normBody = SearchText.Normalize(row.Body), english,
                    embedding = row.Embedding, dims = row.Embedding?.Length, model = row.Model,
                }, ct);
        }
        return rows.Count;
    }

    private sealed class Row(Guid id, SearchUnit unit, int position, string body)
    {
        public Guid Id { get; } = id;
        public SearchUnit Unit { get; } = unit;
        public int Position { get; } = position;
        public string Body { get; } = body;
        public float[]? Embedding { get; set; }
        public string? Model { get; set; }
    }

    private async Task EmbedAsync(List<Row> rows, string? model, CancellationToken ct)
    {
        if (rows.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.SearchSemantic, ct))
        {
            return;
        }
        var eligible = rows.Where(r => Classification(r.Unit.Classification) is not DataClassification.Restricted).Take(MaxEmbeddedPassages).ToList();
        foreach (var group in eligible.GroupBy(r => Classification(r.Unit.Classification)))
        {
            foreach (var batch in group.Chunk(EmbedBatch))
            {
                var result = await gateway.EmbedAsync(new EmbeddingRequest([.. batch.Select(r => (r.Unit.Title + "\n" + r.Body).Trim())], group.Key, model), ct);
                if (!result.IsOk || result.Vectors!.Count != batch.Length)
                {
                    logger.LogInformation("Search passages left keyword only: embedding refused with {Denial}", result.Denial);
                    return;
                }
                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i].Embedding = result.Vectors[i];
                    batch[i].Model = result.Model;
                }
            }
        }
    }

    internal static DataClassification Classification(string key) => key switch
    {
        "public" => DataClassification.Public,
        "internal" => DataClassification.Internal,
        "confidential" => DataClassification.Confidential,
        _ => DataClassification.Restricted,
    };
}
