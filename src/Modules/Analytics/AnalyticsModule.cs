using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// Analytics and insights (spec 021, module map: Analytics, a derived read model). R1: the event pipeline with
/// declared schemas and pseudonymous keys, the tenant analytics policy, aggregation and retention, item reach and acknowledgement, adoption basics and the frontend error list.
/// </summary>
public static class AnalyticsModule
{
    public static readonly MigrationSource Migrations = new("analytics", typeof(AnalyticsModule).Assembly);

    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services)
    {
        services.AddScoped<AnalyticsPolicies>();
        services.AddScoped<Pseudonyms>();
        services.AddScoped<AnalyticsWriter>();
        services.AddScoped<AnalyticsCollector>();
        services.AddScoped<AnalyticsAggregator>();
        services.AddScoped<AnalyticsRetention>();
        services.AddScoped<ItemAnalytics>();
        services.AddScoped<Adoption>();
        services.AddScoped<FrontendErrors>();
        services.AddScoped<IAnalyticsEvents, AnalyticsEventsEmitter>();
        services.AddMessageHandler<AnalyticsEventEmitted, AnalyticsEventHandler>("analytics.write_event");
        return services;
    }

    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var analytics = app.MapGroup("/api/v1/analytics").RequireTenantUser().WithTags("Analytics");

        // Events from the web app: declared events only, tagged with the session's tenant and person (spec 021 plan).
        analytics.MapPost("/events", async (AnalyticsBatch batch, AnalyticsCollector collector, IDbSession session, CancellationToken ct) =>
        {
            var accepted = await collector.CollectAsync(batch, ct);
            await session.CommitAsync(ct);
            return Results.Accepted(value: accepted);
        });

        // Reach and acknowledgement of one item, for the people who look after it (spec 021 US1).
        analytics.MapGet("/items/{source}/{id:guid}", async (string source, Guid id, ItemAnalytics items, IDbSession session, CancellationToken ct) =>
        {
            var view = await items.GetAsync(source, id, ct);
            await session.CommitAsync(ct); // The tenant salt is created on first use.
            return Results.Ok(view);
        });
        analytics.MapPost("/items/{source}/{id:guid}/remind", async (string source, Guid id, RemindGroupRequest request, ItemAnalytics items, IDbSession session,
            CancellationToken ct) =>
        {
            var result = await items.RemindAsync(source, id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        var admin = analytics.MapGroup("").InAdminArea();
        admin.MapGet("/adoption", async (Adoption adoption, CancellationToken ct) => Results.Ok(await adoption.GetAsync(ct)));
        admin.MapGet("/errors", async (string? release, FrontendErrors errors, CancellationToken ct) => Results.Ok(await errors.ListAsync(release, ct)));
        admin.MapGet("/policy", async (AnalyticsPolicies policies, CancellationToken ct) => Results.Ok(await policies.ViewAsync(ct)));
        admin.MapPut("/policy", async (AnalyticsPolicyRequest request, AnalyticsPolicies policies, IDbSession session, CancellationToken ct) =>
        {
            var view = await policies.ChangeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        return app;
    }
}
