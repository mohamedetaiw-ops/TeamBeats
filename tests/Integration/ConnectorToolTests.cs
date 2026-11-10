using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 (R2) T008 and T009, agent tool path (spec 027 FR-006): the integration administrator registers a connector
/// action as a tenant tool; agents hold it through the AI policy like any tool; the tool gateway checks run first and
/// the connector receives only the validated, declared parameters; a write needs L3; a disabled connection turns the tool
/// off; and a tenant's tools never reach another tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ConnectorToolTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Base = "/api/v1/integrations/pack-connections";

    private FakeTicketing Ticketing => world.Factory.Ticketing;

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ct-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        return t;
    }

    private async Task<Guid> ConnectionAsync(TestTenant t)
    {
        var id = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @id, 'test_ticketing', 'Service desk', 'healthy', true, 600)
            """, ("t", t.Id), ("id", id));
        return id;
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

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, Guid connection, string action, string name,
        string description = "Looks up the state of a service desk incident by its number.") =>
        client.PostAsJsonAsync($"{Base}/{connection}/tools", new
        {
            action, name, description,
            title = new Dictionary<string, string> { ["en"] = "Incident status", ["ar"] = "حالة البلاغ" },
        });

    private static string Call(string tool, string args) => $"[fake:tool:{tool.Replace(".", "__")}] [fake:args:{args}]";

    private static List<(string Kind, string? Tool, string Decision, string? Reason)> Steps(JsonElement record) =>
        record.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("kind").GetString()!, s.GetProperty("tool").GetString(), s.GetProperty("decision").GetString()!,
            s.GetProperty("reason").GetString())).ToList();

    /// <summary>Installs a template in Hr as Sara with the given tools, then submits and publishes version 1.</summary>
    private async Task<Guid> AgentAsync(TestTenant t, string[] tools, string autonomy)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = autonomy;
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    [Fact]
    public async Task The_integration_administrator_registers_an_action_with_declared_inputs_as_a_tool()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);

        var tool = await OkAsync(RegisterAsync(t["admin"].Client, c, "get_status", "incident_status"), HttpStatusCode.Created);
        Assert.Equal("connector.incident_status", tool.GetProperty("name").GetString());
        Assert.Equal("read", tool.GetProperty("sideEffect").GetString());
        Assert.Equal("execute", tool.GetProperty("approvalRule").GetString());
        Assert.Equal("حالة البلاغ", tool.GetProperty("title").GetProperty("ar").GetString());
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ActionChanged, c.ToString()));

        // A write action is an external write for agents: it waits for confirmation by default.
        var write = await OkAsync(RegisterAsync(t["admin"].Client, c, "create_incident", "open_incident", "Opens a service desk incident for the employee."),
            HttpStatusCode.Created);
        Assert.Equal("write_external", write.GetProperty("sideEffect").GetString());
        Assert.Equal("confirm", write.GetProperty("approvalRule").GetString());

        var list = await OkAsync(t["admin"].Client.GetAsync($"{Base}/{c}/tools"));
        Assert.Equal(["connector.incident_status", "connector.open_incident"], list.EnumerateArray().Select(x => x.GetProperty("name").GetString()));

        // Checks: a known action, a name and description in shape, one tool per name and per action.
        Assert.Equal("validation.connector_tool.action",
            await ProblemAsync(RegisterAsync(t["admin"].Client, c, "drop_tables", "dropper"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.connector_tool.name",
            await ProblemAsync(RegisterAsync(t["admin"].Client, c, "create_incident_keyed", "Bad Name"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.connector_tool.description",
            await ProblemAsync(RegisterAsync(t["admin"].Client, c, "create_incident_keyed", "keyed", "short"), HttpStatusCode.BadRequest));
        Assert.Equal("conflict.integrations.tool_exists",
            await ProblemAsync(RegisterAsync(t["admin"].Client, c, "create_incident_keyed", "incident_status"), HttpStatusCode.Conflict));
        Assert.Equal("conflict.integrations.tool_exists",
            await ProblemAsync(RegisterAsync(t["admin"].Client, c, "get_status", "status_again"), HttpStatusCode.Conflict));

        // Only integration administrators, and never across tenants.
        await ProblemAsync(RegisterAsync(t["sara"].Client, c, "create_incident_keyed", "keyed"), HttpStatusCode.Forbidden);
        var other = await FreshAsync();
        await ProblemAsync(other["admin"].Client.GetAsync($"{Base}/{c}/tools"), HttpStatusCode.NotFound);
        await ProblemAsync(RegisterAsync(other["admin"].Client, c, "create_incident_keyed", "keyed"), HttpStatusCode.NotFound);
        var otherTools = await OkAsync(other["sara"].Client.GetAsync($"{Agents}/tools"));
        Assert.DoesNotContain(otherTools.GetProperty("tools").EnumerateArray(), x => x.GetProperty("name").GetString()!.StartsWith("connector.", StringComparison.Ordinal));

        // Removing it takes it out of the registry.
        var id = write.GetProperty("id").GetGuid();
        await ProblemAsync(other["admin"].Client.DeleteAsync($"{Base}/{c}/tools/{id}"), HttpStatusCode.NotFound);
        await OkAsync(t["admin"].Client.DeleteAsync($"{Base}/{c}/tools/{id}"), HttpStatusCode.NoContent);
        var registry = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/tools"));
        var names = registry.GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains("connector.incident_status", names);
        Assert.DoesNotContain("connector.open_incident", names);
        Assert.Contains("knowledge.search", names);
        var listed = registry.GetProperty("tools").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "connector.incident_status");
        Assert.Equal("Incident status", listed.GetProperty("title").GetProperty("en").GetString());
    }

    [Fact]
    public async Task An_agent_calls_a_connector_tool_through_the_gateway_with_only_the_validated_parameters()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        await OkAsync(RegisterAsync(t["admin"].Client, c, "get_status", "incident_status"), HttpStatusCode.Created);
        var agent = await AgentAsync(t, ["knowledge.search", "connector.incident_status"], "L0");

        // A parameter the schema does not declare is refused by the gateway before the connector is called.
        var refused = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs",
            new { message = "Where is my laptop? " + Call("connector.incident_status", """{"number":"INC000042","assignee":"me"}""") }));
        var refusedRecord = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{refused.GetProperty("id").GetGuid()}"));
        Assert.Contains(("tool", "connector.incident_status", "refused", "tool.invalid_arguments"), Steps(refusedRecord));
        Assert.Empty(Ticketing.CallsFor(c));

        var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs",
            new { message = "Where is my laptop? " + Call("connector.incident_status", """{"number":"INC000042"}""") }));
        var record = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{run.GetProperty("id").GetGuid()}"));
        Assert.Contains(("tool", "connector.incident_status", "executed", null), Steps(record));
        var call = Assert.Single(Ticketing.CallsFor(c));
        Assert.Equal("get_status", call.Action);
        Assert.Equal("""{"number":"INC000042"}""", call.Input);
        Assert.StartsWith("agent:", call.IdempotencyKey, StringComparison.Ordinal);

        // The call is in run history like any other, with the agent run as its correlation.
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from integrations.run where connection_id = @c and action = 'get_status' and correlation_id like 'agent-run:%' and status = 'succeeded'",
            ("c", c)));

        // A disabled connection turns the tool off for every agent at once.
        await OkAsync(t["admin"].Client.PostAsync($"{Base}/{c}:disable", null));
        var off = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs",
            new { message = "And now? " + Call("connector.incident_status", """{"number":"INC000042"}""") }));
        var offRecord = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{off.GetProperty("id").GetGuid()}"));
        Assert.Contains(("tool", "connector.incident_status", "refused", "tool.connector_unavailable"), Steps(offRecord));
        Assert.Single(Ticketing.CallsFor(c));
    }

    [Fact]
    public async Task A_connector_write_tool_needs_more_than_the_autonomy_R1_builds_and_the_policy_can_block_it()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        await OkAsync(RegisterAsync(t["admin"].Client, c, "create_incident", "open_incident", "Opens a service desk incident for the employee."),
            HttpStatusCode.Created);

        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "connector.open_incident" };
        definition["autonomy"] = "L2";
        Assert.Equal("validation.agent.tool_above_autonomy",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }), HttpStatusCode.BadRequest));

        // The AI policy administrator sees the tool and may only make its rule stricter.
        var policy = await OkAsync(t["aip"].Client.GetAsync("/api/v1/agents/policy"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(policy.GetProperty("policy").GetRawText())!;
        body["toolRules"] = new Dictionary<string, string> { ["connector.open_incident"] = "block" };
        var changed = await OkAsync(t["aip"].Client.PutAsJsonAsync("/api/v1/agents/policy", new { version = policy.GetProperty("version").GetInt32(), policy = body }));
        Assert.Equal("block", changed.GetProperty("policy").GetProperty("toolRules").GetProperty("connector.open_incident").GetString());
        var tools = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/tools"));
        var tool = tools.GetProperty("tools").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "connector.open_incident");
        Assert.False(tool.GetProperty("allowed").GetBoolean());
        Assert.Equal("write_external", tool.GetProperty("sideEffect").GetString());
        Assert.Empty(Ticketing.CallsFor(c));
    }
}
