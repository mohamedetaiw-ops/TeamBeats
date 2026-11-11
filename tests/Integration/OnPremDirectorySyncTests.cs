using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T015 with spec 003 US6 (R2 for on-premises AD): directory sync from an Active Directory connection read
/// by the customer's sync agent. A due run asks the agent for a read and waits; the finished read creates, updates and
/// disables users keyed by the AD objectGUID and keeps selected groups; the next read starts from the cursors the agent
/// reported; a failed read changes nothing; the tenant boundary. The agent is played by the test with a key it makes.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OnPremDirectorySyncTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";

    private sealed record Setup(TestTenant Tenant, Guid Connection, Guid AgentId, ECDsa Key)
    {
        public string Email(string name) => $"{name}@{Tenant.Key}.example";
    }

    private static string Guid36() => Guid.NewGuid().ToString("D");

    private async Task<Setup> SetupAsync(object? settings = null)
    {
        var t = await world.CreateTenantAsync("ad-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Packs, new { kind = "onprem_ad", displayName = "Head office AD", settings = new { host = "dc01.corp.example", baseDn = "DC=corp,DC=example" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{connection}:enable", null)).StatusCode);

        var code = (await (await admin.PostAsync($"{Packs}/{connection}/agents/enrolment-code", null)).ReadAsync<JsonElement>()).GetProperty("code").GetString();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrolled = await world.Factory.TenantClient(t.Key).PostAsJsonAsync("/api/v1/agent/enrol",
            new { code, name = "AGENT-01", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        var agentId = (await enrolled.ReadAsync<JsonElement>()).GetProperty("agentId").GetGuid();

        var saved = await admin.PutAsJsonAsync("/api/v1/tenant/directory",
            settings ?? new { connectionId = connection, userScope = new { mode = "all", groups = Array.Empty<string>() }, deletionThresholdPct = 100 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("adds", (await saved.ReadAsync<JsonElement>()).GetProperty("source").GetString());
        return new Setup(t, connection, agentId, key);
    }

    private Task<HttpResponseMessage> AgentAsync(Setup s, string path, object? payload = null)
    {
        var body = payload is null ? "" : JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(AgentProtocol.AgentHeader, s.AgentId.ToString());
        request.Headers.TryAddWithoutValidation(AgentProtocol.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(AgentProtocol.SignatureHeader, AgentProtocol.Sign(s.Key, "POST", path, ts, Encoding.UTF8.GetBytes(body)));
        return world.Factory.TenantClient(s.Tenant.Key).SendAsync(request);
    }

    private Task<SyncRun?> RunAsync(Setup s) =>
        world.Factory.InScopeAsync(s.Tenant.Id, async sp =>
        {
            var run = await sp.GetRequiredService<DirectorySyncService>().RunAsync(s.Connection, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    private Task<SyncRun?> ApplyAsync(TestTenant t, AgentJobFinished finished) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var run = await sp.GetRequiredService<DirectorySyncService>().ApplyAgentReadAsync(finished, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    /// <summary>The agent pulls the job, sends the records and completes it; then Identity applies the read.</summary>
    private async Task<(JsonElement Job, SyncRun? Run)> ReadAsync(Setup s, object batch, bool ok = true, string? errorClass = null, string? usersCursor = "usn-100",
        string? groupsCursor = "usn-200")
    {
        var pulled = await AgentAsync(s, "/api/v1/agent/jobs:pull");
        Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
        var job = await pulled.ReadAsync<JsonElement>();
        var jobId = job.GetProperty("id").GetGuid();
        if (ok)
        {
            Assert.Equal(HttpStatusCode.OK, (await AgentAsync(s, $"/api/v1/agent/jobs/{jobId}/records", batch)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await AgentAsync(s, $"/api/v1/agent/jobs/{jobId}:complete", new { ok, errorClass, usersCursor, groupsCursor })).StatusCode);
        var run = await ApplyAsync(s.Tenant, new AgentJobFinished(jobId, s.Connection, "directory_sync", ok, errorClass, 0, 0));
        return (job, run);
    }

    private static object User(string id, string name, string? email, bool enabled = true, bool removed = false) =>
        new { externalId = id, removed, accountEnabled = enabled, displayName = name, email, userPrincipalName = email };

    private Task<Guid?> UserIdAsync(Setup s, string objectGuid) =>
        world.Database.TenantScalarAsync<Guid?>(s.Tenant.Id, "select user_id from identity.directory_user_link where directory_id = @d", ("d", objectGuid));

    private Task<string?> MembershipStatusAsync(Setup s, Guid userId) =>
        world.Database.TenantScalarAsync<string>(s.Tenant.Id, "select status from tenancy.tenant_membership where user_id = @u", ("u", userId));

    [Fact]
    public async Task An_agent_read_creates_users_by_object_guid_and_the_next_read_starts_from_its_cursors()
    {
        var s = await SetupAsync();
        var (alice, bob, carol) = (Guid36(), Guid36(), Guid36());

        var waiting = await RunAsync(s);
        Assert.Equal("running", waiting!.Outcome);
        Assert.Null(await RunAsync(s));
        var (job, run) = await ReadAsync(s, new
        {
            users = new[] { User(alice, "Alice Hassan", s.Email("alice")), User(bob, "Bob Saleh", s.Email("bob")), User(carol, "Carol Off", s.Email("carol"), enabled: false) },
        });
        Assert.Equal(JsonValueKind.Null, job.GetProperty("usersCursor").ValueKind);
        Assert.Equal(waiting.Id, run!.Id);
        Assert.Equal("succeeded", run.Outcome);
        Assert.Equal(2, run.Created);

        var aliceId = (await UserIdAsync(s, alice))!.Value;
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "select count(*) from identity.external_identity where user_id = @u and issuer = @i and subject = @d and idp_config_id is null",
            ("u", aliceId), ("i", DirectorySyncService.OnPremIssuer(s.Connection)), ("d", alice)));
        Assert.Equal("directory", await world.Database.TenantScalarAsync<string>(s.Tenant.Id, "select created_via from identity.user_account where id = @u", ("u", aliceId)));
        Assert.Null(await UserIdAsync(s, carol));

        // The next due run reads from where the agent stopped; Bob is disabled in AD and loses access.
        await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "with u as (update identity.directory_connection set next_run_at = now() returning 1) select count(*) from u");
        Assert.Equal("running", (await RunAsync(s))!.Outcome);
        var (next, second) = await ReadAsync(s, new { users = new[] { User(bob, "Bob Saleh", s.Email("bob"), enabled: false) } });
        Assert.Equal("usn-100", next.GetProperty("usersCursor").GetString());
        Assert.Equal("usn-200", next.GetProperty("groupsCursor").GetString());
        Assert.Equal(1, second!.Disabled);
        Assert.Equal("disabled", await MembershipStatusAsync(s, (await UserIdAsync(s, bob))!.Value));
        Assert.Equal("active", await MembershipStatusAsync(s, aliceId));
    }

    [Fact]
    public async Task Selected_ad_groups_become_directory_groups_and_a_read_started_by_an_administrator_is_applied()
    {
        var finance = Guid36();
        var s = await SetupAsync();
        var admin = s.Tenant["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/tenant/directory",
            new { connectionId = s.Connection, syncGroups = new[] { finance }, deletionThresholdPct = 100 })).StatusCode);
        var dana = Guid36();

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"{Packs}/{s.Connection}/agent-jobs", null)).StatusCode);
        var (_, run) = await ReadAsync(s, new
        {
            users = new[] { User(dana, "Dana Finance", s.Email("dana")) },
            groups = new[] { new { externalId = finance, removed = false, displayName = "Finance", membersAdded = new[] { dana }, membersRemoved = Array.Empty<string>() } },
        });
        Assert.Equal("succeeded", run!.Outcome);
        Assert.Equal(1, run.Groups);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "select count(*) from identity.directory_group_link where connection_id = @c and directory_id = @d", ("c", s.Connection), ("d", finance)));

        // A group sent with its whole membership replaces the members known before.
        var omar = Guid36();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync($"{Packs}/{s.Connection}/agent-jobs", null)).StatusCode);
        await ReadAsync(s, new
        {
            users = new[] { User(omar, "Omar Finance", s.Email("omar")) },
            groups = new[] { new { externalId = finance, removed = false, displayName = "Finance", membersAdded = new[] { omar }, membersRemoved = Array.Empty<string>(), membersComplete = true } },
        });
        Assert.Equal(omar, await world.Database.TenantScalarAsync<string>(s.Tenant.Id,
            "select array_to_string(members, ',') from identity.directory_group where connection_id = @c and directory_id = @d", ("c", s.Connection), ("d", finance)));

        var runs = await (await admin.GetAsync("/api/v1/tenant/directory")).ReadAsync<JsonElement>();
        Assert.Equal("succeeded", runs.GetProperty("runs").EnumerateArray().First().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_failed_agent_read_changes_nothing_and_counts_as_a_failure()
    {
        var s = await SetupAsync();
        var erin = Guid36();
        await RunAsync(s);
        await ReadAsync(s, new { users = new[] { User(erin, "Erin", s.Email("erin")) } });

        await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "with u as (update identity.directory_connection set next_run_at = now() returning 1) select count(*) from u");
        await RunAsync(s);
        var (_, failed) = await ReadAsync(s, new { }, ok: false, errorClass: "credential_refused");
        Assert.Equal("failed", failed!.Outcome);
        Assert.Equal("credential_refused", failed.Reason);
        Assert.Equal("active", await MembershipStatusAsync(s, (await UserIdAsync(s, erin))!.Value));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(s.Tenant.Id,
            "select consecutive_failures from identity.directory_connection where connection_id = @c", ("c", s.Connection)));
    }

    [Fact]
    public async Task Settings_take_only_a_usable_ad_connection_of_the_same_tenant_and_reads_stay_in_their_tenant()
    {
        var a = await SetupAsync();
        var b = await world.CreateTenantAsync("ad-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var refused = await b["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { connectionId = a.Connection });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // A read of tenant A is not applied in tenant B, and B never sees A's records.
        await RunAsync(a);
        var pulled = await (await AgentAsync(a, "/api/v1/agent/jobs:pull")).ReadAsync<JsonElement>();
        var jobId = pulled.GetProperty("id").GetGuid();
        await AgentAsync(a, $"/api/v1/agent/jobs/{jobId}/records", new { users = new[] { User(Guid36(), "Fadi", a.Email("fadi")) } });
        await AgentAsync(a, $"/api/v1/agent/jobs/{jobId}:complete", new { ok = true });
        Assert.Null(await ApplyAsync(b, new AgentJobFinished(jobId, a.Connection, "directory_sync", true, null, 1, 0)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(b.Id, "select count(*) from identity.directory_user"));
        Assert.Equal(1, (await ApplyAsync(a.Tenant, new AgentJobFinished(jobId, a.Connection, "directory_sync", true, null, 1, 0)))!.Created);
        Assert.Null(await ApplyAsync(a.Tenant, new AgentJobFinished(jobId, a.Connection, "directory_sync", true, null, 1, 0)));

        // A turned off connection cannot be chosen, and its due run fails without asking the agent.
        Assert.Equal(HttpStatusCode.OK, (await a.Tenant["admin"].Client.PostAsync($"{Packs}/{a.Connection}:disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/directory", new { connectionId = a.Connection })).StatusCode);
        await world.Database.TenantScalarAsync<long>(a.Tenant.Id,
            "with u as (update identity.directory_connection set next_run_at = now() returning 1) select count(*) from u");
        var run = await RunAsync(a);
        Assert.Equal(("failed", "connection_unusable"), (run!.Outcome, run.Reason));
    }

    /// <summary>Spec 017 section 15: 20,000 directory users through the on-premises agent in under 30 minutes (spec 002 T027).</summary>
    [Fact]
    public async Task Twenty_thousand_users_sent_by_the_agent_are_applied_well_within_thirty_minutes()
    {
        const int total = 20_000;
        var s = await SetupAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("running", (await RunAsync(s))!.Outcome);
        var pulled = await AgentAsync(s, "/api/v1/agent/jobs:pull");
        var jobId = (await pulled.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        for (var batch = 0; batch < total / SyncAgentServiceLimits.PerBatch; batch++)
        {
            var users = Enumerable.Range(batch * SyncAgentServiceLimits.PerBatch, SyncAgentServiceLimits.PerBatch)
                .Select(i => User(new Guid(i, 0, 0, new byte[8]).ToString("D"), $"Person {i}", s.Email($"p{i}")));
            Assert.Equal(HttpStatusCode.OK, (await AgentAsync(s, $"/api/v1/agent/jobs/{jobId}/records", new { users })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await AgentAsync(s, $"/api/v1/agent/jobs/{jobId}:complete", new { ok = true, usersCursor = "usn-1", groupsCursor = "usn-2" })).StatusCode);
        var run = await ApplyAsync(s.Tenant, new AgentJobFinished(jobId, s.Connection, "directory_sync", true, null, total, 0));
        watch.Stop();

        Assert.Equal("succeeded", run!.Outcome);
        Assert.Equal(total, run.Created);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(10), $"{total} users took {watch.Elapsed.TotalSeconds:F0} s");
        // The sync leaves an event per user; other tests dispatch the shared outbox in fixed batches, so drop them.
        await world.Database.OwnerExecAsync("delete from messaging.outbox where tenant_id = @t and dispatched_at is null", ("t", s.Tenant.Id));
    }

    private static class SyncAgentServiceLimits
    {
        public const int PerBatch = TeamBeats.Modules.Integrations.SyncAgents.SyncAgentService.MaxRecordsPerKind;
    }
}
