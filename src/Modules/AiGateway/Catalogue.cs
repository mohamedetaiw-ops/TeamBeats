using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.AiGateway;

public static class ModelCapabilities
{
    public const string Chat = "chat";
    public const string Tools = "tools";
    public const string StructuredOutput = "structured_output";
    public const string Vision = "vision";
    public const string Embeddings = "embeddings";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Chat, Tools, StructuredOutput, Vision, Embeddings };
}

/// <summary>A model of the platform catalogue (spec 013 FR-005). Prices are USD per million tokens.</summary>
public sealed record CatalogueEntry(
    string Model,
    string Provider,
    Dictionary<string, string> Names,
    IReadOnlyList<string> Capabilities,
    int ContextTokens,
    IReadOnlyList<string> Regions,
    bool ZeroRetention,
    bool NoTraining,
    string MaxClassification,
    decimal InputPrice,
    decimal OutputPrice,
    string Status,
    DateOnly? DeprecatedOn)
{
    public bool Active => Status != "blocked";

    public bool Can(string capability) => Capabilities.Contains(capability);

    public DataClassification Ceiling => Classifications.Parse(MaxClassification) ?? DataClassification.Internal;

    /// <summary>Whether the catalogue allows Confidential for this model at all: zero retention, no training and a Confidential ceiling.</summary>
    public bool ConfidentialEligible => ZeroRetention && NoTraining && Ceiling >= DataClassification.Confidential;
}

public static class Classifications
{
    public static DataClassification? Parse(string? value) => value switch
    {
        "public" => DataClassification.Public,
        "internal" => DataClassification.Internal,
        "confidential" => DataClassification.Confidential,
        "restricted" => DataClassification.Restricted,
        _ => null,
    };

    public static string Key(DataClassification value) => value.ToString().ToLowerInvariant();
}

public sealed record CatalogueSnapshot(IReadOnlyDictionary<string, CatalogueEntry> Models, IReadOnlyDictionary<string, IReadOnlyList<string>> PlatformRoutes);

public sealed record CatalogueEntryRequest(
    string? Model,
    string? Provider,
    Dictionary<string, string>? Names,
    List<string>? Capabilities,
    int? ContextTokens,
    List<string>? Regions,
    bool? ZeroRetention,
    bool? NoTraining,
    string? MaxClassification,
    decimal? InputPrice,
    decimal? OutputPrice,
    string? Status,
    DateOnly? DeprecatedOn);

public sealed record PlatformRouteRequest(List<string>? Models);

/// <summary>
/// The platform model catalogue and default model set. Read by every tenant's gateway calls through a short cache so a
/// change by the platform operator applies within the 60 seconds of US1 scenario 5; written only on the platform host.
/// </summary>
public sealed class ModelCatalogue(IDbSession session, IAuditWriter audit, IClock clock)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    public static readonly IReadOnlySet<string> Providers = new HashSet<string>(StringComparer.Ordinal) { "openrouter" };
    public const int MaxRouteLength = 10;

    private static readonly Lock Gate = new();
    private static (CatalogueSnapshot Snapshot, DateTimeOffset At)? cached;

    internal static void Invalidate()
    {
        lock (Gate)
        {
            cached = null;
        }
    }

    public async Task<CatalogueSnapshot> SnapshotAsync(CancellationToken ct)
    {
        lock (Gate)
        {
            if (cached is { } c && clock.UtcNow - c.At < CacheFor)
            {
                return c.Snapshot;
            }
        }
        var models = await session.QueryAsync(
            """
            select model, provider, names::text, capabilities, context_tokens, regions, zero_retention, no_training, max_classification,
                   input_price, output_price, status, deprecated_on
            from ai.model_catalogue order by model
            """, null, Read, ct);
        var routes = await session.QueryAsync("select use_case, models from ai.platform_route", null,
            r => (r.GetString(0), (IReadOnlyList<string>)[.. r.GetFieldValue<string[]>(1)]), ct);
        var snapshot = new CatalogueSnapshot(models.ToDictionary(m => m.Model, StringComparer.Ordinal), routes.ToDictionary(r => r.Item1, r => r.Item2, StringComparer.Ordinal));
        lock (Gate)
        {
            cached = (snapshot, clock.UtcNow);
        }
        return snapshot;
    }

    private static CatalogueEntry Read(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2)) ?? [],
        [.. r.GetFieldValue<string[]>(3)], r.GetInt32(4), [.. r.GetFieldValue<string[]>(5)], r.GetBoolean(6), r.GetBoolean(7), r.GetString(8),
        r.GetDecimal(9), r.GetDecimal(10), r.GetString(11), r.Get<DateOnly?>("deprecated_on"));

    /// <summary>Adds or changes one catalogue entry (platform operator). A change of terms applies to new calls at once (edge cases).</summary>
    public async Task<CatalogueEntry> PutAsync(CatalogueEntryRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var model = request.Model?.Trim() ?? string.Empty;
        if (model.Length is < 3 or > 120 || !model.Contains('/', StringComparison.Ordinal))
        {
            errors.Add("model", "validation.ai.model_name");
        }
        if (request.Provider is null || !Providers.Contains(request.Provider))
        {
            errors.Add("provider", "validation.ai.provider");
        }
        var capabilities = request.Capabilities ?? [];
        if (capabilities.Count == 0 || capabilities.Any(c => !ModelCapabilities.All.Contains(c)))
        {
            errors.Add("capabilities", "validation.ai.capabilities");
        }
        if (request.ContextTokens is not (> 0 and <= 10_000_000))
        {
            errors.Add("contextTokens", "validation.ai.out_of_range");
        }
        var regions = request.Regions ?? [];
        if (regions.Any(r => r.Length is < 2 or > 20))
        {
            errors.Add("regions", "validation.ai.region");
        }
        var ceiling = request.MaxClassification ?? "internal";
        if (ceiling is not ("public" or "internal" or "confidential"))
        {
            // Restricted never goes to a shared provider (FR-007), so the catalogue cannot say otherwise.
            errors.Add("maxClassification", "validation.ai.classification");
        }
        if (request.InputPrice is < 0 || request.OutputPrice is < 0)
        {
            errors.Add("price", "validation.ai.out_of_range");
        }
        var status = request.Status ?? "active";
        if (status is not ("active" or "deprecated" or "blocked"))
        {
            errors.Add("status", "validation.ai.status");
        }
        errors.ThrowIfAny();

        var entry = new CatalogueEntry(model, request.Provider!, request.Names ?? [], [.. capabilities.Distinct()], request.ContextTokens!.Value, [.. regions.Distinct()],
            request.ZeroRetention ?? false, request.NoTraining ?? false, ceiling, request.InputPrice ?? 0, request.OutputPrice ?? 0, status, request.DeprecatedOn);
        await session.ExecAsync(
            """
            insert into ai.model_catalogue (model, provider, names, capabilities, context_tokens, regions, zero_retention, no_training, max_classification,
                                            input_price, output_price, status, deprecated_on, updated_at)
            values (@model, @provider, @names, @capabilities, @context, @regions, @zero, @noTraining, @ceiling, @input, @output, @status, @deprecated::date, @at)
            on conflict (model) do update set provider = excluded.provider, names = excluded.names, capabilities = excluded.capabilities,
              context_tokens = excluded.context_tokens, regions = excluded.regions, zero_retention = excluded.zero_retention, no_training = excluded.no_training,
              max_classification = excluded.max_classification, input_price = excluded.input_price, output_price = excluded.output_price,
              status = excluded.status, deprecated_on = excluded.deprecated_on, updated_at = excluded.updated_at
            """,
            new
            {
                model, provider = entry.Provider, names = Jsonb.From(entry.Names), capabilities = entry.Capabilities.ToArray(), context = entry.ContextTokens,
                regions = entry.Regions.ToArray(), zero = entry.ZeroRetention, noTraining = entry.NoTraining, ceiling, input = entry.InputPrice,
                output = entry.OutputPrice, status, deprecated = entry.DeprecatedOn, at = clock.UtcNow,
            }, ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.AiCatalogueChanged, "ai_model", model, Changes: entry), null, ct);
        session.OnCommitted(Invalidate);
        return entry;
    }

    /// <summary>Sets the platform default model and fallbacks of one use case. Every model must be in the catalogue with the right capability.</summary>
    public async Task<IReadOnlyList<string>> PutRouteAsync(string useCase, PlatformRouteRequest request, CancellationToken ct)
    {
        if (!AiUseCases.All.Contains(useCase))
        {
            throw AppException.NotFound();
        }
        var snapshot = await SnapshotAsync(ct);
        var models = (request.Models ?? []).Distinct(StringComparer.Ordinal).ToList();
        var capability = useCase == AiUseCases.Embed ? ModelCapabilities.Embeddings : ModelCapabilities.Chat;
        if (models.Count > MaxRouteLength || models.Any(m => !snapshot.Models.TryGetValue(m, out var e) || !e.Can(capability)))
        {
            throw AppException.Validation("models", "validation.ai.route_model");
        }
        await session.ExecAsync(
            "insert into ai.platform_route (use_case, models) values (@useCase, @models) on conflict (use_case) do update set models = excluded.models",
            new { useCase, models = models.ToArray() }, ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.AiCatalogueChanged, "ai_platform_route", useCase, Changes: new { models }), null, ct);
        session.OnCommitted(Invalidate);
        return models;
    }
}
