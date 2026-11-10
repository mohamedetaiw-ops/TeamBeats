using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.SyncAgents;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T014 and T015: the on-premises sync agent. It enrols once with a one-time code and the public half of a
/// key it made, which binds it to one tenant and one connection; every later request is signed and checked against that
/// key, so revocation refuses the very next request. One agent at a time holds a connection's job, a lease that runs out
/// puts the job back, and a connection whose agents go quiet for 5 minutes is marked degraded. The package and the
/// tenant boundary. Keys are made in the test; nothing real is used.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SyncAgentTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";

    private static readonly object Settings = new { host = "dc01.corp.example", baseDn = "DC=corp,DC=example" };

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus") =>
        await world.CreateTenantAsync("agent-" + Guid.NewGuid().ToString("N")[..8], package);

    private static async Task<Guid> CreateAsync(TestTenant t, bool enable = true)
    {
        var response = await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "onprem_ad", displayName = "Head office AD", settings = Settings });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (enable)
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        }
        return id;
    }

    private static async Task<string> CodeAsync(TestTenant t, Guid connection)
    {
        var response = await t["admin"].Client.PostAsync($"{Base}/{connection}/agents/enrolment-code", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    private sealed class Agent(ECDsa key)
    {
        public ECDsa Key { get; } = key;
        public Guid Id { get; set; }
    }

    private Task<HttpResponseMessage> EnrolRawAsync(TestTenant t, string code, ECDsa key, string name = "AGENT-01") =>
        world.Factory.TenantClient(t.Key).PostAsJsonAsync("/api/v1/agent/enrol",
            new { code, name, publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), version = "1.0.0" });

    private async Task<Agent> EnrolAsync(TestTenant t, Guid connection)
    {
        var agent = new Agent(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var response = await EnrolRawAsync(t, await CodeAsync(t, connection), agent.Key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadAsync<JsonElement>();
        Assert.Equal(connection, body.GetProperty("connectionId").GetGuid());
        agent.Id = body.GetProperty("agentId").GetGuid();
        return agent;
    }

    private Task<HttpResponseMessage> SendAsync(TestTenant t, Agent agent, string path, object? payload = null, long? timestamp = null, ECDsa? signWith = null,
        string? sentBody = null)
    {
        var body = payload is null ? "" : JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(sentBody ?? body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(AgentProtocol.AgentHeader, agent.Id.ToString());
        request.Headers.TryAddWithoutValidation(AgentProtocol.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(AgentProtocol.SignatureHeader, AgentProtocol.Sign(signWith ?? agent.Key, "POST", path, ts, Encoding.UTF8.GetBytes(body)));
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private Task<int> RefreshAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<SyncAgentService>().RefreshAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private async Task<Guid> EnqueueAsync(TestTenant t, Guid connection)
    {
        var response = await t["admin"].Client.PostAsync($"{Base}/{connection}/agent-jobs", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<string?> StatusAsync(TestTenant t, Guid connection) =>
        world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.connection where id = @id", ("id", connection));

    [Fact]
    public async Task An_enrolment_code_works_once_and_only_its_hash_is_kept()
    {
        var t = await TenantAsync();
        var connection = await CreateAsync(t);
        var code = await CodeAsync(t, connection);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.agent_enrolment where code_hash = @c", ("c", code)));

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(HttpStatusCode.OK, (await EnrolRawAsync(t, code, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrolRawAsync(t, code, ECDsa.Create(ECCurve.NamedCurves.nistP256))).StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.AgentEnrolled, connection.ToString()));

        // A new code replaces an unused older one, an expired code is refused, and a key is enrolled once.
        var older = await CodeAsync(t, connection);
        var newer = await CodeAsync(t, connection);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrolRawAsync(t, older, ECDsa.Create(ECCurve.NamedCurves.nistP256))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EnrolRawAsync(t, newer, key)).StatusCode);
        var expired = await CodeAsync(t, connection);
        await world.Database.TenantScalarAsync<long>(t.Id,
            "with u as (update integrations.agent_enrolment set expires_at = now() - interval '1 minute' where used_at is null returning 1) select count(*) from u");
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrolRawAsync(t, expired, ECDsa.Create(ECCurve.NamedCurves.nistP256))).StatusCode);

        // Only an ECDSA P-256 key is accepted.
        var rsa = RSA.Create(2048);
        var wrongKey = await world.Factory.TenantClient(t.Key).PostAsJsonAsync("/api/v1/agent/enrol",
            new { code = await CodeAsync(t, connection), name = "AGENT-02", publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) });
        Assert.Equal(HttpStatusCode.BadRequest, wrongKey.StatusCode);
    }

    [Fact]
    public async Task Every_agent_request_must_be_signed_with_its_enrolled_key_inside_the_window()
    {
        var t = await TenantAsync();
        var connection = await CreateAsync(t);
        var agent = await EnrolAsync(t, connection);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, agent, "/api/v1/agent/heartbeat", new { version = "1.0.1" })).StatusCode);
        Assert.Equal("1.0.1", await world.Database.TenantScalarAsync<string>(t.Id, "select version from integrations.sync_agent where id = @id", ("id", agent.Id)));

        var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(t, agent, "/api/v1/agent/heartbeat", signWith: other)).StatusCode);
        var late = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(t, agent, "/api/v1/agent/heartbeat", timestamp: late)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(t, agent, "/api/v1/agent/heartbeat", new { version = "1" }, sentBody: "{\"version\":\"2\"}")).StatusCode);

        // A request older than one already accepted is a replay.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, agent, "/api/v1/agent/heartbeat", timestamp: now + 2)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(t, agent, "/api/v1/agent/heartbeat", timestamp: now)).StatusCode);

        var unsigned = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/jobs:pull");
        unsigned.Headers.TryAddWithoutValidation(AgentProtocol.AgentHeader, agent.Id.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key).SendAsync(unsigned)).StatusCode);
    }

    [Fact]
    public async Task One_agent_at_a_time_holds_a_job_and_its_records_are_kept_for_the_directory()
    {
        var t = await TenantAsync();
        var connection = await CreateAsync(t);
        var first = await EnrolAsync(t, connection);
        var second = await EnrolAsync(t, connection);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, first, "/api/v1/agent/jobs:pull")).StatusCode);
        var job = await EnqueueAsync(t, connection);
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsync($"{Base}/{connection}/agent-jobs", null)).StatusCode);

        var pulled = await SendAsync(t, first, "/api/v1/agent/jobs:pull");
        Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
        var view = await pulled.ReadAsync<JsonElement>();
        Assert.Equal(job, view.GetProperty("id").GetGuid());
        Assert.Equal("directory_sync", view.GetProperty("kind").GetString());
        Assert.Equal("dc01.corp.example", view.GetProperty("settings").GetProperty("host").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, second, "/api/v1/agent/jobs:pull")).StatusCode);
        Assert.Equal(job, (await (await SendAsync(t, first, "/api/v1/agent/jobs:pull")).ReadAsync<JsonElement>()).GetProperty("id").GetGuid());

        var batch = new
        {
            users = new[] { new { externalId = "S-1-5-21-1", removed = false, displayName = "Test User", email = "test.user@corp.example" } },
            groups = new[] { new { externalId = "S-1-5-21-9", removed = false, displayName = "Finance", membersAdded = new[] { "S-1-5-21-1" }, membersRemoved = Array.Empty<string>() } },
        };
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(t, second, $"/api/v1/agent/jobs/{job}/records", batch)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(t, first, $"/api/v1/agent/jobs/{job}/records", batch)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(t, first, $"/api/v1/agent/jobs/{job}/records", batch)).StatusCode);
        var tooMany = new { users = Enumerable.Range(0, 1001).Select(i => new { externalId = $"u{i}", removed = false }).ToArray() };
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(t, first, $"/api/v1/agent/jobs/{job}/records", tooMany)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, first, $"/api/v1/agent/jobs/{job}:complete", new { ok = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(t, first, $"/api/v1/agent/jobs/{job}:complete", new { ok = true })).StatusCode);
        var jobs = await (await t["admin"].Client.GetAsync($"{Base}/{connection}/agent-jobs")).ReadAsync<JsonElement>();
        var done = jobs.EnumerateArray().Single();
        Assert.Equal("succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("users").GetInt32());
        Assert.Equal(1, done.GetProperty("groups").GetInt32());
        Assert.Equal(1L, await world.Database.OutboxCountAsync(nameof(AgentJobFinished), t.Id));
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.agent_directory_record where job_id = @j", ("j", job)));
    }

    [Fact]
    public async Task A_lapsed_lease_puts_the_job_back_and_it_fails_after_three_attempts()
    {
        var t = await TenantAsync();
        var connection = await CreateAsync(t);
        var agent = await EnrolAsync(t, connection);
        var job = await EnqueueAsync(t, connection);

        for (var attempt = 1; attempt <= SyncAgentService.MaxAttempts; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(t, agent, "/api/v1/agent/jobs:pull")).StatusCode);
            await RefreshAsync(t, DateTimeOffset.UtcNow + SyncAgentService.Lease + TimeSpan.FromMinutes(1));
        }
        var status = await world.Database.TenantScalarAsync<string>(t.Id, "select status || ':' || error_class from integrations.agent_job where id = @j", ("j", job));
        Assert.Equal("failed:not_responding", status);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, agent, "/api/v1/agent/jobs:pull")).StatusCode);
        Assert.Equal(1L, await world.Database.OutboxCountAsync(nameof(AgentJobFinished), t.Id));
    }

    [Fact]
    public async Task A_revoked_agent_is_refused_at_once_and_its_job_goes_back_to_the_queue()
    {
        var t = await TenantAsync();
        var connection = await CreateAsync(t);
        var agent = await EnrolAsync(t, connection);
        var job = await EnqueueAsync(t, connection);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(t, agent, "/api/v1/agent/jobs:pull")).StatusCode);

        var employee = await world.SeedUserAsync(t, "employee", "employee");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.PostAsync($"{Base}/{connection}/agents/{agent.Id}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.PostAsync($"{Base}/{connection}/agents/enrolment-code", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.PostAsync($"{Base}/{connection}/agents/{agent.Id}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(t, agent, "/api/v1/agent/heartbeat")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(t, agent, $"/api/v1/agent/jobs/{job}:complete", new { ok = true })).StatusCode);
        Assert.Equal("queued", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.agent_job where id = @j", ("j", job)));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.AgentRevoked, connection.ToString()));

        var list = await (await t["admin"].Client.GetAsync($"{Base}/{connection}/agents")).ReadAsync<JsonElement>();
        var row = list.EnumerateArray().Single();
        Assert.False(row.GetProperty("online").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("revokedAt").ValueKind);
    }

    [Fact]
    public async Task A_quiet_agent_marks_the_connection_degraded_until_it_is_back_and_the_test_reports_it()
    {
        var t = await TenantAsync();
        var connection = await CreateAsync(t);

        var before = await (await t["admin"].Client.PostAsync($"{Base}/{connection}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("failed", before.GetProperty("status").GetString());
        Assert.Equal(["integrations.test.agent_missing", "integrations.test.agent_offline"],
            before.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("resultKey").GetString()!).ToArray());

        var agent = await EnrolAsync(t, connection);
        var after = await (await t["admin"].Client.PostAsync($"{Base}/{connection}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", after.GetProperty("status").GetString());

        var unavailable = await world.Database.OutboxCountAsync(nameof(ConnectorUnavailable), t.Id);
        await RefreshAsync(t, DateTimeOffset.UtcNow);
        Assert.Equal("healthy", await StatusAsync(t, connection));
        await RefreshAsync(t, DateTimeOffset.UtcNow + SyncAgentService.OfflineAfter + TimeSpan.FromMinutes(1));
        Assert.Equal("degraded", await StatusAsync(t, connection));
        Assert.Equal(unavailable + 1, await world.Database.OutboxCountAsync(nameof(ConnectorUnavailable), t.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(t, agent, "/api/v1/agent/heartbeat")).StatusCode);
        await RefreshAsync(t, DateTimeOffset.UtcNow);
        Assert.Equal("healthy", await StatusAsync(t, connection));
    }

    [Fact]
    public async Task Agents_stay_inside_their_tenant_and_need_the_package()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var connection = await CreateAsync(a);
        var agent = await EnrolAsync(a, connection);
        var code = await CodeAsync(a, connection);

        // Agent A's signed request on tenant B's host, and A's code there, are refused; B's administrator cannot see A's agents.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(b, agent, "/api/v1/agent/heartbeat")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await EnrolRawAsync(b, code, ECDsa.Create(ECCurve.NamedCurves.nistP256))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"{Base}/{connection}/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.PostAsync($"{Base}/{connection}/agents/{agent.Id}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.PostAsync($"{Base}/{connection}/agent-jobs", null)).StatusCode);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(b.Id, "select count(*) from integrations.sync_agent"));
        Assert.Equal(HttpStatusCode.OK, (await EnrolRawAsync(a, code, ECDsa.Create(ECCurve.NamedCurves.nistP256))).StatusCode);

        var plain = await TenantAsync("workplace");
        var refused = await plain["admin"].Client.PostAsJsonAsync(Base, new { kind = "onprem_ad", displayName = "No", settings = Settings });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Settings_need_a_host_an_ldaps_port_a_base_dn_and_wrapped_filters()
    {
        var t = await TenantAsync();
        async Task<HttpStatusCode> TryAsync(object settings) =>
            (await t["admin"].Client.PostAsJsonAsync(Base, new { kind = "onprem_ad", displayName = "Check", settings })).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { baseDn = "DC=corp,DC=example" }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { host = "dc01.corp.example", port = 389, baseDn = "DC=corp,DC=example" }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { host = "dc01.corp.example", baseDn = "corp" }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { host = "dc01.corp.example", baseDn = "DC=corp", userFilter = "objectClass=user" }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { host = "ldap://dc01", baseDn = "DC=corp" }));
        Assert.Equal(HttpStatusCode.Created, await TryAsync(new { host = "DC01.corp.example", port = 3269, baseDn = "DC=corp,DC=example", userFilter = "(objectClass=user)" }));
        var stored = await world.Database.TenantScalarAsync<string>(t.Id, "select settings ->> 'host' from integrations.connection where kind = 'onprem_ad'");
        Assert.Equal("dc01.corp.example", stored);
    }
}
