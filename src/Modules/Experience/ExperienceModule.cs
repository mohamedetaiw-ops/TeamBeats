using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience;

/// <summary>Pages, templates, layouts and the component registry (spec 032; module map: Experience).</summary>
public static class ExperienceModule
{
    public static readonly MigrationSource Migrations = new("experience", typeof(ExperienceModule).Assembly);

    public static IServiceCollection AddExperienceModule(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<PlatformCatalogue>();
        services.AddSingleton<ComponentRegistry>();
        services.AddScoped<ComponentEnablement>();
        services.AddScoped<PageService>();
        return services;
    }

    public static IEndpointRouteBuilder MapExperienceEndpoints(this IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/experience").RequireTenantUser().WithTags("Experience: pages");

        x.MapGet("/catalogue", async (IEntitlementService entitlements, PlatformCatalogue catalogue, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
            return Results.Ok(new { layouts = catalogue.Layouts.Values.OrderBy(l => l.Id), templates = catalogue.Templates.Values.OrderBy(t => t.Id) });
        });

        x.MapPost("/pages", async (CreatePageRequest request, PageService pages, IDbSession session, CancellationToken ct) =>
        {
            var page = await pages.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/experience/pages/{page.Id}", page);
        });

        x.MapGet("/pages/{id:guid}", async (Guid id, HttpResponse response, PageService pages, CancellationToken ct) =>
        {
            var page = await pages.GetAsync(id, ct);
            response.Headers.ETag = page.ETag;
            return Results.Ok(page);
        });

        x.MapPut("/pages/{id:guid}/draft", async (Guid id, SaveDraftRequest request, HttpRequest http, PageService pages, IDbSession session,
            CancellationToken ct) =>
        {
            var page = await pages.SaveDraftAsync(id, http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null, request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = page.ETag;
            return Results.Ok(page);
        });

        x.MapPost("/pages/{id:guid}/validate", async (Guid id, PageService pages, CancellationToken ct) =>
            Results.Ok(new { findings = await pages.ValidateDraftAsync(id, ct) }));

        x.MapPost("/pages/{id:guid}/publish", async (Guid id, PublishRequest? request, PageService pages, IDbSession session, CancellationToken ct) =>
        {
            var release = await pages.PublishAsync(id, request ?? new PublishRequest(null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(release);
        });

        x.MapPost("/pages/{id:guid}/rollback", async (Guid id, RollbackRequest request, PageService pages, IDbSession session, CancellationToken ct) =>
        {
            var release = await pages.RollbackAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(release);
        });

        x.MapGet("/pages/{id:guid}/releases", async (Guid id, PageService pages, CancellationToken ct) => Results.Ok(await pages.ReleasesAsync(id, ct)));

        x.MapGet("/pages/{id:guid}/components", async (Guid id, string? region, PageService pages, CancellationToken ct) =>
            Results.Ok(await pages.PickerAsync(id, region, ct)));

        x.MapGet("/render", async (string? slug, PageService pages, RequestLocale locale, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(slug))
            {
                throw Primitives.AppException.NotFound();
            }
            return Results.Ok(await pages.RenderAsync(slug, locale.Locale, locale.Direction, ct));
        });

        x.MapGet("/components", async (ComponentEnablement enablement, CancellationToken ct) => Results.Ok(await enablement.ListAsync(ct)));

        x.MapPut("/components/{componentId}/enablement", async (string componentId, EnablementRequest request, ComponentEnablement enablement,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await enablement.SetAsync(componentId, request.Enabled, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        return app;
    }
}
