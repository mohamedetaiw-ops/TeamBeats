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
/// Spec 034 R2 T022 (US9 scenario 1, FR-027): per agent, completion, escalation, resolution without human help and time
/// saved against the tenant's baseline, shown only when at least the minimum group size of people ran it, never per
/// person; baselines are set by the AI Policy Administrator; readers see only agents whose evaluation they may read;
/// packages without business outcomes and other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OutcomeScoringTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";
    private static readonly string[] People = ["e1", "e2", "e3", "e4", "e5"];

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("out-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "aip", "fin" }.Concat(People))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        foreach (var name in People)
        {
            await world.AddMembershipAsync(t, name, "Hr");
        }
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fin", BuiltInRoles.AgentDesigner, "org_subtree", finance);
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
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()! : body.GetProperty("code").GetString()!;
    }

    private async Task<Guid> AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        definition["autonomy"] = "L0";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private async Task<Guid> RunAsync(TestTenant t, Guid agent, string user)
    {
        var run = await OkAsync(t[user].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "How many leave days do I get?" }));
        // The figures count answered runs; make this one answered whatever the fake model said, as the test is about counting.
        var id = run.GetProperty("id").GetGuid();
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set status = 'completed', outcome = 'answered' where id = @id returning 1", ("id", id));
        return id;
    }

    private static JsonElement Row(JsonElement view, Guid agent) =>
        view.GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("agentId").GetGuid() == agent);

    [Fact]
    public async Task Outcomes_show_from_the_minimum_group_size_and_count_escalations_human_help_and_time_saved_against_the_baseline()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t);

        // Four people are below the minimum group size of five: nothing is shown about them.
        foreach (var person in People.Take(4))
        {
            await RunAsync(t, agent, person);
        }
        var few = Row(await OkAsync(t["aip"].Client.GetAsync($"{Eval}/outcomes")), agent);
        Assert.True(few.GetProperty("withheld").GetBoolean());
        Assert.Equal(JsonValueKind.Null, few.GetProperty("runs").ValueKind);
        Assert.Equal(JsonValueKind.Null, few.GetProperty("completionRate").ValueKind);

        // A fifth person; one run escalated, and one answered run the person rated not helpful.
        var fifth = await RunAsync(t, agent, "e5");
        var escalated = await RunAsync(t, agent, "e1");
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set status = 'stopped', outcome = 'limit_reached' where id = @id returning 1", ("id", escalated));
        await world.Database.TenantScalarAsync<int>(t.Id,
            """
            with c as (insert into agents.conversation (tenant_id, id, user_id, title, classification, turns, created_at, last_turn_at)
              values (platform.current_tenant_id(), @conv, @user, 'Leave', 'internal', 1, now(), now()) returning id)
            insert into agents.turn (tenant_id, id, conversation_id, user_id, position, question, answer, outcome, language, classification, prompt_version, retrieval,
              run_id, created_at)
            select platform.current_tenant_id(), @turn, c.id, @user, 1, 'How many leave days?', 'Annual leave is 25 days.', 'answered', 'en', 'internal', 'v1', '{}', @run, now()
            from c returning 1
            """, ("conv", Guid.NewGuid()), ("turn", Guid.NewGuid()), ("user", t["e5"].Id), ("run", fifth));
        var turn = await world.Database.TenantScalarAsync<Guid>(t.Id, "select id from agents.turn where run_id = @run", ("run", fifth));
        await OkAsync(t["e5"].Client.PostAsJsonAsync($"/api/v1/assistant/turns/{turn}/feedback", new { helpful = false }), HttpStatusCode.NoContent);

        var shown = Row(await OkAsync(t["aip"].Client.GetAsync($"{Eval}/outcomes")), agent);
        Assert.False(shown.GetProperty("withheld").GetBoolean());
        Assert.Equal(6, shown.GetProperty("runs").GetInt64());
        Assert.Equal(Math.Round(5 / 6.0, 4), shown.GetProperty("completionRate").GetDouble());
        Assert.Equal(Math.Round(1 / 6.0, 4), shown.GetProperty("escalationRate").GetDouble());
        Assert.Equal(Math.Round(4 / 6.0, 4), shown.GetProperty("resolvedWithoutHelpRate").GetDouble());
        // Without a baseline there is no time saved.
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("hoursSaved").ValueKind);

        // The AI Policy Administrator sets the baseline; a designer cannot; the values are checked.
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{agent}", new { taskNames = new { en = "Answer a leave question" }, minutes = 12 }),
            HttpStatusCode.Forbidden);
        Assert.Equal("validation.eval.outcome_minutes", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{agent}",
            new { taskNames = new { en = "Answer a leave question" }, minutes = 0 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.outcome_task", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{agent}",
            new { minutes = 12 }), HttpStatusCode.BadRequest));
        var saved = Row(await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{agent}",
            new { taskNames = new { en = "Answer a leave question", ar = "الإجابة عن سؤال إجازة" }, minutes = 12 })), agent);
        Assert.Equal(12, saved.GetProperty("baseline").GetProperty("minutes").GetDouble());
        // Four runs resolved without help at 12 minutes each.
        Assert.Equal(0.8, saved.GetProperty("hoursSaved").GetDouble());
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.EvalOutcomeBaselineChanged)));

        // The designer of the agent's unit reads it; a designer of another unit and an employee see no agent; no person is named.
        var designer = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/outcomes"));
        Assert.False(designer.GetProperty("canEdit").GetBoolean());
        Assert.Single(designer.GetProperty("agents").EnumerateArray());
        Assert.Empty((await OkAsync(t["fin"].Client.GetAsync($"{Eval}/outcomes"))).GetProperty("agents").EnumerateArray());
        Assert.Empty((await OkAsync(t["e1"].Client.GetAsync($"{Eval}/outcomes"))).GetProperty("agents").EnumerateArray());
        Assert.DoesNotContain(t["e1"].Id.ToString(), designer.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Removing the baseline removes the time saved.
        var removed = Row(await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{agent}", new { })), agent);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("baseline").ValueKind);
        Assert.Equal("validation.eval.outcome_days", await ProblemAsync(t["aip"].Client.GetAsync($"{Eval}/outcomes?days=400"), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Packages_without_business_outcomes_and_other_tenants_see_nothing()
    {
        var basic = await FreshAsync("workplace");
        var outcomes = await OkAsync(basic["aip"].Client.GetAsync($"{Eval}/outcomes"));
        Assert.False(outcomes.GetProperty("available").GetBoolean());
        await OkAsync(basic["aip"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{Guid.NewGuid()}", new { taskNames = new { en = "Task" }, minutes = 5 }),
            HttpStatusCode.Forbidden);

        var t = await FreshAsync();
        var agent = await AgentAsync(t);
        var other = await FreshAsync();
        Assert.Empty((await OkAsync(other["aip"].Client.GetAsync($"{Eval}/outcomes"))).GetProperty("agents").EnumerateArray());
        await OkAsync(other["aip"].Client.PutAsJsonAsync($"{Eval}/outcomes/baselines/{agent}", new { taskNames = new { en = "Task" }, minutes = 5 }), HttpStatusCode.NotFound);
    }
}
