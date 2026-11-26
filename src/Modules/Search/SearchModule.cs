using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using C = TeamBeats.Modules.Content.Contracts;
using D = TeamBeats.Modules.Documents.Contracts;
using P = TeamBeats.Modules.Policies.Contracts;
using S = TeamBeats.Modules.Services.Contracts;
using Pe = TeamBeats.Modules.People.Contracts;
using T = TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Modules.Search;

/// <summary>Search and retrieval (spec 012, module map: Search). Owners register their <see cref="ISearchSource"/>.</summary>
public static class SearchModule
{
    public static readonly MigrationSource Migrations = new("search", typeof(SearchModule).Assembly);

    public static IServiceCollection AddSearchModule(this IServiceCollection services)
    {
        services.AddScoped<SearchIndexer>();
        services.AddScoped<SearchService>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, SearchTool>();
        services.AddScoped<SearchRebuilds>();
        services.AddScoped<SearchTuning>();
        services.AddScoped<FederatedSearch>();
        services.AddScoped<SearchSummariesService>();
        services.AddScoped<SavedSearches>();
        services.AddScoped<SearchQueryLog>();
        services.AddScoped<ISearchAnalyticsFeed>(sp => sp.GetRequiredService<SearchQueryLog>());
        services.AddScoped<SavedSearchScheduler>();
        services.AddMessageHandler<SavedSearchDue, SavedSearchChecker>("search.saved_search_check");
        services.AddScoped<SearchRetrieval>();
        services.AddScoped<ISearchRetrieval>(sp => sp.GetRequiredService<SearchRetrieval>());
        services.AddScoped<IRetrievalEvaluation>(sp => sp.GetRequiredService<SearchRetrieval>());
        services.AddScoped<RetrievalStore>();
        services.AddScoped<RetrievalConfigs>();
        // Changes and deletions reach the index within 5 minutes (FR-006); later ones are counted for the operators' alert.
        services.AddConsumerLagObjective("search.index_", SearchIndexer.LagObjective);
        services.AddMessageHandler<SearchItemChanged, SearchIndexer>("search.index_item");
        services.AddMessageHandler<C.ContentPublished, SearchIndexer>("search.index_content_published");
        services.AddMessageHandler<C.ContentUnpublished, SearchIndexer>("search.index_content_unpublished");
        services.AddMessageHandler<C.ContentExpired, SearchIndexer>("search.index_content_expired");
        services.AddMessageHandler<D.DocumentVersionPublished, SearchIndexer>("search.index_document_published");
        services.AddMessageHandler<D.DocumentDeleted, SearchIndexer>("search.index_document_deleted");
        services.AddMessageHandler<D.DocumentWithdrawn, SearchIndexer>("search.index_document_withdrawn");
        services.AddMessageHandler<D.DocumentDisposed, SearchIndexer>("search.index_document_disposed");
        services.AddMessageHandler<P.PolicyPublished, SearchIndexer>("search.index_policy_published");
        services.AddMessageHandler<P.PolicySuperseded, SearchIndexer>("search.index_policy_superseded");
        services.AddMessageHandler<S.ServicePublished, SearchIndexer>("search.index_service_published");
        services.AddMessageHandler<S.ServiceRetired, SearchIndexer>("search.index_service_retired");
        services.AddMessageHandler<Pe.ProfileUpdated, SearchIndexer>("search.index_profile");
        services.AddMessageHandler<T.TenantMembershipDisabled, SearchIndexer>("search.index_membership_disabled");
        services.AddMessageHandler<T.OrganizationMembershipChanged, SearchIndexer>("search.index_org_membership");
        services.AddMessageHandler<TeamBeats.Modules.Identity.Contracts.UserProvisioned, SearchIndexer>("search.index_user_provisioned");
        services.AddMessageHandler<TeamBeats.Modules.Identity.Contracts.UserDeprovisioned, SearchIndexer>("search.index_user_deprovisioned");
        services.AddMessageHandler<SearchRebuildRequested, SearchRebuilds>("search.rebuild");
        services.AddMessageHandler<RetrievalShadowBuildRequested, RetrievalShadowBuilder>("search.retrieval_shadow_build");
        services.AddMessageHandler<TeamBeats.Modules.Evaluation.Contracts.EvaluationRetrievalGateDecided, RetrievalGateDecisions>("search.retrieval_gate_decided");
        return services;
    }

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/search").RequireTenantUser().WithTags("Search");

        root.MapGet("", async (string? q, string? kind, DateTimeOffset? from, DateTimeOffset? to, int? page, SearchService search, SearchQueryLog log,
            IDbSession session, CancellationToken ct) =>
        {
            var results = await search.SearchAsync(q, kind, from, to, page, ct, personalize: true);
            // The first page of a search is one event of the query log (FR-013), so the page can report the first click.
            if (results.Page == 1)
            {
                results = results with { QueryId = await log.RecordAsync(results.Query, results.Total, ct) };
            }
            await session.CommitAsync(ct); // The query event is queued through the outbox.
            return Results.Ok(results);
        });
        root.MapPost("/clicks", async (SearchClickRequest request, SearchQueryLog log, IDbSession session, CancellationToken ct) =>
        {
            await log.ClickAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        root.MapGet("/query-text", async (SearchQueryLog log, CancellationToken ct) => Results.Ok(await log.GetSettingAsync(ct)));
        root.MapPut("/query-text", async (QueryTextRequest request, SearchQueryLog log, IDbSession session, CancellationToken ct) =>
        {
            var view = await log.SetSettingAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/suggest", async (string? q, SearchService search, CancellationToken ct) => Results.Ok(await search.SuggestAsync(q, ct)));

        // Federated results (US5) are asked for after the TeamBeats results, so a slow source never holds them up.
        root.MapGet("/federated", async (string? q, FederatedSearch federated, CancellationToken ct) => Results.Ok(await federated.QueryAsync(q, ct)));
        // The AI summary (US6) is asked for after the results too, so they never wait for a model.
        root.MapGet("/summary", async (string? q, SearchSummariesService summaries, IDbSession session, CancellationToken ct) =>
        {
            var view = await summaries.SummaryAsync(q, ct);
            await session.CommitAsync(ct); // The summary is kept as an assistant turn for feedback.
            return Results.Ok(view);
        });
        root.MapGet("/ai-summary", async (SearchSummariesService summaries, CancellationToken ct) => Results.Ok(await summaries.GetAsync(ct)));
        root.MapPut("/ai-summary", async (AiSummaryRequest request, SearchSummariesService summaries, IDbSession session, CancellationToken ct) =>
        {
            var view = await summaries.SetAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        // Saved searches with daily alerts (US7, FR-019): each person sees and changes only their own.
        root.MapGet("/saved", async (SavedSearches saved, CancellationToken ct) => Results.Ok(await saved.ListAsync(ct)));
        root.MapPost("/saved", async (SavedSearchRequest request, SavedSearches saved, IDbSession session, CancellationToken ct) =>
        {
            var view = await saved.SaveAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/search/saved/{view.Id}", view);
        });
        root.MapPut("/saved/{id:guid}/alerts", async (Guid id, SavedSearchAlertsRequest request, SavedSearches saved, IDbSession session, CancellationToken ct) =>
        {
            var view = await saved.SetAlertsAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        root.MapDelete("/saved/{id:guid}", async (Guid id, SavedSearches saved, IDbSession session, CancellationToken ct) =>
        {
            await saved.RemoveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        root.MapGet("/federated-sources", async (FederatedSearch federated, CancellationToken ct) => Results.Ok(await federated.ListAsync(ct)));
        root.MapPut("/federated-sources/{source}", async (string source, FederatedSourceRequest request, FederatedSearch federated, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await federated.SetAsync(source, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/rebuilds", async (SearchRebuilds rebuilds, CancellationToken ct) => Results.Ok(await rebuilds.ListAsync(ct)));

        root.MapPost("/rebuilds", async (SearchRebuilds rebuilds, IDbSession session, CancellationToken ct) =>
        {
            var view = await rebuilds.RequestAsync(ct);
            await session.CommitAsync(ct);
            return Results.Accepted($"/api/v1/search/rebuilds", view);
        });

        // Retrieval configuration with its shadow index and release gate (US3 scenario 3, FR-008).
        root.MapGet("/retrieval-config", async (RetrievalConfigs configs, CancellationToken ct) => Results.Ok(await configs.GetAsync(ct)));

        root.MapPost("/retrieval-config", async (RetrievalChangeRequest request, RetrievalConfigs configs, IDbSession session, CancellationToken ct) =>
        {
            var view = await configs.ProposeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Accepted("/api/v1/search/retrieval-config", view);
        });

        root.MapPost("/retrieval-config/{id:guid}/apply", async (Guid id, RetrievalApplyRequest request, RetrievalConfigs configs, IDbSession session, CancellationToken ct) =>
        {
            var view = await configs.ApplyAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/retrieval-config/{id:guid}/cancel", async (Guid id, RetrievalConfigs configs, IDbSession session, CancellationToken ct) =>
        {
            var view = await configs.CancelAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Best bets and synonyms (US4, FR-010).
        root.MapGet("/best-bets", async (SearchTuning tuning, CancellationToken ct) => Results.Ok(new { bestBets = await tuning.ListBestBetsAsync(ct) }));

        root.MapPost("/best-bets", async (BestBetRequest request, SearchTuning tuning, IDbSession session, CancellationToken ct) =>
        {
            var view = await tuning.AddBestBetAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/search/best-bets/{view.Id}", view);
        });

        root.MapDelete("/best-bets/{id:guid}", async (Guid id, SearchTuning tuning, IDbSession session, CancellationToken ct) =>
        {
            await tuning.RemoveBestBetAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/synonyms", async (SearchTuning tuning, CancellationToken ct) => Results.Ok(new { synonymSets = await tuning.ListSynonymsAsync(ct) }));

        root.MapPost("/synonyms", async (SynonymSetRequest request, SearchTuning tuning, IDbSession session, CancellationToken ct) =>
        {
            var view = await tuning.AddSynonymsAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/search/synonyms/{view.Id}", view);
        });

        root.MapPut("/synonyms/{id:guid}", async (Guid id, SynonymSetRequest request, SearchTuning tuning, IDbSession session, CancellationToken ct) =>
        {
            var view = await tuning.UpdateSynonymsAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapDelete("/synonyms/{id:guid}", async (Guid id, SearchTuning tuning, IDbSession session, CancellationToken ct) =>
        {
            await tuning.RemoveSynonymsAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        app.MapSearchPublicEndpoints();
        return app;
    }
}
