using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R3 (with spec 027 US1 scenario 5): every L4 agent task of an approved process is evaluated end to end with
/// deterministic trajectory checks, an action that needed an approver fails the step as critical even though it was
/// refused, the instance outcome closes the process view, results follow the viewer's units, and nothing crosses
/// tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationProcessRunTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Processes = "/api/v1/workflow/processes";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("l4e-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "owner", "aip", "both", "operator", "sec", "rev", "farrev" })
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
        // Agent designers hold eval.design in their units: one over the agent's unit, one over another unit only.
        var far = await world.CreateUnitAsync(t, "Far", null, "subsidiary");
        await world.AssignAsync(t, "rev", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "farrev", BuiltInRoles.AgentDesigner, "org_subtree", far);
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

    private async Task<(TestTenant T, Guid Agent)> ApprovedAsync(string key)
    {
        var t = await FreshAsync();
        await AllowL4Async(t);
        var agent = await AgentAsync(t);
        await PublishAsync(t, key, Model(agent));
        await ApproveAsync(t, "owner", key, "owner");
        await ApproveAsync(t, "aip", key, "policy");
        return (t, agent);
    }

    private static Task<JsonElement> ViewAsync(TestTenant t, string user, string key) => OkAsync(t[user].Client.GetAsync($"/api/v1/evaluation/processes/{key}"));

    [Fact]
    public async Task Each_l4_step_is_scored_and_an_action_needing_an_approver_fails_it_as_critical()
    {
        var (t, agent) = await ApprovedAsync("welcome");
        var good = await StartAsync(t, "welcome", "Note this [fake:tool:test__note] [fake:args:{\"text\":\"Welcome Lina\"}]");
        await DispatchAsync();
        var bad = await StartAsync(t, "welcome", "Order [fake:tool:test__order] [fake:args:{\"item\":\"Desk lamp\",\"amount\":40}]");
        await DispatchAsync();
        Assert.Equal("completed", await StatusAsync(t, good));
        Assert.Equal("completed", await StatusAsync(t, bad));

        var view = await ViewAsync(t, "rev", "welcome");
        var version = Assert.Single(view.GetProperty("versions").EnumerateArray());
        Assert.Equal(1, version.GetProperty("version").GetInt32());
        Assert.Equal(2, version.GetProperty("instances").GetInt32());
        Assert.Equal(2, version.GetProperty("closed").GetInt32());
        Assert.Equal(2, version.GetProperty("completedInstances").GetInt32());
        Assert.Equal(2, version.GetProperty("steps").GetInt32());
        Assert.Equal(1, version.GetProperty("passedSteps").GetInt32());
        Assert.Equal(1, version.GetProperty("criticalSteps").GetInt32());
        Assert.Equal(0.5, version.GetProperty("rates").GetProperty(ProcessStepRules.NoApprovalNeeded).GetDouble());
        Assert.Equal(1.0, version.GetProperty("rates").GetProperty(ProcessStepRules.ScopeHeld).GetDouble());

        var steps = view.GetProperty("recent").EnumerateArray().ToList();
        var failed = steps.Single(s => s.GetProperty("instanceId").GetGuid() == bad);
        Assert.True(failed.GetProperty("critical").GetBoolean());
        Assert.False(failed.GetProperty("metrics").GetProperty(ProcessStepRules.NoApprovalNeeded).GetBoolean());
        Assert.Equal("completed", failed.GetProperty("outcome").GetString());
        var passed = steps.Single(s => s.GetProperty("instanceId").GetGuid() == good);
        Assert.True(passed.GetProperty("passed").GetBoolean());
        Assert.Equal(agent, passed.GetProperty("agentId").GetGuid());
        Assert.Equal(1, passed.GetProperty("toolCalls").GetInt32());

        // The step is recorded once, even when its facts come again; no content is kept.
        var row = await world.Database.ScalarInTenantAsync<string>(t.Id, $"select to_jsonb(s)::text from evaluation.process_step_eval s where instance_id = '{good}'");
        Assert.DoesNotContain("Welcome Lina", row);
    }

    [Fact]
    public async Task A_limit_breach_fails_the_step_and_a_refused_run_is_still_recorded()
    {
        var (t, _) = await ApprovedAsync("looping");
        var instance = await StartAsync(t, "looping", "Keep noting [fake:loop] [fake:tool:test__note] [fake:args:{\"text\":\"Again\"}]");
        await DispatchAsync();
        var step = Assert.Single((await ViewAsync(t, "rev", "looping")).GetProperty("recent").EnumerateArray());
        Assert.Equal(instance, step.GetProperty("instanceId").GetGuid());
        Assert.Equal("failed", step.GetProperty("outcome").GetString());
        Assert.StartsWith("limit_", step.GetProperty("reason").GetString());
        Assert.False(step.GetProperty("metrics").GetProperty(ProcessStepRules.WithinLimit).GetBoolean());
        Assert.False(step.GetProperty("metrics").GetProperty(ProcessStepRules.Completed).GetBoolean());
        Assert.False(step.GetProperty("critical").GetBoolean());
        // The instance waits on an incident, so it is not closed.
        Assert.Equal(0, (await ViewAsync(t, "rev", "looping")).GetProperty("versions")[0].GetProperty("closed").GetInt32());

        // A run refused before it starts (the agent was suspended) is recorded without a run.
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent set status = 'suspended' returning 1");
        await StartAsync(t, "looping", "Note this");
        await DispatchAsync();
        var refused = (await ViewAsync(t, "rev", "looping")).GetProperty("recent").EnumerateArray().First();
        Assert.Equal(JsonValueKind.Null, refused.GetProperty("agentRunId").ValueKind);
        Assert.Equal("failed", refused.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Results_follow_the_viewers_units_and_never_cross_tenants()
    {
        var (t, _) = await ApprovedAsync("welcome");
        await StartAsync(t, "welcome", "Note this [fake:tool:test__note] [fake:args:{\"text\":\"Welcome Omar\"}]");
        await DispatchAsync();
        Assert.Single((await ViewAsync(t, "rev", "welcome")).GetProperty("recent").EnumerateArray());
        // A reviewer of another unit and a person without an evaluation permission see nothing of the agent's steps.
        Assert.Empty((await ViewAsync(t, "farrev", "welcome")).GetProperty("versions").EnumerateArray());
        Assert.Empty((await ViewAsync(t, "operator", "welcome")).GetProperty("recent").EnumerateArray());
        // Another tenant with a process of the same key sees nothing of this one.
        var other = await FreshAsync();
        Assert.Empty((await ViewAsync(other, "rev", "welcome")).GetProperty("versions").EnumerateArray());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from evaluation.process_step_eval"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from evaluation.process_step_eval"));
    }

    [Fact]
    public void The_trajectory_rules_mark_a_call_outside_the_step_as_critical()
    {
        AgentTaskRunRecorded Step(int scope = 0, int approval = 0, int refused = 0, string outcome = "completed", string? reason = null, int ms = 1000) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "p", 1, "agent", Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), outcome, reason, 1, refused, scope, approval, ms, 10);
        Assert.Equal((true, false), (ProcessStepRules.Score(Step()).Passed, ProcessStepRules.Score(Step()).Critical));
        var outside = ProcessStepRules.Score(Step(scope: 1, refused: 1));
        Assert.True(outside.Critical);
        Assert.False(outside.Passed);
        Assert.False(outside.Metrics[ProcessStepRules.ScopeHeld]);
        Assert.False(ProcessStepRules.Score(Step(ms: 10 * 60_000 + 1)).Metrics[ProcessStepRules.WithinLimit]);
        Assert.False(ProcessStepRules.Score(Step(refused: 1)).Critical);
    }
}
