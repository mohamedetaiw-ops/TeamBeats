using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T003: the connector runtime. Retry with backoff, idempotency, business and fatal errors, outcome
/// unknown, rate limits, the circuit breaker, durable jobs, run history, dead letters and replay, the host allowlist,
/// and the tenant boundary, against a fake ticketing system with scripted faults.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ConnectorRuntimeTests(TenantWorld world)
{
    private static readonly JsonElement Input = JsonSerializer.SerializeToElement(new { summary = "Laptop for Mona", category = "hardware" });

    private FakeTicketing Ticketing => world.Factory.Ticketing;

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("cr-" + Guid.NewGuid().ToString("N")[..8], package);

    private async Task<Guid> ConnectionAsync(TestTenant t, bool enabled = true, int perMinute = 600)
    {
        var id = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @id, 'test_ticketing', 'Service desk', 'healthy', @enabled, @rate)
            """, ("t", t.Id), ("id", id), ("enabled", enabled), ("rate", perMinute));
        return id;
    }

    private Task<ConnectorCallResult> InvokeAsync(TestTenant t, Guid connection, string action, string key) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().InvokeAsync(new ConnectorCall(connection, action, Input, key, "corr-1"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<ConnectorCallResult> EnqueueAsync(TestTenant t, Guid connection, string action, string key) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<IConnectorRuntime>().EnqueueAsync(new ConnectorCall(connection, action, Input, key, "corr-2"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ConnectorJobs>().RunDueAsync(now, 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });

    private Task<string?> RunValueAsync(TestTenant t, Guid run, string column) =>
        world.Database.TenantScalarAsync<string>(t.Id, $"select {column}::text from integrations.run where id = @id", ("id", run));

    [Fact]
    public async Task A_call_succeeds_once_and_the_same_key_answers_again_from_history_without_calling_the_system()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);

        var first = await InvokeAsync(t, c, "create_incident", "request-1:step-4");
        Assert.Equal(ConnectorOutcomes.Succeeded, first.Outcome);
        Assert.StartsWith("INC", first.ExternalReference);
        Assert.Equal(first.ExternalReference, first.Output!.Value.GetProperty("number").GetString());
        Assert.False(first.Repeated);

        var again = await InvokeAsync(t, c, "create_incident", "request-1:step-4");
        Assert.Equal(ConnectorOutcomes.Succeeded, again.Outcome);
        Assert.True(again.Repeated);
        Assert.Equal(first.RunId, again.RunId);
        Assert.Equal(first.ExternalReference, again.ExternalReference);
        Assert.Null(again.Output);

        var calls = Ticketing.CallsFor(c);
        Assert.Single(calls);
        Assert.Equal("request-1:step-4", calls[0].IdempotencyKey);
        Assert.Contains("Laptop for Mona", calls[0].Input);
        // Run history keeps no payload once the run succeeded.
        Assert.Null(await RunValueAsync(t, first.RunId!.Value, "input"));
        Assert.Equal("1", await RunValueAsync(t, first.RunId!.Value, "attempts"));
    }

    [Fact]
    public async Task Technical_errors_are_retried_with_backoff_within_ten_seconds_and_then_fail_as_a_dead_letter()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.ServerError), PackCallResult.Technical(ConnectorErrorClasses.ServerError),
            PackCallResult.Technical(ConnectorErrorClasses.ServerError));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await InvokeAsync(t, c, "create_incident", "k-retry");
        clock.Stop();

        Assert.Equal(ConnectorOutcomes.Failed, result.Outcome);
        Assert.Equal(ConnectorErrorClasses.ServerError, result.ErrorClass);
        Assert.Equal([1, 2, 3], Ticketing.CallsFor(c).Select(x => x.Attempt));
        Assert.All(Ticketing.CallsFor(c), x => Assert.Equal("k-retry", x.IdempotencyKey));
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        Assert.Equal("failed", await RunValueAsync(t, result.RunId!.Value, "status"));
        // A dead letter keeps its input so it can be replayed.
        Assert.NotNull(await RunValueAsync(t, result.RunId!.Value, "input"));
    }

    [Fact]
    public async Task A_timeout_after_sending_stops_a_plain_write_as_outcome_unknown_but_an_idempotent_write_is_retried_with_the_same_key()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));

        var plain = await InvokeAsync(t, c, "create_incident", "k-unknown");
        Assert.Equal(ConnectorOutcomes.Unknown, plain.Outcome);
        Assert.Single(Ticketing.CallsFor(c));

        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));
        var keyed = await InvokeAsync(t, c, "create_incident_keyed", "k-keyed");
        Assert.Equal(ConnectorOutcomes.Succeeded, keyed.Outcome);
        var keyedCalls = Ticketing.CallsFor(c).Where(x => x.Action == "create_incident_keyed").ToList();
        Assert.Equal(2, keyedCalls.Count);
        Assert.All(keyedCalls, x => Assert.Equal("k-keyed", x.IdempotencyKey));
    }

    [Fact]
    public async Task An_attempt_that_never_answers_is_cut_off_by_the_budget_and_counted_as_not_responding()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, FakeTicketing.Hang, FakeTicketing.Hang, FakeTicketing.Hang);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await InvokeAsync(t, c, "get_status", "k-hang");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), $"took {clock.Elapsed}");
        Assert.Equal(ConnectorOutcomes.Failed, result.Outcome);
        Assert.Equal(ConnectorErrorClasses.NotResponding, result.ErrorClass);
    }

    [Fact]
    public async Task Business_errors_and_refused_credentials_are_not_retried()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Business("invalid_category"), PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused));

        var business = await InvokeAsync(t, c, "create_incident", "k-business");
        Assert.Equal(ConnectorOutcomes.BusinessError, business.Outcome);
        Assert.Equal("invalid_category", business.ErrorCode);

        var refused = await InvokeAsync(t, c, "create_incident", "k-credential");
        Assert.Equal(ConnectorOutcomes.Failed, refused.Outcome);
        Assert.Equal(ConnectorErrorClasses.CredentialRefused, refused.ErrorClass);
        Assert.Equal(2, Ticketing.CallsFor(c).Count);

        // A business error is final for its key.
        var repeat = await InvokeAsync(t, c, "create_incident", "k-business");
        Assert.True(repeat.Repeated);
        Assert.Equal("invalid_category", repeat.ErrorCode);
        Assert.Equal(2, Ticketing.CallsFor(c).Count);
    }

    [Fact]
    public async Task Disabled_connections_unknown_actions_and_packages_without_connectors_never_reach_the_system()
    {
        var t = await TenantAsync();
        var disabled = await ConnectionAsync(t, enabled: false);
        var off = await InvokeAsync(t, disabled, "create_incident", "k-off");
        Assert.Equal(ConnectorOutcomes.Refused, off.Outcome);
        Assert.Equal(ConnectorErrorClasses.ConnectionDisabled, off.ErrorClass);

        var unknown = await InvokeAsync(t, disabled, "delete_everything", "k-unknown-action");
        Assert.Equal(ConnectorErrorClasses.UnknownAction, unknown.ErrorClass);

        var missing = await InvokeAsync(t, Guid.NewGuid(), "create_incident", "k-missing");
        Assert.Equal(ConnectorErrorClasses.ConnectionMissing, missing.ErrorClass);
        Assert.Null(missing.RunId);

        var workplace = await TenantAsync("workplace");
        var w = await ConnectionAsync(workplace);
        var notInPackage = await InvokeAsync(workplace, w, "create_incident", "k-package");
        Assert.Equal(ConnectorErrorClasses.NotInPackage, notInPackage.ErrorClass);

        Assert.Empty(Ticketing.CallsFor(disabled));
        Assert.Empty(Ticketing.CallsFor(w));
    }

    [Fact]
    public async Task A_connection_of_another_tenant_is_missing_and_its_runs_are_never_visible()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var c = await ConnectionAsync(a);
        var run = await InvokeAsync(a, c, "create_incident", "k-tenant");

        var fromB = await InvokeAsync(b, c, "create_incident", "k-tenant");
        Assert.Equal(ConnectorErrorClasses.ConnectionMissing, fromB.ErrorClass);
        Assert.Single(Ticketing.CallsFor(c));

        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs")).StatusCode);
        var replay = await b["admin"].Client.PostAsJsonAsync($"/api/v1/integrations/connections/{c}/runs:replay", new { runIds = new[] { run.RunId } });
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(b.Id, "select count(*) from integrations.run"));
    }

    [Fact]
    public async Task Bursts_beyond_the_connection_rate_limit_wait_briefly_and_then_fail_as_rate_limited()
    {
        var t = await TenantAsync();
        // Six calls a minute: one token, a new one every ten seconds.
        var c = await ConnectionAsync(t, perMinute: 6);

        Assert.Equal(ConnectorOutcomes.Succeeded, (await InvokeAsync(t, c, "get_status", "k-rate-1")).Outcome);
        var second = await InvokeAsync(t, c, "get_status", "k-rate-2");
        Assert.Equal(ConnectorOutcomes.Failed, second.Outcome);
        Assert.Equal(ConnectorErrorClasses.RateLimited, second.ErrorClass);
        Assert.Single(Ticketing.CallsFor(c));
    }

    [Fact]
    public async Task The_circuit_opens_after_half_of_twenty_calls_fail_and_a_trial_call_after_two_minutes_closes_it()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        // Ten successes, then ten refusals: the twentieth call brings the failure ratio to one half.
        Ticketing.Script(c, Enumerable.Repeat(PackCallResult.Ok(), 10).Concat(Enumerable.Repeat(PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused), 10)).ToArray());
        for (var i = 0; i < 20; i++)
        {
            await InvokeAsync(t, c, "get_status", $"k-circuit-{i}");
        }
        Assert.Equal(20, Ticketing.CallsFor(c).Count);
        Assert.Equal("degraded", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.connection where id = @id", ("id", c)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from messaging.outbox where message_type = 'ConnectorUnavailable' and payload->>'connectionId' = @c", ("c", c.ToString())));

        var open = await InvokeAsync(t, c, "get_status", "k-circuit-open");
        Assert.Equal(ConnectorErrorClasses.CircuitOpen, open.ErrorClass);
        Assert.Equal(20, Ticketing.CallsFor(c).Count);

        await world.Database.OwnerExecInTenantAsync(t.Id,
            "update integrations.connection set circuit_opened_at = now() - interval '3 minutes' where id = @id", ("id", c));
        var trial = await InvokeAsync(t, c, "get_status", "k-circuit-trial");
        Assert.Equal(ConnectorOutcomes.Succeeded, trial.Outcome);
        Assert.Equal("healthy", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.connection where id = @id", ("id", c)));
        Assert.True(await world.Database.TenantScalarAsync<bool>(t.Id, "select circuit_opened_at is null from integrations.connection where id = @id", ("id", c)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from messaging.outbox where message_type = 'ConnectorRestored' and payload->>'connectionId' = @c", ("c", c.ToString())));
    }

    [Fact]
    public async Task A_job_retries_later_with_backoff_finishes_and_announces_its_outcome()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.Throttled, retryAfter: TimeSpan.FromSeconds(30)));

        var queued = await EnqueueAsync(t, c, "create_incident", "k-job");
        Assert.Equal(ConnectorOutcomes.Queued, queued.Outcome);
        var again = await EnqueueAsync(t, c, "create_incident", "k-job");
        Assert.Equal(ConnectorOutcomes.InProgress, again.Outcome);
        Assert.Equal(queued.RunId, again.RunId);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, await RunDueAsync(t, now.AddSeconds(1)));
        Assert.Equal("retrying", await RunValueAsync(t, queued.RunId!.Value, "status"));
        // Not due again until the backoff (about a minute) has passed.
        Assert.Equal(0, await RunDueAsync(t, now.AddSeconds(5)));
        Assert.Equal(1, await RunDueAsync(t, now.AddMinutes(2)));

        Assert.Equal("succeeded", await RunValueAsync(t, queued.RunId!.Value, "status"));
        Assert.Equal("2", await RunValueAsync(t, queued.RunId!.Value, "attempts"));
        var reference = await RunValueAsync(t, queued.RunId!.Value, "external_reference");
        Assert.StartsWith("INC", reference);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            """
            select count(*) from messaging.outbox where message_type = 'ConnectorActionCompleted' and payload->>'runId' = @r
              and payload->>'outcome' = 'succeeded' and payload->>'externalReference' = @ref and payload->>'correlationId' = 'corr-2'
            """, ("r", queued.RunId!.Value.ToString()), ("ref", reference!)));
    }

    [Fact]
    public async Task A_job_on_a_disabled_connection_waits_and_runs_once_the_connection_is_enabled()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t, enabled: false);
        var queued = await EnqueueAsync(t, c, "create_incident", "k-paused");
        Assert.Equal(ConnectorOutcomes.Queued, queued.Outcome);
        Assert.Equal(ConnectorErrorClasses.ConnectionDisabled, queued.ErrorClass);

        var now = DateTimeOffset.UtcNow;
        await RunDueAsync(t, now.AddSeconds(1));
        Assert.Equal("queued", await RunValueAsync(t, queued.RunId!.Value, "status"));
        Assert.Equal(ConnectorErrorClasses.ConnectionDisabled, await RunValueAsync(t, queued.RunId!.Value, "error_class"));
        Assert.Empty(Ticketing.CallsFor(c));

        await world.Database.OwnerExecInTenantAsync(t.Id, "update integrations.connection set enabled = true where id = @id", ("id", c));
        await RunDueAsync(t, now.AddMinutes(2));
        Assert.Equal("succeeded", await RunValueAsync(t, queued.RunId!.Value, "status"));
        Assert.Single(Ticketing.CallsFor(c));
    }

    [Fact]
    public async Task An_operator_sees_history_without_payloads_and_replays_a_dead_letter_with_its_original_key()
    {
        var t = await TenantAsync();
        await world.SeedUserAsync(t, "operator", "employee");
        await world.SeedUserAsync(t, "employee", "employee");
        await world.AssignAsync(t, "operator", "integration_administrator", "tenant", null);
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused));
        var failed = await InvokeAsync(t, c, "create_incident", "k-replay");
        var ok = await InvokeAsync(t, c, "get_status", "k-ok");

        var list = await t["operator"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await list.ReadAsync<JsonElement>();
        var raw = page.GetRawText();
        Assert.DoesNotContain("Laptop for Mona", raw);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());

        var dead = await (await t["operator"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs?status=dead_letter")).ReadAsync<JsonElement>();
        var only = Assert.Single(dead.GetProperty("items").EnumerateArray());
        Assert.Equal(failed.RunId, only.GetProperty("id").GetGuid());
        Assert.True(only.GetProperty("replayable").GetBoolean());
        Assert.Equal(ConnectorErrorClasses.CredentialRefused, only.GetProperty("errorClass").GetString());

        var paged = await (await t["operator"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs?limit=1")).ReadAsync<JsonElement>();
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        var cursor = paged.GetProperty("nextCursor").GetString();
        var next = await (await t["operator"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs?limit=1&cursor={cursor}")).ReadAsync<JsonElement>();
        Assert.NotEqual(paged.GetProperty("items")[0].GetProperty("id").GetGuid(), next.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextCursor").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["operator"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs?status=lost")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PostAsJsonAsync($"/api/v1/integrations/connections/{c}/runs:replay",
            new { runIds = new[] { failed.RunId } })).StatusCode);

        var replay = await t["operator"].Client.PostAsJsonAsync($"/api/v1/integrations/connections/{c}/runs:replay",
            new { runIds = new[] { failed.RunId, ok.RunId } });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = await replay.ReadAsync<JsonElement>();
        Assert.Equal(failed.RunId, Assert.Single(result.GetProperty("replayed").EnumerateArray()).GetGuid());
        Assert.Equal(ok.RunId, Assert.Single(result.GetProperty("skipped").EnumerateArray()).GetGuid());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @c", ("a", AuditActions.ConnectorReplay), ("c", c.ToString())));

        await RunDueAsync(t, DateTimeOffset.UtcNow.AddSeconds(1));
        var calls = Ticketing.CallsFor(c).Where(x => x.Action == "create_incident").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, x => Assert.Equal("k-replay", x.IdempotencyKey));

        var after = await (await t["operator"].Client.GetAsync($"/api/v1/integrations/connections/{c}/runs?action=create_incident")).ReadAsync<JsonElement>();
        var replayed = Assert.Single(after.GetProperty("items").EnumerateArray());
        Assert.Equal("succeeded", replayed.GetProperty("status").GetString());
        Assert.Equal(ConnectorErrorClasses.CredentialRefused, replayed.GetProperty("previousErrorClass").GetString());
        Assert.Equal(1, replayed.GetProperty("replays").GetInt32());
        Assert.False(replayed.GetProperty("replayable").GetBoolean());
    }

    [Fact]
    public async Task Dead_letters_stop_being_replayable_after_14_days_and_history_is_purged_after_90()
    {
        var t = await TenantAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused));
        var failed = await InvokeAsync(t, c, "create_incident", "k-old");
        var recent = await InvokeAsync(t, c, "get_status", "k-recent");
        await world.Database.OwnerExecInTenantAsync(t.Id,
            "update integrations.run set finished_at = now() - interval '15 days' where id = @id", ("id", failed.RunId!.Value));

        var replay = await t["admin"].Client.PostAsJsonAsync($"/api/v1/integrations/connections/{c}/runs:replay", new { runIds = new[] { failed.RunId } });
        Assert.Empty((await replay.ReadAsync<JsonElement>()).GetProperty("replayed").EnumerateArray());

        await world.Database.OwnerExecInTenantAsync(t.Id,
            "update integrations.run set created_at = now() - interval '91 days' where id = @id", ("id", recent.RunId!.Value));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ConnectorJobs>().PurgeAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Null(await RunValueAsync(t, failed.RunId!.Value, "input"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from integrations.run where id = @id", ("id", recent.RunId!.Value)));
    }

    [Fact]
    public async Task The_host_allowlist_takes_host_names_and_wildcards_only_is_audited_and_stays_in_its_tenant()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        var admin = t["admin"].Client;

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host = "API.Facilities.example.com." })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host = "*.service-now.example" })).StatusCode);
        foreach (var bad in new[] { "10.0.0.5", "localhost", "*.com.", "http://x.example", "a..example", "" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host = bad })).StatusCode);
        }
        var list = await (await admin.GetAsync("/api/v1/integrations/allowlist")).ReadAsync<JsonElement>();
        Assert.Equal(["*.service-now.example", "api.facilities.example.com"], list.EnumerateArray().Select(e => e.GetProperty("host").GetString()));

        var allowed = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var a = sp.GetRequiredService<HostAllowlist>();
            return (Exact: await a.IsAllowedAsync(new Uri("https://api.facilities.example.com/permits"), default),
                Wild: await a.IsAllowedAsync(new Uri("https://acme.service-now.example/api"), default),
                Apex: await a.IsAllowedAsync(new Uri("https://service-now.example/api"), default),
                Plain: await a.IsAllowedAsync(new Uri("http://api.facilities.example.com/permits"), default),
                Other: await a.IsAllowedAsync(new Uri("https://evil.example/"), default));
        });
        Assert.True(allowed.Exact);
        Assert.True(allowed.Wild);
        Assert.False(allowed.Apex);
        Assert.False(allowed.Plain);
        Assert.False(allowed.Other);

        Assert.Empty((await (await other["admin"].Client.GetAsync("/api/v1/integrations/allowlist")).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.DeleteAsync("/api/v1/integrations/allowlist/api.facilities.example.com")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/integrations/allowlist/api.facilities.example.com")).StatusCode);
        Assert.Equal(3, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.HostAllowlistChanged)));

        await world.SeedUserAsync(t, "employee", "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync("/api/v1/integrations/allowlist")).StatusCode);
    }

    [Fact]
    public async Task The_connector_http_handler_refuses_loopback_and_private_addresses_before_connecting()
    {
        using var client = new HttpClient(SafeHttp.CreateHandler(allowPrivate: false)) { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var url in new[] { "http://127.0.0.1:9/", "http://localhost:9/", "http://[::1]:9/", "http://169.254.169.254/latest/meta-data/", "http://10.1.2.3:9/" })
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
            Assert.IsType<BlockedAddressException>(ex.InnerException);
        }

        // An on-premises install may allow them; the connection itself then fails normally (nothing listens on port 9).
        using var onPrem = new HttpClient(SafeHttp.CreateHandler(allowPrivate: true)) { Timeout = TimeSpan.FromSeconds(5) };
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => onPrem.GetAsync("http://127.0.0.1:9/"));
        Assert.IsNotType<BlockedAddressException>(refused.InnerException);
    }
}
