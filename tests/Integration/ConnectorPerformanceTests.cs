using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T020 and section 15: connector overhead under 30 ms at p95 excluding the external call, and 1,000 queued
/// jobs a minute. The fake system answers in process without delay, so the measured time is the runtime (package and
/// connection checks, rate limit, circuit, run record, credential, allowlist, request mapping and output mapping)
/// plus a negligible fake call, an upper bound on the overhead. The 20,000 user agent sync is measured in
/// <c>SyncAgentTests</c>.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ConnectorPerformanceTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private static readonly JsonElement Input = JsonSerializer.SerializeToElement(new { visitor = "Mona Saleh" });

    private async Task<(TestTenant T, Guid Connection, string Host)> SetupAsync()
    {
        var t = await world.CreateTenantAsync("cperf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var host = $"perf-{Guid.NewGuid():N}"[..20] + ".rest.test";
        var n = 0;
        world.Factory.Rest.On(host, _ => FakeRestApi.Json(HttpStatusCode.Created, $$$"""{"permit":{"id":"PRM-{{{Interlocked.Increment(ref n)}}}"}}"""));
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "perf-key"), "fictional-perf-value");
        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind = "custom_rest",
            displayName = "Permits",
            rateLimitPerMinute = 100000,
            settings = new { baseUrl = $"https://{host}/v1", auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "perf-key") } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Packs}/{id}/actions/create_permit", new
        {
            definition = new
            {
                method = "POST",
                path = "/permits",
                body = JsonSerializer.SerializeToElement(new { visitor = "{{visitor}}" }),
                inputs = new object[] { new { name = "visitor", type = "string", required = true } },
                output = new Dictionary<string, string> { ["permitId"] = "$.permit.id" },
                referencePath = "$.permit.id",
                sideEffect = "write",
                idempotencyHeader = "Idempotency-Key",
            },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);
        return (t, id, host);
    }

    [Fact]
    public async Task Connector_overhead_stays_under_30_ms_at_p95()
    {
        const int warmup = 10, calls = 100;
        var (t, connection, host) = await SetupAsync();
        var timings = new List<double>(calls);
        for (var i = 0; i < warmup + calls; i++)
        {
            var watch = Stopwatch.StartNew();
            var result = await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                var r = await sp.GetRequiredService<IConnectorRuntime>().InvokeAsync(new ConnectorCall(connection, "create_permit", Input, $"perf-{i}"));
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return r;
            });
            watch.Stop();
            Assert.Equal(ConnectorOutcomes.Succeeded, result.Outcome);
            if (i >= warmup)
            {
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        Assert.Equal(warmup + calls, world.Factory.Rest.SentTo(host).Count);
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(calls * 0.95) - 1];
        Assert.True(p95 < 30, $"p95 {p95:F1} ms, median {timings[calls / 2]:F1} ms");
        await DropEventsAsync(t);
    }

    /// <summary>Each run announces its outcome; other tests dispatch the shared outbox in fixed batches, so drop them.</summary>
    private Task DropEventsAsync(TestTenant t) =>
        world.Database.OwnerExecAsync("delete from messaging.outbox where tenant_id = @t and dispatched_at is null", ("t", t.Id));

    [Fact]
    public async Task A_thousand_queued_jobs_run_within_a_minute_in_batches()
    {
        const int jobs = 1000;
        var (t, connection, host) = await SetupAsync();
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var runtime = sp.GetRequiredService<IConnectorRuntime>();
            for (var i = 0; i < jobs; i++)
            {
                await runtime.EnqueueAsync(new ConnectorCall(connection, "create_permit", Input, $"job-{i}"));
            }
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

        // As the worker drains a busy tenant: one transaction per batch until a batch is not full.
        var watch = Stopwatch.StartNew();
        int ran;
        var batches = 0;
        do
        {
            ran = await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                var n = await sp.GetRequiredService<ConnectorJobs>().RunBatchAsync(DateTimeOffset.UtcNow.AddSeconds(1), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return n;
            });
            batches++;
        }
        while (ran == ConnectorJobs.BatchSize && watch.Elapsed < TimeSpan.FromMinutes(2));
        watch.Stop();

        Assert.Equal(jobs, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.run where connection_id = @c and status = 'succeeded'", ("c", connection)));
        Assert.Equal(jobs, world.Factory.Rest.SentTo(host).Count);
        Assert.True(batches >= jobs / ConnectorJobs.BatchSize);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(1), $"{jobs} jobs took {watch.Elapsed.TotalSeconds:F1} s");
        await DropEventsAsync(t);
    }
}
