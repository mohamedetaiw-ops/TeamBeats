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
        services.AddScoped<Requests>();
        services.AddScoped<Attachments>();
        services.AddScoped<Tasks>();
        services.AddScoped<Delegations>();
        services.AddScoped<Workflow.Contracts.IDelegations>(sp => sp.GetRequiredService<Delegations>());
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

        catalogue.MapGet("/{id:guid}/prefill", async (Guid id, Guid? @for, Requests requests, CancellationToken ct) =>
            Results.Ok(await requests.PrefillAsync(id, @for, ct)));

        var requests = app.MapGroup("/api/v1/requests").RequireTenantUser().WithTags("Services: requests");

        requests.MapPost("", async (RequestSubmit request, Requests service, IDbSession session, CancellationToken ct) =>
        {
            var (detail, created) = await service.SubmitAsync(request, ct);
            await session.CommitAsync(ct);
            return created ? Results.Created($"/api/v1/requests/{detail.Request.Id}", detail) : Results.Ok(detail);
        });

        requests.MapGet("", async (string? status, int? limit, Requests service, CancellationToken ct) =>
            Results.Ok(await service.MineAsync(status, limit ?? 50, ct)));

        requests.MapGet("/{id:guid}", async (Guid id, Requests service, CancellationToken ct) => Results.Ok(await service.DetailAsync(id, ct)));

        requests.MapPost("/{id:guid}/cancel", async (Guid id, Requests service, IDbSession session, CancellationToken ct) =>
        {
            var detail = await service.CancelAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        requests.MapPost("/{id:guid}/resubmit", async (Guid id, RequestResubmit request, Requests service, IDbSession session, CancellationToken ct) =>
        {
            var detail = await service.ResubmitAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(detail);
        });

        var tasks = app.MapGroup("/api/v1/tasks").RequireTenantUser().WithTags("Services: task inbox");

        tasks.MapGet("", async (Guid? service, Tasks inbox, CancellationToken ct) => Results.Ok(await inbox.InboxAsync(service, ct)));

        tasks.MapGet("/count", async (Tasks inbox, CancellationToken ct) => Results.Ok(await inbox.CountAsync(ct)));

        tasks.MapPost("/{id:guid}/decision", async (Guid id, TaskDecision decision, Tasks inbox, IDbSession session, CancellationToken ct) =>
        {
            var task = await inbox.DecideAsync(id, decision, ct);
            await session.CommitAsync(ct);
            return Results.Ok(task);
        });

        var delegation = app.MapGroup("/api/v1/me/delegation").RequireTenantUser().WithTags("Services: delegation");

        delegation.MapGet("", async (Delegations delegations, CancellationToken ct) => Results.Ok(new { delegation = await delegations.MineAsync(ct) }));

        delegation.MapPut("", async (DelegationRequest request, Delegations delegations, IDbSession session, CancellationToken ct) =>
        {
            var view = await delegations.SetAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { delegation = view });
        });

        delegation.MapDelete("", async (Delegations delegations, IDbSession session, CancellationToken ct) =>
        {
            await delegations.EndAsync(ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        requests.MapPost("/attachments", async (string? name, HttpRequest http, Attachments files, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[Attachments.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > Attachments.MaxBytes)
                {
                    throw Primitives.AppException.Validation("file", "validation.services.attachment_size");
                }
            }
            var view = await files.UploadAsync(name, http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/requests/attachments/{view.Id}", view);
        }).DisableAntiforgery();

        requests.MapGet("/{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, Requests service, Attachments files,
            HttpResponse response, CancellationToken ct) =>
        {
            await service.VisibleRowAsync(id, ct);
            var (content, type, fileName) = await files.OpenAsync(id, attachmentId, ct);
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return Results.File(content, type, fileName);
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

        admin.MapGet("/{id:guid}/preview", async (Guid id, Requests requests, CancellationToken ct) => Results.Ok(await requests.PreviewAsync(id, ct)));

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
