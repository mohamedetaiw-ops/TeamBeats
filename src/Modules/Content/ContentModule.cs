using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>Content and news: sites, items, versions and the lifecycle (spec 009, module map: Content).</summary>
public static class ContentModule
{
    public static readonly MigrationSource Migrations = new("content", typeof(ContentModule).Assembly);

    public static IServiceCollection AddContentModule(this IServiceCollection services)
    {
        services.AddScoped<Sites>();
        services.AddScoped<Items>();
        services.AddScoped<AudienceFactsLoader>();
        services.AddScoped<ContentScheduler>();
        services.AddScoped<ContentReviews>();
        services.AddScoped<ContentFeed>();
        services.AddScoped<ContentTaskSource>();
        services.AddScoped<Workflow.Contracts.ITaskSource>(sp => sp.GetRequiredService<ContentTaskSource>());
        services.AddMessageHandler<Workflow.Contracts.UserTaskCreated, ContentTaskSource>("content.notify_review");
        services.AddScoped<CriticalAnnouncements>();
        services.AddMessageHandler<Contracts.ContentPublished, CriticalAudience>("content.critical_audience");
        services.AddMessageHandler<Tenancy.Contracts.OrganizationMembershipChanged, CriticalAudience>("content.critical_membership");
        services.AddMessageHandler<Contracts.AckReportRequested, AckReportHandler>("content.ack_report");
        return services;
    }

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        var sites = app.MapGroup("/api/v1/content/sites").RequireTenantUser().WithTags("Content: sites");

        sites.MapGet("", async (Sites service, IDbSession session, CancellationToken ct) =>
        {
            var list = await service.ListAsync(ct);
            await session.CommitAsync(ct); // The default site is created on first use.
            return Results.Ok(list);
        });

        sites.MapPost("", async (SiteRequest request, Sites service, IDbSession session, CancellationToken ct) =>
        {
            var site = await service.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/content/sites/{site.Id}", site);
        });

        sites.MapGet("/{id:guid}", async (Guid id, Sites service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)));

        sites.MapPut("/{id:guid}", async (Guid id, SiteRequest request, Sites service, IDbSession session, CancellationToken ct) =>
        {
            var site = await service.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(site);
        });

        var settings = app.MapGroup("/api/v1/content/settings").RequireTenantUser().WithTags("Content: settings");

        settings.MapGet("", async (Sites service, CancellationToken ct) => Results.Ok(await service.SettingsAsync(ct)));

        settings.MapPut("", async (ContentSettingsRequest request, Sites service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateSettingsAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        app.MapGroup("/api/v1/content").RequireTenantUser().WithTags("Content: feed").MapGet("/feed",
            async (Guid? site, string? type, string? cursor, int? limit, ContentFeed feed, CancellationToken ct) =>
                Results.Ok(await feed.PageAsync(site, type, cursor, limit ?? 20, ct)));

        var items = app.MapGroup("/api/v1/content/items").RequireTenantUser().WithTags("Content: items");

        items.MapGet("", async (Guid? site, string? status, string? type, int? limit, Items service, IDbSession session, CancellationToken ct) =>
        {
            var list = await service.ListAsync(site, status, type, limit ?? 50, ct);
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        items.MapPost("", async (ItemRequest request, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/content/items/{item.Id}", item);
        });

        items.MapGet("/{id:guid}", async (Guid id, Items service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct)));

        items.MapPut("/{id:guid}", async (Guid id, ItemRequest request, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(item);
        });

        items.MapPost("/{id:guid}/publish", async (Guid id, PublishRequest? request, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.PublishAsync(id, request ?? new PublishRequest(null, null, null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(item);
        });

        items.MapPost("/{id:guid}/submit", async (Guid id, PublishRequest? request, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.SubmitAsync(id, request ?? new PublishRequest(null, null, null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(item);
        });

        items.MapPost("/{id:guid}/withdraw", async (Guid id, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.WithdrawAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(item);
        });

        items.MapGet("/{id:guid}/reviews", async (Guid id, Items service, CancellationToken ct) => Results.Ok(await service.ReviewHistoryAsync(id, ct)));

        items.MapPost("/{id:guid}/unpublish", async (Guid id, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.UnpublishAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(item);
        });

        items.MapPost("/{id:guid}/archive", async (Guid id, Items service, IDbSession session, CancellationToken ct) =>
        {
            var item = await service.ArchiveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(item);
        });

        items.MapGet("/{id:guid}/view", async (Guid id, Items service, CancellationToken ct) => Results.Ok(await service.ReadAsync(id, ct)));

        items.MapGet("/{id:guid}/versions", async (Guid id, Items service, CancellationToken ct) => Results.Ok(await service.VersionsAsync(id, ct)));

        // Critical announcements (US2).
        app.MapGroup("/api/v1/content/critical").RequireTenantUser().WithTags("Content: critical").MapGet("/pending",
            async (CriticalAnnouncements service, CancellationToken ct) => Results.Ok(await service.PendingAsync(ct)));

        items.MapPost("/{id:guid}/acknowledge", async (Guid id, AcknowledgeRequest? request, CriticalAnnouncements service, IDbSession session, CancellationToken ct) =>
        {
            var ack = await service.AcknowledgeAsync(id, request ?? new AcknowledgeRequest(null, null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(ack);
        });

        items.MapPost("/{id:guid}/remind-later", async (Guid id, CriticalAnnouncements service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.RemindLaterAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        items.MapPost("/{id:guid}/dismiss", async (Guid id, CriticalAnnouncements service, IDbSession session, CancellationToken ct) =>
        {
            await service.DismissAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        items.MapGet("/{id:guid}/acknowledgements", async (Guid id, CriticalAnnouncements service, CancellationToken ct) =>
            Results.Ok(await service.SummaryAsync(id, ct)));

        items.MapGet("/{id:guid}/acknowledgements/team", async (Guid id, CriticalAnnouncements service, CancellationToken ct) =>
            Results.Ok(await service.TeamAsync(id, ct)));

        items.MapPost("/{id:guid}/ack-report", async (Guid id, CriticalAnnouncements service, IDbSession session, CancellationToken ct) =>
        {
            var report = await service.RequestReportAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Accepted($"/api/v1/content/ack-reports/{report.Id}", report);
        });

        var reports = app.MapGroup("/api/v1/content/ack-reports").RequireTenantUser().WithTags("Content: critical");

        reports.MapGet("/{id:guid}", async (Guid id, CriticalAnnouncements service, CancellationToken ct) => Results.Ok(await service.ReportAsync(id, ct)));

        reports.MapGet("/{id:guid}/file", async (Guid id, CriticalAnnouncements service, TenantObjectStorage storage, CancellationToken ct) =>
            Results.File(await service.ReportFileAsync(id, storage, ct), "text/csv", $"acknowledgements-{id}.csv"));

        return app;
    }
}
