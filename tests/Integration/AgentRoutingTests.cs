using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Routing from the assistant to agents and contextual actions (spec 020 T012, US7; spec 014 T004, US2): only agents
/// the person may use are candidates, the routing model chooses at most one, the person can switch agent or go back to
/// the knowledge agent, the decision is recorded in the run's execution record without the question, a confirmation
/// card reaches the conversation, the kill switch empties the list, and a contextual action reads the item with the
/// person's permissions. Nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentRoutingTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Turns = "/api/v1/assistant/turns";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("rt-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "out", "aip", "sec", "pia" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "out", "Finance");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "emp", BuiltInRoles.ContentEditor, "tenant", null);
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

    /// <summary>Installs a template in Hr as Sara with these tools, submits it and has the AI policy administrator publish it.</summary>
    private async Task<Guid> PublishedAsync(TestTenant t, string template, string[] tools, string autonomy)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = template, orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
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

    private static Task<JsonElement> AskAsync(HttpClient client, object body) => OkAsync(client.PostAsJsonAsync(Turns, body));

    private Task<long> DecisionsAsync(TestTenant t, Guid user) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.route_decision where user_id = @user", ("user", user));

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    [Fact]
    public async Task The_assistant_routes_to_one_agent_the_person_may_use_and_records_the_decision_in_the_run()
    {
        var t = await FreshAsync();
        var hr = await PublishedAsync(t, "hr_assistant", ["knowledge.search", "test.note"], "L2");

        var available = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/available"));
        Assert.Equal(hr, Assert.Single(available.GetProperty("agents").EnumerateArray()).GetProperty("id").GetGuid());

        // The routing model picks agent 1: the HR agent answers, named on the turn, with the agents to switch to.
        var turn = await AskAsync(t["emp"].Client, new { question = "How many leave days do I have? [fake:route:1]" });
        Assert.Equal(AssistantOutcomes.Answered, turn.GetProperty("outcome").GetString());
        Assert.Equal(hr.ToString(), turn.GetProperty("agent").GetString());
        Assert.Equal(hr, turn.GetProperty("answeredBy").GetProperty("id").GetGuid());
        Assert.Single(turn.GetProperty("agents").EnumerateArray());
        var runId = turn.GetProperty("run").GetProperty("id").GetGuid();
        Assert.Equal(hr, turn.GetProperty("run").GetProperty("agentId").GetGuid());

        // The execution record holds the routing decision; the decision holds no question text.
        var record = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{runId}"));
        Assert.Equal(RouteMethods.Model, record.GetProperty("route").GetProperty("method").GetString());
        Assert.Equal(1, record.GetProperty("route").GetProperty("candidates").GetInt32());
        Assert.Equal(hr, record.GetProperty("route").GetProperty("agentId").GetGuid());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from agents.route_decision where reason like '%leave%' or model like '%leave%'"));

        // When no agent fits the model answers 0 and the knowledge agent answers.
        var general = await AskAsync(t["emp"].Client, new { question = "Where is the canteen?" });
        Assert.Equal(JsonValueKind.Null, general.GetProperty("answeredBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, general.GetProperty("agent").ValueKind);
        Assert.Equal(AssistantOutcomes.NoEvidence, general.GetProperty("outcome").GetString());

        // Switching to the agent, and going back to the knowledge agent, are recorded as the person's choice.
        var conversation = general.GetProperty("conversationId").GetGuid();
        var picked = await AskAsync(t["emp"].Client, new { conversationId = conversation, question = "Ask HR then", agentId = hr });
        Assert.Equal(hr, picked.GetProperty("answeredBy").GetProperty("id").GetGuid());
        var back = await AskAsync(t["emp"].Client, new { conversationId = conversation, question = "Never mind [fake:route:1]", knowledge = true });
        Assert.Equal(JsonValueKind.Null, back.GetProperty("answeredBy").ValueKind);
        Assert.Equal(4L, await DecisionsAsync(t, t["emp"].Id));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.route_decision where method = 'knowledge'"));

        // The conversation history names the answering agent.
        var history = await OkAsync(t["emp"].Client.GetAsync($"/api/v1/assistant/conversations/{conversation}"));
        Assert.Contains(history.GetProperty("turns").EnumerateArray(), x => x.GetProperty("agent").GetString() == hr.ToString());

        // The assistant itself gained no tools: nothing ran for the person.
        Assert.Empty(world.Factory.AgentTools.For(t["emp"].Id));
    }

    [Fact]
    public async Task People_outside_the_audience_get_no_routing_and_cannot_pick_the_agent()
    {
        var t = await FreshAsync();
        var hr = await PublishedAsync(t, "hr_assistant", ["knowledge.search"], "L2");

        Assert.Empty((await OkAsync(t["out"].Client.GetAsync($"{Agents}/available"))).GetProperty("agents").EnumerateArray());
        var turn = await AskAsync(t["out"].Client, new { question = "Leave days? [fake:route:1]" });
        Assert.Equal(JsonValueKind.Null, turn.GetProperty("answeredBy").ValueKind);
        Assert.Equal("not_found", await ProblemAsync(t["out"].Client.PostAsJsonAsync(Turns, new { question = "Leave days?", agentId = hr }), HttpStatusCode.NotFound));
        Assert.Equal(0L, await DecisionsAsync(t, t["out"].Id));

        // A suspended agent is no longer a candidate.
        await OkAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{hr}/suspend", new { reason = "Check" }));
        Assert.Empty((await OkAsync(t["emp"].Client.GetAsync($"{Agents}/available"))).GetProperty("agents").EnumerateArray());

        // Another tenant's people never see or reach it.
        var other = await FreshAsync();
        await OkAsync(t["sec"].Client.PostAsync($"{Agents}/{hr}/resume", null));
        Assert.Empty((await OkAsync(other["emp"].Client.GetAsync($"{Agents}/available"))).GetProperty("agents").EnumerateArray());
        Assert.Equal("not_found", await ProblemAsync(other["emp"].Client.PostAsJsonAsync(Turns, new { question = "Leave days?", agentId = hr }), HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task An_agents_action_card_reaches_the_conversation_and_the_kill_switch_empties_routing()
    {
        var t = await FreshAsync();
        await PublishedAsync(t, "hr_assistant", ["test.note"], "L2");

        var turn = await AskAsync(t["emp"].Client, new { question = """Remind me to call HR [fake:route:1] [fake:tool:test__note] [fake:args:{"text":"Call HR"}]""" });
        Assert.Equal(AssistantOutcomes.ActionPending, turn.GetProperty("outcome").GetString());
        var card = turn.GetProperty("run").GetProperty("confirmation");
        Assert.Equal("test.note", card.GetProperty("tool").GetString());
        Assert.Empty(world.Factory.AgentTools.For(t["emp"].Id));

        // A routed turn also streams, as one finished turn.
        using var request = new HttpRequestMessage(HttpMethod.Post, Turns) { Content = JsonContent.Create(new { question = "Leave days? [fake:route:1]" }) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var text = await (await t["emp"].Client.SendAsync(request)).Content.ReadAsStringAsync();
        var done = text.Split("\n\n").Single(e => e.StartsWith("event: done", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(done[(done.IndexOf("data: ", StringComparison.Ordinal) + 6)..]).RootElement.GetProperty("answeredBy").ValueKind);

        var routed = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/route", new { message = "Leave days? [fake:route:1]" }));
        Assert.Equal(RouteMethods.Model, routed.GetProperty("method").GetString());
        Assert.Equal(JsonValueKind.Object, routed.GetProperty("agent").ValueKind);

        await OkAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch", new { active = true, confirmation = $"{t.Key} holding", reason = "Check" }));
        var off = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/route", new { message = "Leave days? [fake:route:1]" }));
        Assert.Equal(JsonValueKind.Null, off.GetProperty("decisionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, off.GetProperty("agent").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await AskAsync(t["emp"].Client, new { question = "Leave days? [fake:route:1]" })).GetProperty("answeredBy").ValueKind);
    }

    [Fact]
    public async Task A_contextual_action_reads_the_item_with_the_persons_permissions_and_cites_it()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t, "enterprise_knowledge", ["knowledge.search"], "L0");
        var variants = new Dictionary<string, object>
        {
            ["en"] = new { title = "Travel policy", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "Book economy for flights under six hours." } } } } } },
        };
        var item = (await OkAsync(t["pia"].Client.PostAsJsonAsync("/api/v1/content/items", new { type = "news", variants }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"/api/v1/content/items/{item}/publish", new { }));
        await DispatchAsync();

        var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "Explain this", itemSource = "content", itemId = item }));
        Assert.Equal(RunStatuses.Completed, run.GetProperty("status").GetString());
        Assert.Contains("[1]", run.GetProperty("answer").GetString());
        Assert.Equal(item, Assert.Single(run.GetProperty("citations").EnumerateArray()).GetProperty("itemId").GetGuid());
        var record = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{run.GetProperty("id").GetGuid()}"));
        Assert.Equal("content", record.GetProperty("itemSource").GetString());
        Assert.Equal(item, record.GetProperty("itemId").GetGuid());

        Assert.Equal("validation.agent_run.item", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "Explain", itemId = item }),
            HttpStatusCode.BadRequest));
        Assert.Equal("not_found", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs",
            new { message = "Explain", itemSource = "content", itemId = Guid.NewGuid() }), HttpStatusCode.NotFound));

        // An item of another tenant is not found, even with the same agent template there.
        var other = await FreshAsync();
        var otherAgent = await PublishedAsync(other, "enterprise_knowledge", ["knowledge.search"], "L0");
        Assert.Equal("not_found", await ProblemAsync(other["emp"].Client.PostAsJsonAsync($"{Agents}/{otherAgent}/runs",
            new { message = "Explain", itemSource = "content", itemId = item }), HttpStatusCode.NotFound));
    }
}
