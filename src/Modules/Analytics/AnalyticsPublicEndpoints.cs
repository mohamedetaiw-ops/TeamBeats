using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;

namespace TeamBeats.Modules.Analytics;

/// <summary>The metrics the caller may query, with the minimum group size the answers follow.</summary>
public sealed record PublicMetricCatalogue(int MinGroupSize, IReadOnlyList<MetricInfo> Metrics);

/// <summary>
/// The analytics API (spec 021 R3, section 12) for customer BI tools under <c>analytics:read</c> and
/// <c>analytics.api</c>: the same metric catalogue and query as the analytics workspace, run as the person who
/// consented with their own analytics rights, so the scope, the minimum group size and the "other" merging are exactly
/// the workspace's. It returns weekly aggregates and breakdowns only; there are no raw events and no person keys.
/// </summary>
public static class AnalyticsPublicEndpoints
{
    public static void MapAnalyticsPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var analytics = app.MapPublicApi("Public: analytics");

        analytics.MapGet("/analytics/metrics", async (Workspace workspace, IEntitlementService entitlements, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.AnalyticsApi, ct);
            var view = await workspace.GetAsync(ct);
            return Results.Ok(new PublicMetricCatalogue(view.MinGroupSize, view.Metrics));
        }).RequireScope(ScopeCatalogue.AnalyticsRead).Produces<PublicMetricCatalogue>();

        analytics.MapPost("/analytics/query", async (MetricQuery query, Workspace workspace, IEntitlementService entitlements, CancellationToken ct) =>
        {
            await entitlements.DemandAsync(Capabilities.AnalyticsApi, ct);
            return Results.Ok(await workspace.QueryAsync(query, ct));
        }).RequireScope(ScopeCatalogue.AnalyticsRead).Produces<MetricResult>().WithMetadata(ReadOnlySafeMetadata.Instance);
    }
}
