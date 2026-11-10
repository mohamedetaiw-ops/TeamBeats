using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>The AI gateway and model governance (spec 013, module map: AiGateway). Adapters register themselves as <see cref="IModelProvider"/>.</summary>
public static class AiGatewayModule
{
    public static readonly MigrationSource Migrations = new("ai", typeof(AiGatewayModule).Assembly);

    public static IServiceCollection AddAiGatewayModule(this IServiceCollection services)
    {
        services.AddSingleton<ProviderCircuits>();
        services.AddScoped<ModelCatalogue>();
        services.AddScoped<ModelPolicies>();
        services.AddMessageHandler<Evaluation.Contracts.EvaluationRouteGateDecided, RouteGateDecisions>("ai.route_gate_decided");
        services.AddSingleton<DebugLogProtector>();
        services.AddScoped<DebugWindows>();
        services.AddScoped<UsageRecorder>();
        services.AddScoped<UsageReports>();
        services.AddScoped<IAiAgentUsage, AiAgentUsage>();
        services.AddScoped<AiGatewayService>();
        services.AddScoped<IAiGateway>(sp => sp.GetRequiredService<AiGatewayService>());
        services.AddScoped<IAiClassificationPolicy>(sp => sp.GetRequiredService<AiGatewayService>());
        return services;
    }

    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapAiGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var ai = app.MapGroup("/api/v1/ai").RequireTenantUser().InAdminArea().WithTags("AI gateway");

        ai.MapGet("/catalogue", async (ModelPolicies policies, CancellationToken ct) => Results.Ok(await policies.CatalogueAsync(ct)));

        ai.MapGet("/policy", async (ModelPolicies policies, HttpResponse response, CancellationToken ct) =>
        {
            var view = await policies.GetAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        ai.MapPut("/policy", async (ModelPolicyRequest request, HttpRequest http, ModelPolicies policies, IDbSession session, CancellationToken ct) =>
        {
            var result = await policies.UpdateAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = result.Policy.ETag;
            return result.Applied ? Results.Ok(result) : Results.Accepted(value: result);
        });

        ai.MapGet("/usage", async (DateOnly? from, DateOnly? to, string? groupBy, UsageReports reports, CancellationToken ct) =>
            Results.Ok(await reports.ViewAsync(from, to, groupBy, ct)));

        ai.MapGet("/debug-windows", async (DebugWindows windows, CancellationToken ct) => Results.Ok(await windows.ListAsync(ct)));

        ai.MapPost("/debug-windows", async (DebugWindowRequest request, DebugWindows windows, IDbSession session, CancellationToken ct) =>
        {
            var view = await windows.OpenAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/ai/debug-windows/{view.Id}", view);
        });

        ai.MapDelete("/debug-windows/{id:guid}", async (Guid id, DebugWindows windows, IDbSession session, CancellationToken ct) =>
        {
            var view = await windows.CloseAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        ai.MapGet("/debug-windows/{id:guid}/entries", async (Guid id, DebugWindows windows, CancellationToken ct) => Results.Ok(await windows.EntriesAsync(id, ct)));

        ai.MapGet("/route-changes", async (ModelPolicies policies, CancellationToken ct) => Results.Ok(await policies.RouteChangesAsync(ct)));

        ai.MapPost("/route-changes/{id:guid}/override", async (Guid id, RouteOverrideRequest request, ModelPolicies policies, IDbSession session, CancellationToken ct) =>
        {
            var result = await policies.OverrideAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        var platform = app.MapGroup("/api/v1/platform/ai").RequirePlatformOperator().WithTags("Platform: AI");

        platform.MapGet("/catalogue", async (ModelCatalogue catalogue, CancellationToken ct) =>
        {
            var snapshot = await catalogue.SnapshotAsync(ct);
            return Results.Ok(new { models = snapshot.Models.Values, routes = snapshot.PlatformRoutes });
        });

        platform.MapPut("/catalogue", async (CatalogueEntryRequest request, ModelCatalogue catalogue, IDbSession session, CancellationToken ct) =>
        {
            var entry = await catalogue.PutAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(entry);
        });

        platform.MapPut("/routes/{useCase}", async (string useCase, PlatformRouteRequest request, ModelCatalogue catalogue, IDbSession session, CancellationToken ct) =>
        {
            var models = await catalogue.PutRouteAsync(useCase, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { useCase, models });
        });

        return app;
    }
}
