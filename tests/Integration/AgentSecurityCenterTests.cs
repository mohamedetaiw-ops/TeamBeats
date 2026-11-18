using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 027 R2 T017 (US6 scenarios 4 and 5, US9 scenario 3, FR-023): the Agent Security Center. Ten refused tool
/// calls in ten minutes raise one alert for the agent owner and the security officers over the agent's unit, later
/// matches join it; an injection finding raises an alert one severity higher for a watched High risk agent; an incident
/// opened from an alert records actions, linked runs and a closure note and exports as evidence, audited. Officers of
/// another unit and other tenants do not find the records, and the Security Center needs its package.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentSecurityCenterTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Security = "/api/v1/agents/security";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("asc-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "aip", "sec", "gulfsec", "africasec" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "GulfHr", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", gulf);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.AssignAsync(t, "gulfsec", BuiltInRoles.SecurityOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "africasec", BuiltInRoles.SecurityOfficer, "org_subtree", africa);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> PublishedAgentAsync(TestTenant t, string autonomy)
    {
        if (autonomy == "L3")
        {
            var current = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
            await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy",
                new { version = current.GetProperty("version").GetInt32(), policy = AgentPolicy.Default with { MaxAutonomy = "L3" } }));
        }
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("GulfHr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["autonomy"] = autonomy;
        if (autonomy == "L3")
        {
            definition["escalation"] = new Dictionary<string, string> { ["en"] = "Ask the HR team.", ["ar"] = "اسأل فريق الموارد البشرية." };
        }
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    /// <summary>A finished run of the agent with <paramref name="refused"/> refused tool calls just now.</summary>
    private async Task<Guid> RunAsync(TestTenant t, Guid agent, int refused, int tokens = 100)
    {
        var run = Guid.NewGuid();
        await world.Database.TenantScalarAsync<object>(t.Id,
            """
            insert into agents.run (tenant_id, id, agent_id, agent_version, user_id, org_unit_id, mode, status, outcome, language, classification, input_tokens,
              output_tokens, created_at, updated_at, finished_at)
            values (@t, @run, @agent, 1, @user, @unit, 'run', 'completed', 'answered', 'en', 'internal', @tokens, 0, now(), now(), now());
            insert into agents.run_step (tenant_id, run_id, n, kind, tool, decision, reason, at)
            select @t, @run, n, 'tool', 'test.order', 'refused', 'policy', now() from generate_series(1, @refused) n;
            """, ("t", t.Id), ("run", run), ("agent", agent), ("user", t["sara"].Id), ("unit", t.Unit("GulfHr")), ("tokens", tokens), ("refused", refused));
        return run;
    }

    private Task<IReadOnlyList<Guid>> ScanAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var raised = await sp.GetRequiredService<AgentSecurityCenter>().ScanAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
            return raised;
        });

    private Task<long> NotifiedAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @user and template_key = @template",
            ("user", t[user].Id), ("template", template));

    [Fact]
    public async Task Ten_refused_calls_raise_one_alert_for_the_owner_and_the_officers_over_the_unit_and_later_matches_join_it()
    {
        var t = await FreshAsync();
        var agent = await PublishedAgentAsync(t, "L1");
        var first = await RunAsync(t, agent, 6);
        Assert.Empty(await ScanAsync(t));

        var second = await RunAsync(t, agent, 4);
        var raised = Assert.Single(await ScanAsync(t));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentAlertRaised, raised.ToString());
        const string template = AgentSecurityCenter.AlertTemplate + ".denied_burst";
        Assert.Equal(1L, await NotifiedAsync(t, "sara", template));
        Assert.Equal(1L, await NotifiedAsync(t, "sec", template));
        Assert.Equal(1L, await NotifiedAsync(t, "gulfsec", template));
        Assert.Equal(0L, await NotifiedAsync(t, "africasec", template));

        // A later burst joins the open alert instead of raising another.
        await RunAsync(t, agent, 3);
        Assert.Empty(await ScanAsync(t));
        var alert = await OkAsync(t["gulfsec"].Client.GetAsync($"{Security}/alerts/{raised}"));
        Assert.Equal("denied_burst", alert.GetProperty("rule").GetString());
        Assert.Equal("medium", alert.GetProperty("severity").GetString());
        Assert.Equal(13, alert.GetProperty("matches").GetInt32());
        var runs = alert.GetProperty("runIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
        Assert.Contains(first, runs);
        Assert.Contains(second, runs);

        // An officer of another unit, a designer and another tenant do not find it.
        Assert.Equal(HttpStatusCode.NotFound, (await t["africasec"].Client.GetAsync($"{Security}/alerts/{raised}")).StatusCode);
        Assert.Empty((await OkAsync(t["africasec"].Client.GetAsync($"{Security}/alerts"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Security}/alerts")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["sec"].Client.GetAsync($"{Security}/alerts/{raised}")).StatusCode);

        // Dismissing needs the officer over the unit; a dismissed alert cannot be dismissed again.
        Assert.Equal(HttpStatusCode.NotFound, (await t["africasec"].Client.PostAsync($"{Security}/alerts/{raised}/dismiss", null)).StatusCode);
        var dismissed = await OkAsync(t["gulfsec"].Client.PostAsync($"{Security}/alerts/{raised}/dismiss", null));
        Assert.Equal("dismissed", dismissed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await t["gulfsec"].Client.PostAsync($"{Security}/alerts/{raised}/dismiss", null)).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentAlertDismissed, raised.ToString());
    }

    [Fact]
    public async Task A_high_risk_agent_is_watched_for_30_days_and_its_injection_alert_becomes_an_incident_with_evidence()
    {
        var t = await FreshAsync();
        var agent = await PublishedAgentAsync(t, "L3");
        Assert.Equal(1L, await NotifiedAsync(t, "gulfsec", AgentSecurityCenter.WatchTemplate));
        Assert.Equal(0L, await NotifiedAsync(t, "africasec", AgentSecurityCenter.WatchTemplate));
        var watch = Assert.Single((await OkAsync(t["sec"].Client.GetAsync($"{Security}/watch"))).EnumerateArray());
        Assert.Equal(agent, watch.GetProperty("agentId").GetGuid());
        var days = (watch.GetProperty("until").GetDateTimeOffset() - watch.GetProperty("since").GetDateTimeOffset()).TotalDays;
        Assert.Equal(30, Math.Round(days));
        Assert.Empty((await OkAsync(t["africasec"].Client.GetAsync($"{Security}/watch"))).EnumerateArray());

        // An injection finding raises a high alert, one level up (critical) for a watched agent.
        var run = await RunAsync(t, agent, 0);
        await world.Database.TenantScalarAsync<object>(t.Id,
            "insert into agents.injection_finding (tenant_id, id, run_id, agent_id, rule, source, at) values (@t, @id, @run, @agent, 'ignore_previous', 'knowledge', now())",
            ("t", t.Id), ("id", Guid.NewGuid()), ("run", run), ("agent", agent));
        var raised = Assert.Single(await ScanAsync(t));
        Assert.Empty(await ScanAsync(t));
        var alert = await OkAsync(t["sec"].Client.GetAsync($"{Security}/alerts/{raised}"));
        Assert.Equal("injection", alert.GetProperty("rule").GetString());
        Assert.Equal("critical", alert.GetProperty("severity").GetString());

        // An incident from the alert takes its severity and runs; the alert points at it.
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["africasec"].Client.PostAsJsonAsync($"{Security}/incidents", new { title = "Injection", alertId = raised })).StatusCode);
        var incident = await OkAsync(t["gulfsec"].Client.PostAsJsonAsync($"{Security}/incidents", new { title = "Injection in HR agent", alertId = raised }),
            HttpStatusCode.Created);
        var id = incident.GetProperty("id").GetGuid();
        Assert.Equal("critical", incident.GetProperty("severity").GetString());
        Assert.Contains(run, incident.GetProperty("runIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal("incident", (await OkAsync(t["sec"].Client.GetAsync($"{Security}/alerts/{raised}"))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentIncidentOpened, id.ToString());

        // Actions and severity changes follow the version; a stale one is refused.
        var changed = await OkAsync(t["gulfsec"].Client.PatchAsJsonAsync($"{Security}/incidents/{id}",
            new { version = 1, severity = "high", action = "Suspended the agent and told its owner." }));
        Assert.Equal("high", changed.GetProperty("severity").GetString());
        Assert.Single(changed.GetProperty("actions").EnumerateArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentIncidentUpdated, id.ToString());
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["gulfsec"].Client.PatchAsJsonAsync($"{Security}/incidents/{id}", new { version = 1, action = "Again" })).StatusCode);
        var bad = await t["gulfsec"].Client.PatchAsJsonAsync($"{Security}/incidents/{id}", new { version = 2, severity = "urgent" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await t["gulfsec"].Client.PostAsJsonAsync($"{Security}/incidents/{id}/close", new { version = 2, note = " " })).StatusCode);
        var closed = await OkAsync(t["gulfsec"].Client.PostAsJsonAsync($"{Security}/incidents/{id}/close",
            new { version = 2, note = "Source document cleaned; agent resumed after review." }));
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentIncidentClosed, id.ToString());
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["gulfsec"].Client.PatchAsJsonAsync($"{Security}/incidents/{id}", new { version = 3, action = "Late" })).StatusCode);

        // Evidence holds the incident, its alert and each run record; exporting it is audited.
        var evidence = await OkAsync(t["sec"].Client.GetAsync($"{Security}/incidents/{id}/evidence"));
        Assert.Equal(id, evidence.GetProperty("incident").GetProperty("id").GetGuid());
        Assert.Equal(raised, Assert.Single(evidence.GetProperty("alerts").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Single(evidence.GetProperty("runs").EnumerateArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentEvidenceExported, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["africasec"].Client.GetAsync($"{Security}/incidents/{id}/evidence")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["sec"].Client.GetAsync($"{Security}/incidents/{id}")).StatusCode);
    }

    [Fact]
    public async Task Revoking_an_agents_tokens_stops_its_waiting_runs_and_removes_what_it_holds_for_people()
    {
        var t = await FreshAsync("workplace_plus");
        var agent = await PublishedAgentAsync(t, "L1");
        var waiting = await RunAsync(t, agent, 0);
        await world.Database.TenantScalarAsync<object>(t.Id,
            """
            update agents.run set status = 'waiting', finished_at = null where id = @run;
            insert into agents.agent_briefing (tenant_id, user_id, agent_id, time, days, time_zone, created_at, next_run_at)
            values (@t, @sara, @agent, '08:00', '{1,2,3,4,5}', 'Asia/Dubai', now(), now() + interval '1 day');
            insert into agents.agent_memory_consent (tenant_id, user_id, agent_id, accepted_at) values (@t, @sec, @agent, now());
            """, ("t", t.Id), ("run", waiting), ("sara", t["sara"].Id), ("sec", t["sec"].Id), ("agent", agent));

        // Only a security officer over the agent's unit may do it, with a reason.
        Assert.False((await t["africasec"].Client.PostAsJsonAsync($"{Agents}/{agent}/revoke-tokens", new { reason = "Leaked" })).IsSuccessStatusCode);
        Assert.False((await t["sara"].Client.PostAsJsonAsync($"{Agents}/{agent}/revoke-tokens", new { reason = "Leaked" })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["gulfsec"].Client.PostAsJsonAsync($"{Agents}/{agent}/revoke-tokens", new { reason = "" })).StatusCode);
        var detail = await OkAsync(t["gulfsec"].Client.PostAsJsonAsync($"{Agents}/{agent}/revoke-tokens", new { reason = "Credential seen in a shared document" }));
        Assert.Equal(AgentStatuses.Published, detail.GetProperty("agent").GetProperty("status").GetString());

        Assert.Equal("tokens_revoked", await world.Database.TenantScalarAsync<string>(t.Id, "select outcome from agents.run where id = @run", ("run", waiting)));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.agent_briefing where agent_id = @a", ("a", agent)));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.agent_memory_consent where agent_id = @a", ("a", agent)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentTokensRevoked, agent.ToString());
        Assert.Equal(1L, await NotifiedAsync(t, "sara", AgentLifecycle.TokensRevokedTemplate));
        Assert.Equal(1L, await NotifiedAsync(t, "sec", AgentLifecycle.TokensRevokedTemplate));

        // Another tenant does not find the agent.
        var other = await FreshAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["sec"].Client.PostAsJsonAsync($"{Agents}/{agent}/revoke-tokens", new { reason = "Leaked" })).StatusCode);
    }

    [Fact]
    public async Task The_security_center_needs_its_package_and_raises_nothing_without_it()
    {
        var t = await FreshAsync("workplace_plus");
        var agent = await PublishedAgentAsync(t, "L1");
        await RunAsync(t, agent, 12);
        Assert.Empty(await ScanAsync(t));
        var response = await t["sec"].Client.GetAsync($"{Security}/alerts");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("ai.governance.security_center", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.security_alert"));
    }
}
