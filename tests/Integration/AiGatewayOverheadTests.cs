using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T017 and the performance requirement "gateway overhead under 50 ms at p95 excluding the provider call". The
/// fake OpenRouter answers in process without delay, so the measured time is the pipeline (entitlement, quota, policy,
/// classification, routing, output checks and the call record) plus a negligible fake call, an upper bound on the overhead.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiGatewayOverheadTests(TenantWorld world)
{
    private const int Warmup = 10;
    private const int Calls = 100;
    private const double LimitMs = 50;

    [Fact]
    public async Task Gateway_overhead_stays_under_50_ms_at_p95()
    {
        var t = await world.CreateTenantAsync("aiperf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var before = world.Factory.OpenRouter.Requests;

        var timings = await world.Factory.AsUserAsync(t.Id, t["emp"].Id, async sp =>
        {
            var gateway = sp.GetRequiredService<IAiGateway>();
            var request = new AiRequest(AiUseCases.Assistant, [new AiMessage(AiRoles.User, "What is the leave policy?")], DataClassification.Internal);
            var result = new List<double>(Calls);
            for (var i = 0; i < Warmup + Calls; i++)
            {
                var watch = Stopwatch.StartNew();
                var answer = await gateway.ChatAsync(request, CancellationToken.None);
                watch.Stop();
                Assert.True(answer.IsOk, answer.Denial);
                if (i >= Warmup)
                {
                    result.Add(watch.Elapsed.TotalMilliseconds);
                }
            }
            return result;
        });

        Assert.Equal(Warmup + Calls, world.Factory.OpenRouter.Requests - before);
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(Calls * 0.95) - 1];
        Assert.True(p95 < LimitMs, $"p95 {p95:F1} ms, median {timings[Calls / 2]:F1} ms");
    }
}
