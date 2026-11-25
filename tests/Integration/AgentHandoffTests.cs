using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 020 R3 T024 (US10, FR-016): an agent hands part of the work to another agent only through a handoff its definition
/// declares. The other agent runs as the same person with its own tools, so permissions never add up across agents; a chain
/// stops at three agents and offers the human contact; Workplace Plus allows only the handoffs of platform templates, and
/// nothing crosses tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentHandoffTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Escalation = "Ask the HR team.";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("ho-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
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

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var body = await OkAsync(call, expected);
        return body.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : body.GetProperty("detailKey").GetString()!;
    }

    private static object Handoff(Guid to) =>
        new { agent = to, topic = new Dictionary<string, string> { ["en"] = "Leave questions", ["ar"] = "أسئلة الإجازات" } };

    private static Dictionary<string, object?> Definition(JsonElement detail, string[] tools, object[]? handoffs)
    {
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = "L2";
        definition["escalation"] = new Dictionary<string, string> { ["en"] = Escalation, ["ar"] = "اسأل فريق الموارد البشرية." };
        if (handoffs is not null)
        {
            definition["handoffs"] = handoffs;
        }
        return definition;
    }

    /// <summary>Installs a template in Hr as Sara with the given tools and handoffs, and publishes version 1.</summary>
    private async Task<Guid> AgentAsync(TestTenant t, string[] tools, params Guid[] handoffs)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = Definition(detail, tools, handoffs.Length == 0 ? null : handoffs.Select(Handoff).ToArray());
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static string Call(string tool, object args) => $"[fake:tool:{tool.Replace(".", "__")}] [fake:args:{JsonSerializer.Serialize(args)}]";

    /// <summary>A message that hands the request to <paramref name="to"/>; the request carries what the next agent's model does.</summary>
    private static string HandTo(Guid to, string request) => "Please pass this on " + Call("agent.handoff", new { to = to.ToString(), request });

    private static Task<JsonElement> RunAsync(TestTenant t, Guid agent, string message) =>
        OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message }));

    private static async Task<List<(string Kind, string? Tool, string Decision, string? Reason)>> StepsAsync(TestTenant t, Guid run) =>
        (await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{run}"))).GetProperty("steps").EnumerateArray()
            .Select(s => (s.GetProperty("kind").GetString()!, s.GetProperty("tool").GetString(), s.GetProperty("decision").GetString()!, s.GetProperty("reason").GetString()))
            .ToList();

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    [Fact]
    public async Task A_declared_handoff_runs_the_other_agent_as_the_same_person_with_its_own_tools()
    {
        var t = await FreshAsync();
        var leave = await AgentAsync(t, ["knowledge.search"]);
        var front = await AgentAsync(t, ["knowledge.search", "test.note"], leave);

        var run = await RunAsync(t, front, HandTo(leave, "Find the leave policy " + Call("knowledge.search", new { query = "leave policy" })));
        Assert.Equal(leave, run.GetProperty("agentId").GetGuid());
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var from = run.GetProperty("handedOffFrom");
        Assert.Equal(front, from.GetProperty("agentId").GetGuid());
        Assert.False(string.IsNullOrEmpty(from.GetProperty("toNames").GetProperty("ar").GetString()));
        var parent = from.GetProperty("runId").GetGuid();
        var child = run.GetProperty("id").GetGuid();

        // The first run ends as handed off; the second points at it, one level deeper, and searched with its own tool.
        Assert.Equal("handed_off", await world.Database.TenantScalarAsync<string>(t.Id, "select outcome from agents.run where id = @id", ("id", parent)));
        Assert.Equal(parent, await world.Database.TenantScalarAsync<Guid>(t.Id, "select parent_run_id from agents.run where id = @id", ("id", child)));
        Assert.Equal(2, await world.Database.TenantScalarAsync<int>(t.Id, "select depth from agents.run where id = @id", ("id", child)));
        Assert.Equal(t["emp"].Id, await world.Database.TenantScalarAsync<Guid>(t.Id, "select user_id from agents.run where id = @id", ("id", child)));
        Assert.Contains(("tool", "agent.handoff", "executed", null), await StepsAsync(t, parent));
        Assert.Contains(("tool", "knowledge.search", "executed", null), await StepsAsync(t, child));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentHandoff));

        // Permissions do not add up: the first agent may note, the second may not, so the second cannot note after a handoff.
        var noted = await RunAsync(t, front, HandTo(leave, "Note it " + Call("test.note", new { text = "Leave on Monday" })));
        Assert.Equal(leave, noted.GetProperty("agentId").GetGuid());
        var steps = await StepsAsync(t, noted.GetProperty("id").GetGuid());
        // The second agent is never offered the note tool, so any call of it is dropped before a tool runs.
        Assert.DoesNotContain(steps, s => s.Kind == "tool" && s.Decision is "executed" or "confirmation_requested");
    }

    [Fact]
    public async Task An_undeclared_handoff_is_refused_and_the_first_agent_answers()
    {
        var t = await FreshAsync();
        var other = await AgentAsync(t, ["knowledge.search"]);
        var declared = await AgentAsync(t, ["knowledge.search"]);
        var front = await AgentAsync(t, ["knowledge.search"], declared);

        var run = await RunAsync(t, front, HandTo(other, "Find the leave policy"));
        Assert.Equal(front, run.GetProperty("agentId").GetGuid());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("handedOffFrom").ValueKind);
        Assert.Contains(("tool", "agent.handoff", "refused", "tool.handoff_not_declared"), await StepsAsync(t, run.GetProperty("id").GetGuid()));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.run where agent_id = @id", ("id", other)));
    }

    [Fact]
    public async Task A_chain_stops_at_three_agents_and_offers_the_human_contact()
    {
        var t = await FreshAsync();
        var fourth = await AgentAsync(t, ["knowledge.search"]);
        var third = await AgentAsync(t, ["knowledge.search"], fourth);
        var second = await AgentAsync(t, ["knowledge.search"], third);
        var first = await AgentAsync(t, ["knowledge.search"], second);

        var run = await RunAsync(t, first, HandTo(second, HandTo(third, HandTo(fourth, "Find the leave policy"))));
        Assert.Equal(third, run.GetProperty("agentId").GetGuid());
        // The person sees the agent they asked and the agent that answered.
        Assert.Equal(first, run.GetProperty("handedOffFrom").GetProperty("agentId").GetGuid());
        Assert.Contains(("tool", "agent.handoff", "refused", "tool.handoff_depth"), await StepsAsync(t, run.GetProperty("id").GetGuid()));
        Assert.Contains(Escalation, run.GetProperty("answer").GetString());
        Assert.Equal(3, await world.Database.TenantScalarAsync<int>(t.Id, "select depth from agents.run where id = @id", ("id", run.GetProperty("id").GetGuid())));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.run where agent_id = @id", ("id", fourth)));
    }

    [Fact]
    public async Task Workplace_plus_refuses_a_handoff_a_tenant_added_and_no_handoff_crosses_tenants()
    {
        var plus = await FreshAsync("workplace_plus");
        var target = await AgentAsync(plus, ["knowledge.search"]);
        var front = await AgentAsync(plus, ["knowledge.search"], target);
        var run = await RunAsync(plus, front, HandTo(target, "Find the leave policy"));
        Assert.Equal(front, run.GetProperty("agentId").GetGuid());
        Assert.Contains(("tool", "agent.handoff", "refused", "tool.not_in_package"), await StepsAsync(plus, run.GetProperty("id").GetGuid()));

        // An agent of another tenant is never found, even when its ID is declared.
        var gov = await FreshAsync();
        var foreign = await AgentAsync(gov, ["knowledge.search"], target);
        var crossed = await RunAsync(gov, foreign, HandTo(target, "Find the leave policy"));
        Assert.Equal(foreign, crossed.GetProperty("agentId").GetGuid());
        Assert.Contains(("tool", "agent.handoff", "refused", "tool.handoff_unavailable"), await StepsAsync(gov, crossed.GetProperty("id").GetGuid()));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(plus.Id, "select count(*) from agents.run where agent_id = @id and depth > 1", ("id", target)));
    }

    [Fact]
    public async Task A_handoff_needs_one_target_and_a_topic_in_every_language()
    {
        var t = await FreshAsync();
        var target = await AgentAsync(t, ["knowledge.search"]);
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var both = Definition(detail, ["knowledge.search"],
            [new { template = "enterprise_knowledge", agent = target, topic = new Dictionary<string, string> { ["en"] = "Leave", ["ar"] = "الإجازات" } }]);
        Assert.Equal("validation.agent.handoff_target", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition = both }), HttpStatusCode.BadRequest));
        var englishOnly = Definition(detail, ["knowledge.search"], [new { agent = target, topic = new Dictionary<string, string> { ["en"] = "Leave" } }]);
        Assert.Equal("validation.agent.handoff_topic", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition = englishOnly }), HttpStatusCode.BadRequest));
        var many = Definition(detail, ["knowledge.search"], Enumerable.Range(0, 6).Select(_ => Handoff(Guid.NewGuid())).ToArray());
        Assert.Equal("validation.agent.handoffs", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition = many }), HttpStatusCode.BadRequest));
    }
}
