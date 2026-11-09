using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Specs 020 and 027, R1 foundations: the template library by package and policy with "not ready", installing a template
/// in the designer's org unit, drafts checked against the schema, the tool registry and the tenant policy on every save,
/// the computed risk class, the audience inside the designer's scope, two designers, the tenant agent policy, the tools
/// running as the person, and no access across org units, roles, packages or tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentRegistryTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ag-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "fadi", "emp", "aip", "sec", "pia" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", finance);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Task<JsonElement> InstallAsync(HttpClient client, string template, Guid unit) =>
        OkAsync(client.PostAsJsonAsync(Agents, new { templateId = template, orgUnitId = unit }), HttpStatusCode.Created);

    /// <summary>The draft definition as JSON, changed by <paramref name="edit"/>, ready to save at its revision.</summary>
    private static object Draft(JsonElement detail, Action<Dictionary<string, object?>> edit)
    {
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        edit(definition);
        return new { revision = detail.GetProperty("latest").GetProperty("revision").GetInt32(), definition };
    }

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    [Fact]
    public async Task The_template_library_shows_package_templates_with_not_ready_when_policy_blocks_a_tool()
    {
        var t = await FreshAsync();
        var library = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/templates"));
        var templates = library.GetProperty("templates").EnumerateArray().ToList();
        Assert.Equal(6, templates.Count);
        Assert.All(templates, x => Assert.True(x.GetProperty("ready").GetBoolean(), x.GetProperty("id").GetString()));
        var hr = templates.Single(x => x.GetProperty("id").GetString() == "hr_assistant");
        Assert.Equal("مساعد الموارد البشرية", hr.GetProperty("name").GetProperty("ar").GetString());
        Assert.Equal("L2", hr.GetProperty("autonomy").GetString());

        // People who do not design agents do not get the library.
        Assert.Equal("authz.denied", await ProblemAsync(t["emp"].Client.GetAsync($"{Agents}/templates"), HttpStatusCode.Forbidden));

        // The tenant allows only knowledge search: templates that need request status are not ready and cannot be installed.
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new
        {
            version = 0,
            policy = AgentPolicy.Default with { AllowedTools = [KnowledgeSearchTool.Name] },
        }));
        library = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/templates"));
        hr = library.GetProperty("templates").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "hr_assistant");
        Assert.False(hr.GetProperty("ready").GetBoolean());
        Assert.Equal(["requests.status"], hr.GetProperty("blockedTools").EnumerateArray().Select(e => e.GetString()));
        Assert.True(library.GetProperty("templates").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "enterprise_knowledge")
            .GetProperty("ready").GetBoolean());
        Assert.Equal("validation.agent.template_not_ready",
            await ProblemAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Installing_creates_a_draft_agent_in_the_designers_unit_with_a_computed_risk_class()
    {
        var t = await FreshAsync();
        var detail = await InstallAsync(t["sara"].Client, "hr_assistant", t.Unit("Hr"));
        var agent = detail.GetProperty("agent");
        Assert.Equal(AgentStatuses.Draft, agent.GetProperty("status").GetString());
        Assert.Equal("hr_assistant", agent.GetProperty("templateId").GetString());
        Assert.Equal(1, agent.GetProperty("templateVersion").GetInt32());
        Assert.Equal(t.Unit("Hr"), agent.GetProperty("orgUnitId").GetGuid());
        Assert.Equal(t["sara"].Id, agent.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(JsonValueKind.Null, agent.GetProperty("currentVersion").ValueKind);
        // L2 makes the HR Assistant Medium (spec 027 US9).
        Assert.Equal(AgentRiskClasses.Medium, agent.GetProperty("riskClass").GetString());
        var audience = detail.GetProperty("definition").GetProperty("audience");
        Assert.Equal(AgentAudience.Units, audience.GetProperty("scope").GetString());
        Assert.Equal([t.Unit("Hr")], audience.GetProperty("orgUnits").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(1, detail.GetProperty("latest").GetProperty("number").GetInt32());
        Assert.True(detail.GetProperty("canEdit").GetBoolean());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentInstalledFromTemplate));

        var knowledge = await InstallAsync(t["sara"].Client, "enterprise_knowledge", t.Unit("Hr"));
        Assert.Equal(AgentRiskClasses.Low, knowledge.GetProperty("agent").GetProperty("riskClass").GetString());

        // Not in a unit Sara does not design for, not in a unit that does not exist, not from an unknown template.
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Finance") }),
            HttpStatusCode.Forbidden));
        Assert.Equal("validation.agent.org_unit", await ProblemAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = Guid.NewGuid() }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.agent.template", await ProblemAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "procurement", orgUnitId = t.Unit("Hr") }),
            HttpStatusCode.BadRequest));
        Assert.Equal("authz.denied", await ProblemAsync(t["emp"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }),
            HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task Every_save_is_checked_against_the_schema_the_tool_registry_and_the_tenant_policy()
    {
        var t = await FreshAsync();
        var detail = await InstallAsync(t["sara"].Client, "hr_assistant", t.Unit("Hr"));
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var draft = $"{Agents}/{id}/draft";

        async Task<string> RefusedAsync(Action<Dictionary<string, object?>> edit) =>
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync(draft, Draft(detail, edit)), HttpStatusCode.BadRequest);

        Assert.Equal("validation.agent.autonomy_above_policy", await RefusedAsync(d => d["autonomy"] = "L3"));
        Assert.Equal("validation.agent.tool_unknown", await RefusedAsync(d => d["tools"] = new[] { "mail.send" }));
        Assert.Equal("validation.agent.classification_above_policy", await RefusedAsync(d => d["maxClassification"] = "restricted"));
        Assert.Equal("validation.agent.instructions", await RefusedAsync(d => d["instructions"] = new Dictionary<string, string> { ["en"] = "Help." }));
        // The audience stays inside Sara's scope, and only an AI Policy Administrator makes it tenant-wide.
        Assert.Equal("validation.agent.audience_scope", await RefusedAsync(d => d["audience"] = new { scope = "org_units", orgUnits = new[] { t.Unit("Finance") } }));
        Assert.Equal("validation.agent.audience_tenant", await RefusedAsync(d => d["audience"] = new { scope = "tenant" }));

        // A business change saves and bumps the revision; the version stays one draft.
        var saved = await OkAsync(t["sara"].Client.PutAsJsonAsync(draft, Draft(detail, d =>
        {
            d["name"] = new Dictionary<string, string> { ["en"] = "Acme HR Helper", ["ar"] = "مساعد أكمي للموارد البشرية" };
            d["autonomy"] = "L0";
        })));
        Assert.Equal("Acme HR Helper", saved.GetProperty("agent").GetProperty("name").GetProperty("en").GetString());
        Assert.Equal(2, saved.GetProperty("latest").GetProperty("revision").GetInt32());
        Assert.Equal(1, saved.GetProperty("latest").GetProperty("number").GetInt32());
        Assert.Equal(AgentRiskClasses.Low, saved.GetProperty("agent").GetProperty("riskClass").GetString());

        // Two designers: a save over a newer revision is refused.
        Assert.Equal("conflict.agent.revision", await ProblemAsync(t["sara"].Client.PutAsJsonAsync(draft, Draft(detail, d => d["tone"] = "formal")), HttpStatusCode.Conflict));

        // The policy is read on every save: once the cap is L1, an L2 draft is refused.
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = AgentPolicy.Default with { MaxAutonomy = "L1" } }));
        Assert.Equal("validation.agent.autonomy_above_policy",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync(draft, Draft(saved, d => d["autonomy"] = "L2")), HttpStatusCode.BadRequest));

        // The AI Policy Administrator is not a designer of the HR unit, so cannot edit the agent either.
        Assert.Equal("authz.denied", await ProblemAsync(t["aip"].Client.PutAsJsonAsync(draft, Draft(saved, d => d["tone"] = "formal")), HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task A_tenant_wide_audience_needs_the_ai_policy_administrator_and_is_audited()
    {
        var t = await FreshAsync();
        // A tenant-level designer who also holds ai.policy_manage may widen the audience.
        await world.AssignAsync(t, "aip", BuiltInRoles.AgentDesigner, "tenant", null);
        var detail = await InstallAsync(t["aip"].Client, "enterprise_knowledge", t.Unit("Hr"));
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var saved = await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", Draft(detail, d => d["audience"] = new { scope = "tenant" })));
        Assert.Equal(AgentAudience.Tenant, saved.GetProperty("definition").GetProperty("audience").GetProperty("scope").GetString());
        // A tenant-wide audience makes the agent Medium.
        Assert.Equal(AgentRiskClasses.Medium, saved.GetProperty("agent").GetProperty("riskClass").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentAudienceChanged));
    }

    [Fact]
    public async Task Agents_are_seen_only_by_their_owner_their_units_designers_and_overseers_never_across_tenants()
    {
        var t = await FreshAsync();
        var hr = await InstallAsync(t["sara"].Client, "hr_assistant", t.Unit("Hr"));
        var id = hr.GetProperty("agent").GetProperty("id").GetGuid();
        var finance = await InstallAsync(t["fadi"].Client, "content_writing", t.Unit("Finance"));
        var financeId = finance.GetProperty("agent").GetProperty("id").GetGuid();

        static IEnumerable<Guid> Ids(JsonElement list) => list.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("id").GetGuid());

        Assert.Equal([id], Ids(await OkAsync(t["sara"].Client.GetAsync(Agents))));
        Assert.Equal([financeId], Ids(await OkAsync(t["fadi"].Client.GetAsync(Agents))));
        Assert.Equal("not_found", await ProblemAsync(t["fadi"].Client.GetAsync($"{Agents}/{id}"), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(t["fadi"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", Draft(hr, _ => { })), HttpStatusCode.NotFound));
        Assert.Empty(Ids(await OkAsync(t["emp"].Client.GetAsync(Agents))));
        Assert.Equal("not_found", await ProblemAsync(t["emp"].Client.GetAsync($"{Agents}/{id}"), HttpStatusCode.NotFound));

        // Security officers and AI Policy Administrators see every agent, read-only.
        var all = Ids(await OkAsync(t["sec"].Client.GetAsync(Agents))).ToHashSet();
        Assert.True(all.SetEquals([id, financeId]));
        var seen = await OkAsync(t["sec"].Client.GetAsync($"{Agents}/{id}"));
        Assert.False(seen.GetProperty("canEdit").GetBoolean());
        Assert.Equal(2, Ids(await OkAsync(t["aip"].Client.GetAsync(Agents))).Count());

        // Another tenant's administrator cannot see or change this tenant's agent.
        var other = await FreshAsync();
        Assert.Empty(Ids(await OkAsync(other["admin"].Client.GetAsync(Agents))));
        Assert.Equal("not_found", await ProblemAsync(other["admin"].Client.GetAsync($"{Agents}/{id}"), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(other["admin"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", Draft(hr, _ => { })), HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task The_tenant_agent_policy_is_read_by_designers_changed_only_by_the_ai_policy_administrator_and_versioned()
    {
        var t = await FreshAsync();
        var initial = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/policy"));
        Assert.Equal(0, initial.GetProperty("version").GetInt32());
        Assert.Equal("L2", initial.GetProperty("policy").GetProperty("maxAutonomy").GetString());
        Assert.Equal(20, initial.GetProperty("policy").GetProperty("maxToolCalls").GetInt32());
        Assert.Equal("authz.denied", await ProblemAsync(t["emp"].Client.GetAsync($"{Agents}/policy"), HttpStatusCode.Forbidden));

        var stricter = AgentPolicy.Default with { MaxToolCalls = 10, ToolRules = new() { ["requests.status"] = ToolApprovalRules.Confirm } };
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = stricter }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied", await ProblemAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = stricter }), HttpStatusCode.Forbidden));

        var saved = await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = stricter }));
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentPolicyChanged));
        Assert.Equal("conflict.agent_policy.version",
            await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = AgentPolicy.Default }), HttpStatusCode.Conflict));

        // The tool picker shows the stricter rule.
        var tools = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/tools"));
        var status = tools.GetProperty("tools").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "requests.status");
        Assert.Equal(ToolApprovalRules.Execute, status.GetProperty("defaultRule").GetString());
        Assert.Equal(ToolApprovalRules.Confirm, status.GetProperty("rule").GetString());
        Assert.Equal(ToolSideEffects.Read, status.GetProperty("sideEffect").GetString());
        Assert.Equal(["knowledge.search", "requests.status", "tasks.inbox"], tools.GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()));

        async Task<string> RefusedAsync(AgentPolicy policy) =>
            await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 1, policy }), HttpStatusCode.BadRequest);

        Assert.Equal("validation.agent_policy.autonomy", await RefusedAsync(AgentPolicy.Default with { MaxAutonomy = "L4" }));
        Assert.Equal("validation.agent_policy.tools", await RefusedAsync(AgentPolicy.Default with { AllowedTools = ["mail.send"] }));
        Assert.Equal("validation.agent_policy.not_built", await RefusedAsync(AgentPolicy.Default with { ScheduledAgents = true }));
        Assert.Equal("validation.agent_policy.max_tool_calls", await RefusedAsync(AgentPolicy.Default with { MaxToolCalls = 0 }));
        Assert.Equal("validation.agent_policy.max_steps", await RefusedAsync(AgentPolicy.Default with { MaxSteps = 5 }));

        // Another tenant keeps the platform default.
        var other = await FreshAsync();
        Assert.Equal(0, (await OkAsync(other["aip"].Client.GetAsync($"{Agents}/policy"))).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Without_the_agents_capability_everything_is_refused()
    {
        var tenant = await world.CreateTenantAsync("ag-p-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var capabilities = new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
            [Capabilities.OrgHierarchy] = new { enabled = true },
        };
        var created = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new { capabilities }), HttpStatusCode.Created);
        var version = created.GetProperty("version").GetInt32();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace/versions/{version}:publish", null)).EnsureSuccessStatusCode();
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { packageKey = "workplace", version })).EnsureSuccessStatusCode();

        foreach (var call in new Func<Task<HttpResponseMessage>>[]
        {
            () => tenant["admin"].Client.GetAsync($"{Agents}/templates"),
            () => tenant["admin"].Client.GetAsync(Agents),
            () => tenant["admin"].Client.GetAsync($"{Agents}/policy"),
            () => tenant["admin"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = Guid.NewGuid() }),
        })
        {
            Assert.Equal("entitlement.not_in_package", await ProblemAsync(call(), HttpStatusCode.Forbidden));
        }
    }

    [Fact]
    public async Task Tools_run_as_the_person_and_return_only_what_they_may_read()
    {
        var t = await FreshAsync();
        var client = t["pia"].Client;
        async Task<Guid> NewsAsync(string title, string body, string? classification = null)
        {
            var variants = new Dictionary<string, object>
            {
                ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = body } } } } } },
            };
            var item = await OkAsync(client.PostAsJsonAsync("/api/v1/content/items", new { type = "news", classification, variants }), HttpStatusCode.Created);
            var id = item.GetProperty("id").GetGuid();
            await OkAsync(client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
            return id;
        }
        var open = await NewsAsync("Forklift inspection checklist", "Check the forklift brakes, horn and mast chains before every shift.");
        await NewsAsync("Forklift bonus pool", "Confidential forklift bonus pool for the sales directors.", "confidential");
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }

        using var args = JsonDocument.Parse("""{"query":"forklift"}""");
        var result = await world.Factory.AsUserAsync(t.Id, t["emp"].Id, async sp =>
        {
            var tool = sp.GetRequiredService<ToolRegistry>().Find(KnowledgeSearchTool.Name)!;
            return await tool.ExecuteAsync(args.RootElement, new ToolCallContext(Guid.NewGuid(), Guid.NewGuid(), 1, t["emp"].Id, false), CancellationToken.None);
        });
        Assert.True(result.Ok);
        var source = Assert.Single(result.Sources);
        Assert.Equal(open, source.ItemId);
        Assert.Contains("<untrusted_passages>", result.Content);
        Assert.DoesNotContain("bonus", result.Content, StringComparison.OrdinalIgnoreCase);

        using var bad = JsonDocument.Parse("""{"query":""}""");
        var refused = await world.Factory.AsUserAsync(t.Id, t["emp"].Id, sp =>
            sp.GetRequiredService<ToolRegistry>().Find(KnowledgeSearchTool.Name)!.ExecuteAsync(bad.RootElement,
                new ToolCallContext(Guid.NewGuid(), Guid.NewGuid(), 1, t["emp"].Id, false), CancellationToken.None));
        Assert.False(refused.Ok);
        Assert.Equal("tool.invalid_arguments", refused.ErrorKey);

        // The person's own requests and inbox: an employee with none gets empty lists, not someone else's.
        foreach (var name in new[] { "requests.status", "tasks.inbox" })
        {
            using var none = JsonDocument.Parse("{}");
            var own = await world.Factory.AsUserAsync(t.Id, t["emp"].Id, sp =>
                sp.GetRequiredService<ToolRegistry>().Find(name)!.ExecuteAsync(none.RootElement,
                    new ToolCallContext(Guid.NewGuid(), Guid.NewGuid(), 1, t["emp"].Id, false), CancellationToken.None));
            Assert.True(own.Ok, name);
            Assert.Equal("[]", own.Content);
        }
    }
}
