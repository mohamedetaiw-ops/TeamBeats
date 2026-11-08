using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>Self services, service catalogue and requests (spec 016, module map: Services).</summary>
public static class ServicesModule
{
    public static readonly MigrationSource Migrations = new("services", typeof(ServicesModule).Assembly);

    public static IServiceCollection AddServicesModule(this IServiceCollection services)
    {
        services.AddScoped<Catalogue>();
        services.AddScoped<ServiceAdmin>();
        return services;
    }

    public static IEndpointRouteBuilder MapServicesEndpoints(this IEndpointRouteBuilder app)
    {
        var catalogue = app.MapGroup("/api/v1/services").RequireTenantUser().WithTags("Services: catalogue");

        catalogue.MapGet("", async (string? q, Guid? category, Catalogue services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.ListAsync(q, category, ct);
            await session.CommitAsync(ct); // Module services join the catalogue on first read.
            return Results.Ok(view);
        });

        catalogue.MapGet("/{id:guid}", async (Guid id, Catalogue services, IDbSession session, CancellationToken ct) =>
        {
            var detail = await services.DetailAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        catalogue.MapPut("/{id:guid}/favourite", async (Guid id, Catalogue services, IDbSession session, CancellationToken ct) =>
        {
            await services.SetFavouriteAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        catalogue.MapDelete("/{id:guid}/favourite", async (Guid id, Catalogue services, IDbSession session, CancellationToken ct) =>
        {
            await services.SetFavouriteAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        catalogue.MapPost("/{id:guid}/used", async (Guid id, Catalogue services, IDbSession session, CancellationToken ct) =>
        {
            await services.MarkUsedAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        var admin = app.MapGroup("/api/v1/services/admin").RequireTenantUser().InAdminArea().WithTags("Services: builder");

        admin.MapGet("", async (ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var list = await services.ListAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        admin.MapPost("", async (ServiceSettingsRequest request, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/services/admin/{view.Service.Id}", view);
        });

        admin.MapGet("/{id:guid}", async (Guid id, ServiceAdmin services, CancellationToken ct) => Results.Ok(await services.GetAsync(id, ct)));

        admin.MapPut("/{id:guid}", async (Guid id, ServiceSettingsRequest request, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        admin.MapPut("/{id:guid}/draft", async (Guid id, ServiceDraftRequest request, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.SaveDraftAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        admin.MapPost("/{id:guid}/publish", async (Guid id, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.PublishAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        admin.MapPost("/{id:guid}/retire", async (Guid id, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.RetireAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        admin.MapPost("/categories", async (CategoryRequest request, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.CreateCategoryAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/services/admin/categories/{view.Id}", view);
        });

        admin.MapPut("/categories/{id:guid}", async (Guid id, CategoryRequest request, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            var view = await services.UpdateCategoryAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        admin.MapDelete("/categories/{id:guid}", async (Guid id, ServiceAdmin services, IDbSession session, CancellationToken ct) =>
        {
            await services.DeleteCategoryAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}
