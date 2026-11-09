using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Testing;

/// <summary>
/// Drives the release gate of a proposed model route change the way the outbox would, one step at a time: spec 034
/// starts the gate, its runs go on in batches, and spec 013 takes the decision.
/// </summary>
public static class RouteGateFlow
{
    /// <summary>Hands the proposal to spec 034, as <see cref="AiModelRouteChangeProposed"/> would arrive.</summary>
    public static async Task StartAsync(TenantWorld world, Guid tenantId, Guid routeChangeId)
    {
        var row = await world.Database.TenantScalarAsync<string>(tenantId,
            "select json_build_object('useCases', use_cases, 'by', proposed_by)::text from ai.route_change where id = @id", ("id", routeChangeId));
        using var doc = System.Text.Json.JsonDocument.Parse(row!);
        var useCases = doc.RootElement.GetProperty("useCases").EnumerateArray().Select(u => u.GetString()!).ToList();
        var by = doc.RootElement.GetProperty("by").GetGuid();
        await world.Factory.InScopeAsync(tenantId, async sp =>
        {
            var empty = new Dictionary<string, IReadOnlyList<string>>();
            await sp.GetRequiredService<RouteGateStarter>().HandleAsync(new AiModelRouteChangeProposed(routeChangeId, useCases, empty, empty, by), CancellationToken.None);
            return true;
        });
    }

    /// <summary>Runs every gate run of the route change to its end.</summary>
    public static async Task RunAsync(TenantWorld world, Guid tenantId, Guid routeChangeId)
    {
        for (var i = 0; i < 50; i++)
        {
            var run = await world.Database.TenantScalarAsync<Guid>(tenantId,
                "select id from evaluation.run where route_change_id = @id and status in ('queued', 'running') order by created_at limit 1", ("id", routeChangeId));
            if (run == Guid.Empty)
            {
                return;
            }
            await world.Factory.InScopeAsync(tenantId, async sp =>
            {
                await sp.GetRequiredService<EvaluationRunner>().HandleAsync(new EvaluationRunRequested(run), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
        throw new InvalidOperationException("The route gate runs did not finish.");
    }

    /// <summary>Hands the gate decision to spec 013, as <see cref="EvaluationRouteGateDecided"/> would arrive.</summary>
    public static async Task DecideAsync(TenantWorld world, Guid tenantId, Guid routeChangeId)
    {
        var row = await world.Database.TenantScalarAsync<string>(tenantId,
            "select json_build_object('result', result, 'failures', failures)::text from evaluation.route_gate where route_change_id = @id and status = 'decided'",
            ("id", routeChangeId)) ?? throw new InvalidOperationException("The route gate has not decided.");
        using var doc = System.Text.Json.JsonDocument.Parse(row);
        var result = doc.RootElement.GetProperty("result").GetString()!;
        var failures = doc.RootElement.GetProperty("failures").EnumerateArray().Select(f => f.GetString()!).ToList();
        await world.Factory.InScopeAsync(tenantId, async sp =>
        {
            await sp.GetRequiredService<RouteGateDecisions>().HandleAsync(new EvaluationRouteGateDecided(routeChangeId, result, failures), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
    }
}
