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
        services.AddScoped<IAnalyticsPrivacy>(sp => sp.GetRequiredService<AnalyticsPolicies>());
        services.AddScoped<Pseudonyms>();
        services.AddScoped<IAnalyticsPseudonyms>(sp => sp.GetRequiredService<Pseudonyms>());
        services.AddScoped<AnalyticsWriter>();
        services.AddScoped<AnalyticsCollector>();
        services.AddScoped<AnalyticsAggregator>();
        services.AddScoped<AnalyticsRetention>();
        services.AddScoped<ItemAnalytics>();
        services.AddScoped<Adoption>();
        services.AddScoped<FrontendErrors>();
        services.AddScoped<Workspace>();
        services.AddScoped<AnalyticsEstimates>();
        services.AddScoped<CampaignAnalytics>();
        services.AddScoped<ServiceAnalytics>();
        services.AddScoped<ComplianceAnalytics>();
        services.AddScoped<ScopeDashboards>();
        services.AddScoped<KpiTiles>();
        services.AddScoped<ExperienceAnalytics>();
        services.AddScoped<ExperimentResults>();
        services.AddScoped<AnalyticsBenchmarks>();
        services.AddScoped<ReportDefinitions>();
        services.AddScoped<AnalyticsReports>();
        services.AddScoped<ReportRunner>();
        services.AddScoped<SentimentClassifier>();
        services.AddScoped<SentimentAnalytics>();
        services.AddScoped<IAnalyticsSentimentTrends, SentimentTrends>();
        services.AddScoped<AiUsageAnalytics>();
        services.AddScoped<DashboardSummaries>();
        services.AddScoped<SearchGaps>();
        services.AddScoped<AreaDashboards>();
        services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IShellCommands, AnalyticsCommands>();
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

        // The analytics workspace (spec 021 US2): dashboards and the metric query, scoped to the viewer's analytics coverage.
        analytics.MapGet("/workspace", async (Workspace workspace, CancellationToken ct) => Results.Ok(await workspace.GetAsync(ct)));
        analytics.MapPost("/query", async (MetricQuery query, Workspace workspace, CancellationToken ct) => Results.Ok(await workspace.QueryAsync(query, ct))).WithMetadata(TeamBeats.Entitlements.Contracts.ReadOnlySafeMetadata.Instance);

        // Campaign analytics (spec 021 US2, T011): Content decides who may see a campaign; the package needs analytics.advanced.
        analytics.MapGet("/campaigns", async (CampaignAnalytics campaigns, CancellationToken ct) => Results.Ok(await campaigns.ListAsync(ct)));
        analytics.MapGet("/campaigns/compare", async (Guid first, Guid second, CampaignAnalytics campaigns, CancellationToken ct) =>
            Results.Ok(await campaigns.CompareAsync(first, second, ct)));
        analytics.MapGet("/campaigns/{id:guid}", async (Guid id, CampaignAnalytics campaigns, CancellationToken ct) => Results.Ok(await campaigns.GetAsync(id, ct)));
        // Service analytics (spec 021 US4, T012): Services decides who may see a service; the package needs analytics.advanced.
        analytics.MapGet("/services", async (ServiceAnalytics services, CancellationToken ct) => Results.Ok(await services.ListAsync(ct)));
        analytics.MapGet("/services/{id:guid}", async (Guid id, DateOnly? from, DateOnly? to, ServiceAnalytics services, CancellationToken ct) =>
            Results.Ok(await services.GetAsync(id, from, to, ct)));
        // Policy compliance analytics (spec 021 US5, T013): Policies decides whose attestations a viewer counts.
        analytics.MapGet("/compliance", async (ComplianceAnalytics compliance, CancellationToken ct) => Results.Ok(await compliance.GetAsync(ct)));
        // Location and unit dashboards (spec 021 US6, T014): analytics.view at the unit, or for the whole tenant for a location.
        analytics.MapGet("/scopes", async (ScopeDashboards scopes, CancellationToken ct) => Results.Ok(await scopes.ListAsync(ct)));
        analytics.MapGet("/scopes/unit/{id:guid}", async (Guid id, DateOnly? from, DateOnly? to, ScopeDashboards scopes, CancellationToken ct) =>
            Results.Ok(await scopes.UnitAsync(id, from, to, ct)));
        analytics.MapGet("/scopes/location/{key}", async (string key, DateOnly? from, DateOnly? to, ScopeDashboards scopes, CancellationToken ct) =>
            Results.Ok(await scopes.LocationAsync(key, from, to, ct)));
        // Experience dashboards (spec 021 US9, T015): analytics.view for the whole tenant.
        analytics.MapGet("/experience", async (DateOnly? from, DateOnly? to, ExperienceAnalytics experience, CancellationToken ct) =>
            Results.Ok(await experience.GetAsync(from, to, ct)));
        // Sentiment (spec 021 US8, T017): labels only, with the minimum group size; analytics.view for the whole tenant.
        analytics.MapGet("/benchmarks", async (AnalyticsBenchmarks benchmarks, CancellationToken ct) => Results.Ok(await benchmarks.GetAsync(ct)));
        analytics.MapPut("/benchmarks", async (BenchmarksRequest request, AnalyticsBenchmarks benchmarks, IDbSession session, CancellationToken ct) =>
        {
            var view = await benchmarks.ChangeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        analytics.MapGet("/experiments/{id:guid}", async (Guid id, ExperimentResults results, CancellationToken ct) => Results.Ok(await results.GetAsync(id, ct)));
        analytics.MapGet("/sentiment", async (DateOnly? from, DateOnly? to, SentimentAnalytics sentiment, CancellationToken ct) =>
            Results.Ok(await sentiment.GetAsync(from, to, ct)));
        // AI usage (spec 021 section 2, FR-016): figures read from the AI Gateway, the assistant and evaluation.
        analytics.MapGet("/ai-usage", async (DateOnly? from, DateOnly? to, AiUsageAnalytics usage, CancellationToken ct) =>
            Results.Ok(await usage.GetAsync(from, to, ct)));
        // Search gaps (spec 021 US3, FR-007): from the spec 012 query feed, five people or more, no personal data.
        analytics.MapGet("/search", async (DateOnly? from, DateOnly? to, string? locale, Guid? unit, SearchGaps gaps, CancellationToken ct) =>
            Results.Ok(await gaps.GetAsync(from, to, locale, ct, unit)));
        // Knowledge, engagement, HR and security trends (spec 021 section 2, FR-002): read from their owners, minimum group size.
        analytics.MapGet("/knowledge", async (DateOnly? from, DateOnly? to, AreaDashboards areas, CancellationToken ct) =>
            Results.Ok(await areas.KnowledgeAsync(from, to, ct)));
        analytics.MapGet("/engagement", async (DateOnly? from, DateOnly? to, AreaDashboards areas, CancellationToken ct) =>
            Results.Ok(await areas.EngagementAsync(from, to, ct)));
        analytics.MapGet("/hr", async (DateOnly? from, DateOnly? to, AreaDashboards areas, CancellationToken ct) =>
            Results.Ok(await areas.HrAsync(from, to, ct)));
        analytics.MapGet("/security", async (DateOnly? from, DateOnly? to, AreaDashboards areas, CancellationToken ct) =>
            Results.Ok(await areas.SecurityAsync(from, to, ct)));
        // AI summaries of a dashboard (spec 021 US8 scenario 3): the figures it used, labelled as AI-generated.
        analytics.MapPost("/summaries", async (SummaryRequest request, DashboardSummaries summaries, CancellationToken ct) =>
            Results.Ok(await summaries.SummarizeAsync(request, ct)));
        // Scheduled reports and exports (spec 021 US7, T016): computed in the background as each recipient.
        analytics.MapGet("/reports/options", async (AnalyticsReports reports, CancellationToken ct) => Results.Ok(await reports.OptionsAsync(ct)));
        analytics.MapGet("/reports", async (AnalyticsReports reports, CancellationToken ct) => Results.Ok(await reports.ListAsync(ct)));
        analytics.MapGet("/reports/{id:guid}", async (Guid id, AnalyticsReports reports, CancellationToken ct) => Results.Ok(await reports.GetAsync(id, ct)));
        analytics.MapGet("/reports/{id:guid}/file", async (Guid id, string? format, AnalyticsReports reports, IDbSession session, CancellationToken ct) =>
        {
            var file = await reports.FileAsync(id, format, ct);
            await session.CommitAsync(ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        analytics.MapPost("/exports", async (ExportRequest request, AnalyticsReports reports, IDbSession session, CancellationToken ct) =>
        {
            var report = await reports.ExportAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Accepted(AnalyticsReports.Link(report.Id), report);
        });
        analytics.MapPost("/schedules", async (ScheduleRequest request, AnalyticsReports reports, IDbSession session, CancellationToken ct) =>
        {
            var schedule = await reports.ScheduleAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created("/api/v1/analytics/reports", schedule);
        });
        analytics.MapDelete("/schedules/{id:guid}", async (Guid id, AnalyticsReports reports, IDbSession session, CancellationToken ct) =>
        {
            await reports.DeleteScheduleAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        analytics.MapGet("/estimates", async (AnalyticsEstimates estimates, CancellationToken ct) => Results.Ok(await estimates.ViewAsync(ct)));

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
        // KPI tiles on location dashboards (spec 021 R3): the tile set, changed by people who manage tenant settings.
        analytics.MapGet("/kpi-tiles", async (KpiTiles tiles, CancellationToken ct) => Results.Ok(await tiles.ViewAsync(ct)));
        admin.MapPut("/kpi-tiles", async (KpiTileSetRequest request, KpiTiles tiles, IDbSession session, CancellationToken ct) =>
        {
            var view = await tiles.ChangeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        admin.MapPut("/estimates", async (EffortEstimatesRequest request, AnalyticsEstimates estimates, IDbSession session, CancellationToken ct) =>
        {
            var view = await estimates.ChangeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        app.MapAnalyticsPublicEndpoints();
        return app;
    }
}
