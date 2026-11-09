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
        services.AddScoped<SearchRebuilds>();
        services.AddMessageHandler<SearchItemChanged, SearchIndexer>("search.index_item");
        services.AddMessageHandler<C.ContentPublished, SearchIndexer>("search.index_content_published");
        services.AddMessageHandler<C.ContentUnpublished, SearchIndexer>("search.index_content_unpublished");
        services.AddMessageHandler<C.ContentExpired, SearchIndexer>("search.index_content_expired");
        services.AddMessageHandler<D.DocumentVersionPublished, SearchIndexer>("search.index_document_published");
        services.AddMessageHandler<D.DocumentDeleted, SearchIndexer>("search.index_document_deleted");
        services.AddMessageHandler<D.DocumentWithdrawn, SearchIndexer>("search.index_document_withdrawn");
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
        return services;
    }

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/search").RequireTenantUser().WithTags("Search");

        root.MapGet("", async (string? q, string? kind, DateTimeOffset? from, DateTimeOffset? to, int? page, SearchService search, CancellationToken ct) =>
            Results.Ok(await search.SearchAsync(q, kind, from, to, page, ct)));

        root.MapGet("/suggest", async (string? q, SearchService search, CancellationToken ct) => Results.Ok(await search.SuggestAsync(q, ct)));

        root.MapGet("/rebuilds", async (SearchRebuilds rebuilds, CancellationToken ct) => Results.Ok(await rebuilds.ListAsync(ct)));

        root.MapPost("/rebuilds", async (SearchRebuilds rebuilds, IDbSession session, CancellationToken ct) =>
        {
            var view = await rebuilds.RequestAsync(ct);
            await session.CommitAsync(ct);
            return Results.Accepted($"/api/v1/search/rebuilds", view);
        });

        return app;
    }
}
