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
/// Spec 017 SC-001 and constitution XXVIII: the connector-down suite, run on every build with the integration tests.
/// Each real pack that calls a system over HTTP meets the five faults through the runtime: a revoked credential, a
/// throttling system, a system that is down, a partial failure, and a duplicate delivery. The same faults for the
/// fake ticketing pack, webhooks, clock data, finance and Google Workspace are in their own test classes; this suite
/// checks that every HTTP pack maps them the same way.
/// </summary>
[Collection(WorldCollection.Name)]
[Trait("Suite", "ConnectorDown")]
public sealed class ConnectorDownTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";

    public enum Pack
    {
        CustomRest,
        Slack,
    }

    public enum Mode
    {
        Ok,
        Revoked,
        Throttled,
        Down,
        EverySecondDown,
    }

    private FakeRestApi Rest => world.Factory.Rest;

    /// <summary>One system under test: the connection, its action and input, and a fault switch on its fake host.</summary>
    private sealed class Target(TestTenant tenant, Guid connection, string host, string action, JsonElement input)
    {
        public TestTenant Tenant { get; } = tenant;
        public Guid Connection { get; } = connection;
        public string Host { get; } = host;
        public string Action { get; } = action;
        public JsonElement Input { get; } = input;
        public Mode Mode { get; set; }
        public int Answered;
    }

    private static string Host(string label) => $"{label}-{Guid.NewGuid():N}"[..20] + ".rest.test";

    private async Task<Target> SetupAsync(Pack pack)
    {
        var t = await world.CreateTenantAsync("down-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var host = Host(pack == Pack.Slack ? "slack" : "permits");
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "down-key"), "fictional-down-value");
        HttpResponseMessage created;
        Target target;
        if (pack == Pack.Slack)
        {
            created = await admin.PostAsJsonAsync(Packs, new { kind = "slack", displayName = "Slack", settings = new { baseUrl = $"https://{host}/api", botTokenRef = SecretReferences.For(t.Id, "down-key") } });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
            target = new Target(t, id, host, "lookup_user", JsonSerializer.SerializeToElement(new { email = "mona@acme.example" }));
        }
        else
        {
            created = await admin.PostAsJsonAsync(Packs, new
            {
                kind = "custom_rest",
                displayName = "Permits",
                settings = new { baseUrl = $"https://{host}/v1", auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "down-key") } },
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
            target = new Target(t, id, host, "create_permit", JsonSerializer.SerializeToElement(new { visitor = "Mona Saleh" }));
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{target.Connection}:enable", null)).StatusCode);
        Rest.On(host, s => Respond(pack, target, s));
        return target;
    }

    private static HttpResponseMessage Respond(Pack pack, Target target, FakeRestApi.Sent s)
    {
        var n = Interlocked.Increment(ref target.Answered);
        switch (target.Mode)
        {
            case Mode.Revoked:
                return pack == Pack.Slack ? Json(new { ok = false, error = "token_revoked" }) : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            case Mode.Throttled:
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.TryAddWithoutValidation("Retry-After", "30");
                return r;
            }
            case Mode.Down:
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            case Mode.EverySecondDown when n % 2 == 0:
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
        }
        return pack == Pack.Slack
            ? Json(new { ok = true, user = new { id = $"U{n:D9}", team_id = "T0000000001", deleted = false } })
            : FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { permit = new { id = $"PRM-{n}" } }));
    }

    private static HttpResponseMessage Json(object body) => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));

    private Task<ConnectorCallResult> InvokeAsync(Target x, string key) =>
        world.Factory.InScopeAsync(x.Tenant.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().InvokeAsync(new ConnectorCall(x.Connection, x.Action, x.Input, key, "down"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<ConnectorCallResult> EnqueueAsync(Target x, string key) =>
        world.Factory.InScopeAsync(x.Tenant.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().EnqueueAsync(new ConnectorCall(x.Connection, x.Action, x.Input, key, "down"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<int> RunDueAsync(Target x, DateTimeOffset now) =>
        world.Factory.InScopeAsync(x.Tenant.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ConnectorJobs>().RunDueAsync(now, 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });

    private Task<string?> StatusAsync(Target x, Guid run) =>
        world.Database.TenantScalarAsync<string>(x.Tenant.Id, "select status from integrations.run where id = @id", ("id", run));

    private int Sent(Target x) => Rest.SentTo(x.Host).Count;

    [Theory]
    [InlineData(Pack.CustomRest)]
    [InlineData(Pack.Slack)]
    public async Task A_revoked_credential_fails_at_once_without_retries_and_can_be_replayed_once_fixed(Pack pack)
    {
        var x = await SetupAsync(pack);
        x.Mode = Mode.Revoked;
        var failed = await InvokeAsync(x, "k-revoked");
        Assert.Equal((ConnectorOutcomes.Failed, ConnectorErrorClasses.CredentialRefused), (failed.Outcome, failed.ErrorClass));
        Assert.Equal(1, Sent(x));

        x.Mode = Mode.Ok;
        var replay = await x.Tenant["admin"].Client.PostAsJsonAsync($"/api/v1/integrations/connections/{x.Connection}/runs:replay", new { runIds = new[] { failed.RunId } });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await RunDueAsync(x, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("succeeded", await StatusAsync(x, failed.RunId!.Value));
        Assert.Equal(2, Sent(x));
    }

    [Theory]
    [InlineData(Pack.CustomRest)]
    [InlineData(Pack.Slack)]
    public async Task A_throttling_system_is_waited_for_as_it_asks_and_the_job_then_succeeds_once(Pack pack)
    {
        var x = await SetupAsync(pack);
        x.Mode = Mode.Throttled;
        var queued = await EnqueueAsync(x, "k-throttled");
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await RunDueAsync(x, now.AddSeconds(1)));
        Assert.Equal("retrying", await StatusAsync(x, queued.RunId!.Value));
        // Retry-After asked for 30 seconds: nothing is sent before then.
        Assert.Equal(0, await RunDueAsync(x, now.AddSeconds(10)));
        Assert.Equal(1, Sent(x));

        x.Mode = Mode.Ok;
        Assert.Equal(1, await RunDueAsync(x, now.AddMinutes(3)));
        Assert.Equal("succeeded", await StatusAsync(x, queued.RunId!.Value));
        Assert.Equal(2, Sent(x));
    }

    [Theory]
    [InlineData(Pack.CustomRest)]
    [InlineData(Pack.Slack)]
    public async Task A_system_that_is_down_is_retried_then_left_as_a_dead_letter_and_the_circuit_opens(Pack pack)
    {
        var x = await SetupAsync(pack);
        x.Mode = Mode.Down;
        var failed = await InvokeAsync(x, "k-down");
        Assert.Equal((ConnectorOutcomes.Failed, ConnectorErrorClasses.ServerError), (failed.Outcome, failed.ErrorClass));
        Assert.Equal(3, Sent(x));
        Assert.Equal("failed", await StatusAsync(x, failed.RunId!.Value));

        // Repeated failures open the circuit: later calls stop before reaching the system.
        for (var i = 0; i < 20 && (await InvokeAsync(x, $"k-down-{i}")).ErrorClass != ConnectorErrorClasses.CircuitOpen; i++)
        {
        }
        var before = Sent(x);
        var blocked = await InvokeAsync(x, "k-after");
        Assert.Equal(ConnectorErrorClasses.CircuitOpen, blocked.ErrorClass);
        Assert.Equal(before, Sent(x));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(ConnectorUnavailable), x.Tenant.Id));
    }

    [Theory]
    [InlineData(Pack.CustomRest)]
    [InlineData(Pack.Slack)]
    public async Task In_a_partial_failure_each_call_finishes_on_its_own_and_nothing_succeeds_twice(Pack pack)
    {
        var x = await SetupAsync(pack);
        x.Mode = Mode.EverySecondDown;
        var runs = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            runs.Add((await EnqueueAsync(x, $"k-part-{i}")).RunId!.Value);
        }
        var now = DateTimeOffset.UtcNow;
        await RunDueAsync(x, now.AddSeconds(1));
        var statuses = new List<string?>();
        foreach (var r in runs)
        {
            statuses.Add(await StatusAsync(x, r));
        }
        Assert.Equal(3, statuses.Count(s => s == "succeeded"));
        Assert.Equal(3, statuses.Count(s => s == "retrying"));

        x.Mode = Mode.Ok;
        await RunDueAsync(x, now.AddMinutes(3));
        foreach (var r in runs)
        {
            Assert.Equal("succeeded", await StatusAsync(x, r));
        }
        // Six calls succeeded once each: the three failures were sent again, the successes never.
        Assert.Equal(9, Sent(x));
    }

    [Theory]
    [InlineData(Pack.CustomRest)]
    [InlineData(Pack.Slack)]
    public async Task A_duplicate_request_with_the_same_key_is_answered_from_history_and_reaches_the_system_once(Pack pack)
    {
        var x = await SetupAsync(pack);
        var first = await InvokeAsync(x, "k-dup");
        var second = await InvokeAsync(x, "k-dup");
        Assert.Equal(ConnectorOutcomes.Succeeded, first.Outcome);
        Assert.True(second.Repeated);
        Assert.Equal((first.RunId, first.ExternalReference), (second.RunId, second.ExternalReference));
        var queued = await EnqueueAsync(x, "k-dup-job");
        var again = await EnqueueAsync(x, "k-dup-job");
        Assert.Equal(queued.RunId, again.RunId);
        await RunDueAsync(x, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(2, Sent(x));
        if (pack == Pack.CustomRest)
        {
            Assert.All(Rest.SentTo(x.Host), s => Assert.True(s.Headers.ContainsKey("Idempotency-Key")));
        }
    }
}
