using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 027 R3 T023 (US1 scenario 5, FR-015, FR-024): L4 bounded autonomy runs only as an agent task of a published
/// process version that a process owner and a holder of ai.policy_manage, two different people, approved. The run acts
/// for the person who started the instance, takes only the step's tools without a confirmation card, refuses actions that
/// need an approver, and a limit breach fails the step into an incident and opens a security incident. Package and
/// tenant boundaries hold. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentProcessTaskTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Processes = "/api/v1/workflow/processes";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("l4-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "owner", "aip", "both", "operator", "sec" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "operator", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "owner", BuiltInRoles.WorkflowDesigner, "tenant", null);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "both", BuiltInRoles.WorkflowDesigner, "tenant", null);
        await world.AssignAsync(t, "both", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "operator", BuiltInRoles.ProcessOperator, "tenant", null);
        await world.AssignAsync(t, "operator", BuiltInRoles.ContentEditor, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var problem = await OkAsync(call, expected);
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : problem.GetProperty("detailKey").GetString()!;
    }

    private async Task AllowL4Async(TestTenant t)
    {
        var current = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy",
            new { version = current.GetProperty("version").GetInt32(), policy = AgentPolicy.Default with { MaxAutonomy = "L4" } }));
    }

    private async Task<Guid> AgentAsync(TestTenant t, string autonomy = "L4")
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search", "test.note", "test.order" };
        definition["autonomy"] = autonomy;
        definition["escalation"] = new Dictionary<string, string> { ["en"] = "Ask the HR team.", ["ar"] = "اسأل فريق الموارد البشرية." };
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    /// <summary>Start, an agent task that notes the request (its answer becomes <c>summary</c>), end.</summary>
    private static string Model(Guid agent, string tools = "test.note test.order", int minutes = 10) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          <bpmn:process id="p" name="Onboarding notes">
            <bpmn:startEvent id="s"/>
            <bpmn:serviceTask id="agent" name="Write the welcome note"><bpmn:extensionElements>
              <tb:agent id="{agent}" tools="{tools}" minutes="{minutes}"/>
              <tb:input name="request" source="ask"/>
              <tb:output variable="summary" source="answer"/>
            </bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="e"/>
            <bpmn:sequenceFlow id="f1" sourceRef="s" targetRef="agent"/><bpmn:sequenceFlow id="f2" sourceRef="agent" targetRef="e"/>
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static async Task PublishAsync(TestTenant t, string key, string xml)
    {
        await OkAsync(t["owner"].Client.PutAsJsonAsync($"{Processes}/{key}", new { bpmnXml = xml }));
        await OkAsync(t["owner"].Client.PostAsync($"{Processes}/{key}:publish", null));
    }

    private static async Task<Guid> StartAsync(TestTenant t, string key, string ask) =>
        (await OkAsync(t["operator"].Client.PostAsJsonAsync($"{Processes}/{key}/instances", new { businessKey = "NH-" + Guid.NewGuid().ToString("N")[..6], variables = new { ask } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static Task<JsonElement> ApproveAsync(TestTenant t, string user, string key, string role, HttpStatusCode expected = HttpStatusCode.OK) =>
        OkAsync(t[user].Client.PostAsJsonAsync($"{Processes}/{key}/versions/1/autonomy:approve", new { @as = role }), expected);

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private Task<string?> StatusAsync(TestTenant t, Guid instance) =>
        world.Database.ScalarInTenantAsync<string>(t.Id, $"select status from workflow.process_instance where id = '{instance}'");

    private Task<string?> IncidentClassAsync(TestTenant t, Guid instance) =>
        world.Database.ScalarInTenantAsync<string>(t.Id, $"select error_class from workflow.incident where instance_id = '{instance}' order by raised_at desc limit 1");

    [Fact]
    public async Task An_agent_task_runs_only_after_two_people_approve_the_version_and_acts_for_the_starter_with_the_step_tools()
    {
        var t = await FreshAsync();
        await AllowL4Async(t);
        var agent = await AgentAsync(t);
        await PublishAsync(t, "welcome", Model(agent));

        // Before the approval, the step stops at once with an incident and the agent does not run.
        var early = await StartAsync(t, "welcome", "Note this [fake:tool:test__note] [fake:args:{\"text\":\"Welcome Lina\"}]");
        await DispatchAsync();
        Assert.Equal("agent_not_approved", await IncidentClassAsync(t, early));
        Assert.Empty(world.Factory.AgentTools.For(t["operator"].Id));

        // The approval shows the agent tasks; one person cannot sign both parts; owner and policy approver sign.
        var view = await OkAsync(t["owner"].Client.GetAsync($"{Processes}/welcome/autonomy"));
        Assert.Equal(1, view.GetProperty("version").GetInt32());
        Assert.Equal(agent, view.GetProperty("tasks")[0].GetProperty("agentId").GetGuid());
        Assert.Equal(["owner"], view.GetProperty("canApproveAs").EnumerateArray().Select(x => x.GetString()));
        await OkAsync(t["sec"].Client.GetAsync($"{Processes}/welcome/autonomy"), HttpStatusCode.NotFound);
        await ApproveAsync(t, "both", "welcome", "owner");
        Assert.Equal("conflict.process.autonomy_same_person",
            await ProblemKeyAsync(t["both"].Client.PostAsJsonAsync($"{Processes}/welcome/versions/1/autonomy:approve", new { @as = "policy" }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.process.autonomy_signed",
            await ProblemKeyAsync(t["owner"].Client.PostAsJsonAsync($"{Processes}/welcome/versions/1/autonomy:approve", new { @as = "owner" }), HttpStatusCode.Conflict));
        await ApproveAsync(t, "aip", "welcome", "owner", HttpStatusCode.Forbidden);
        var approved = await ApproveAsync(t, "aip", "welcome", "policy");
        Assert.True(approved.GetProperty("approved").GetBoolean());
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.ProcessAutonomyApproved)));

        // The run acts for the operator who started it: the note is written without a confirmation card, and the answer becomes a variable.
        var instance = await StartAsync(t, "welcome", "Note this [fake:tool:test__note] [fake:args:{\"text\":\"Welcome Lina\"}]");
        await DispatchAsync();
        Assert.Equal("completed", await StatusAsync(t, instance));
        var call = Assert.Single(world.Factory.AgentTools.For(t["operator"].Id), c => c.Tool == "test.note");
        Assert.Contains("Welcome Lina", call.Arguments);
        var variables = await world.Database.ScalarInTenantAsync<string>(t.Id, $"select variables::text from workflow.process_instance where id = '{instance}'");
        Assert.Contains("summary", variables);
        var run = await world.Database.ScalarInTenantAsync<string>(t.Id,
            $"select mode || ':' || status || ':' || user_id from agents.run where id = (select run_id from agents.process_task where instance_id = '{instance}')");
        Assert.Equal($"autonomous:completed:{t["operator"].Id}", run);

        // An action that needs its own approver is refused inside the run, and a tool outside the step's list is not offered or run.
        var order = await StartAsync(t, "welcome", "Order [fake:tool:test__order] [fake:args:{\"item\":\"Desk lamp\",\"amount\":40}]");
        await DispatchAsync();
        Assert.Equal("completed", await StatusAsync(t, order));
        Assert.DoesNotContain(world.Factory.AgentTools.For(t["operator"].Id), c => c.Tool == "test.order");
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from agents.run_step s join agents.process_task p on p.run_id = s.run_id where s.reason = 'tool.approval_not_available'"));

        // A new version needs both approvals again.
        await PublishAsync(t, "welcome", Model(agent, "test.note", 5));
        var fresh = await OkAsync(t["owner"].Client.GetAsync($"{Processes}/welcome/autonomy"));
        Assert.Equal(2, fresh.GetProperty("version").GetInt32());
        Assert.False(fresh.GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task A_limit_breach_fails_the_step_into_an_incident_and_opens_a_security_incident()
    {
        var t = await FreshAsync();
        await AllowL4Async(t);
        var agent = await AgentAsync(t);
        await PublishAsync(t, "looping", Model(agent, "test.note"));
        await ApproveAsync(t, "owner", "looping", "owner");
        await ApproveAsync(t, "aip", "looping", "policy");

        var instance = await StartAsync(t, "looping", "Keep noting [fake:loop] [fake:tool:test__note] [fake:args:{\"text\":\"Again\"}]");
        await DispatchAsync();
        Assert.Equal("agent_limit_steps", await IncidentClassAsync(t, instance));
        var incidents = await OkAsync(t["sec"].Client.GetAsync($"{Agents}/security/incidents"));
        var incident = Assert.Single(incidents.EnumerateArray());
        Assert.Equal("l4_limit", incident.GetProperty("kind").GetString());
        Assert.Equal(agent, incident.GetProperty("agentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, incident.GetProperty("openedBy").ValueKind);
    }

    [Fact]
    public async Task L4_needs_its_package_and_an_L4_agent_and_stays_in_its_tenant()
    {
        // Without ai.agents.autonomous: the policy cannot allow L4 and a model with an agent task does not publish.
        var plus = await FreshAsync("workplace_plus");
        await world.SetCapabilityAsync(plus, Capabilities.WorkflowBpmn, new { enabled = true });
        var current = await OkAsync(plus["aip"].Client.GetAsync($"{Agents}/policy"));
        await OkAsync(plus["aip"].Client.PutAsJsonAsync($"{Agents}/policy",
            new { version = current.GetProperty("version").GetInt32(), policy = AgentPolicy.Default with { MaxAutonomy = "L4" } }), HttpStatusCode.Forbidden);
        await OkAsync(plus["owner"].Client.PutAsJsonAsync($"{Processes}/welcome", new { bpmnXml = Model(Guid.NewGuid()) }));
        var refused = await OkAsync(plus["owner"].Client.PostAsync($"{Processes}/welcome:publish", null), HttpStatusCode.BadRequest);
        Assert.Equal("validation.process.agent_package", refused.GetProperty("errors").GetProperty("bpmnXml")[0].GetProperty("key").GetString());

        // An L3 agent in an approved process is refused: only an L4 agent runs unattended.
        var t = await FreshAsync();
        await AllowL4Async(t);
        var l3 = await AgentAsync(t, "L3");
        await PublishAsync(t, "governed", Model(l3, "test.note"));
        await ApproveAsync(t, "owner", "governed", "owner");
        await ApproveAsync(t, "aip", "governed", "policy");
        var instance = await StartAsync(t, "governed", "Note this [fake:tool:test__note] [fake:args:{\"text\":\"Hello\"}]");
        await DispatchAsync();
        Assert.Equal("agent_autonomy", await IncidentClassAsync(t, instance));
        Assert.Empty(world.Factory.AgentTools.For(t["operator"].Id));

        // Another tenant sees neither the approval nor can sign it.
        var other = await FreshAsync();
        await OkAsync(other["owner"].Client.GetAsync($"{Processes}/governed/autonomy"), HttpStatusCode.NotFound);
        await ApproveAsync(other, "aip", "governed", "policy", HttpStatusCode.NotFound);
    }
}
