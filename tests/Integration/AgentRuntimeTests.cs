using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// The agent runtime and tool gateway (spec 020 T004, T005; spec 027 T004 to T006, T009, T010): runs only for people in
/// the audience of a published agent, cited answers from what the person may read, L0 never writes, L2 waits for the
/// person to confirm exactly the parameters on the card, a revoked permission or a suspended agent refused at
/// execution, the test panel never writes, schema errors and run limits, execution records for the user and security
/// officers in scope, and nothing across tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentRuntimeTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "out", "sec", "pia", "fadi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "out", "Finance");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", finance);
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

    /// <summary>Installs a template in Hr as Sara, saves it with the given tools and autonomy, and optionally publishes version 1.</summary>
    private async Task<Guid> AgentAsync(TestTenant t, string template, string[] tools, string autonomy, bool publish = true, int? maxToolCalls = null)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = template, orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = autonomy;
        if (maxToolCalls is { } calls)
        {
            definition["maxToolCalls"] = calls;
        }
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        if (publish)
        {
            // Review and publishing come with the lifecycle slice; until then the version is published directly.
            await world.Database.TenantScalarAsync<int>(t.Id,
                """
                with v as (update agents.agent_version set state = 'published', published_at = now() where agent_id = @id and number = 1 returning 1)
                update agents.agent set status = 'published', current_version = 1 where id = @id returning 1
                """, ("id", id));
        }
        return id;
    }

    private Task SetStatusAsync(TestTenant t, Guid id, string status) =>
        world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent set status = @status where id = @id returning 1", ("id", id), ("status", status));

    private static Task<JsonElement> RunAsync(HttpClient client, Guid agent, string message) =>
        OkAsync(client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message }));

    private static string Call(string tool, string? args = null) => $"[fake:tool:{tool.Replace(".", "__")}]" + (args is null ? string.Empty : $" [fake:args:{args}]");

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    private static List<(string Kind, string? Tool, string Decision, string? Reason)> Steps(JsonElement record) =>
        record.GetProperty("steps").EnumerateArray().Select(s => (s.GetProperty("kind").GetString()!, s.GetProperty("tool").GetString(), s.GetProperty("decision").GetString()!,
            s.GetProperty("reason").GetString())).ToList();

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task NewsAsync(HttpClient client, string title, string body, string? classification = null)
    {
        var variants = new Dictionary<string, object>
        {
            ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = body } } } } } },
        };
        var id = (await OkAsync(client.PostAsJsonAsync("/api/v1/content/items", new { type = "news", classification, variants }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
    }

    [Fact]
    public async Task A_published_agent_answers_its_audience_with_citations_and_leaves_an_execution_record()
    {
        var t = await FreshAsync();
        await NewsAsync(t["pia"].Client, "Forklift inspection checklist", "Check the forklift brakes, horn and mast chains before every shift.");
        await NewsAsync(t["pia"].Client, "Forklift bonus pool", "Confidential forklift bonus pool for the sales directors.", "confidential");
        await DispatchAsync();
        var agent = await AgentAsync(t, "enterprise_knowledge", ["knowledge.search"], "L0");

        var run = await RunAsync(t["emp"].Client, agent, "What do I check on a forklift? " + Call("knowledge.search", """{"query":"forklift"}"""));
        Assert.Equal(RunStatuses.Completed, run.GetProperty("status").GetString());
        Assert.Equal(RunOutcomes.Answered, run.GetProperty("outcome").GetString());
        Assert.Contains("[1]", run.GetProperty("answer").GetString());
        Assert.True(run.GetProperty("generated").GetBoolean());
        var citation = Assert.Single(run.GetProperty("citations").EnumerateArray());
        Assert.Equal("Forklift inspection checklist", citation.GetProperty("title").GetString());

        // The record: model turn, the tool call, the answer; metadata only, no question or answer text.
        var id = run.GetProperty("id").GetGuid();
        var record = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{id}"));
        Assert.Equal([("model", null, "executed", null), ("tool", "knowledge.search", "executed", null), ("model", null, "executed", null)],
            Steps(record).Select(s => (s.Kind, s.Tool, s.Decision, s.Reason)));
        Assert.Equal(1, record.GetProperty("run").GetProperty("toolCalls").GetInt32());
        Assert.NotNull(record.GetProperty("run").GetProperty("model").GetString());
        Assert.Single(record.GetProperty("sources").EnumerateArray());
        Assert.Equal("internal", await world.Database.TenantScalarAsync<string>(t.Id, "select classification from agents.run where id = @id", ("id", id)));

        // Security officers find it; a designer of another unit and other employees do not.
        var found = await OkAsync(t["sec"].Client.GetAsync($"{Agents}/runs?agent={agent}&tool=knowledge.search"));
        Assert.Equal([id], found.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        await OkAsync(t["sec"].Client.GetAsync($"{Agents}/runs/{id}"));
        Assert.Equal("not_found", await ProblemAsync(t["fadi"].Client.GetAsync($"{Agents}/runs/{id}"), HttpStatusCode.NotFound));
        Assert.Empty((await OkAsync(t["out"].Client.GetAsync($"{Agents}/runs"))).GetProperty("runs").EnumerateArray());

        // Only the audience can use the agent; nobody learns it exists otherwise, and never across tenants.
        Assert.Equal("not_found", await ProblemAsync(t["out"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "Hello" }), HttpStatusCode.NotFound));
        var other = await FreshAsync();
        Assert.Equal("not_found", await ProblemAsync(other["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "Hello" }), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(other["admin"].Client.GetAsync($"{Agents}/runs/{id}"), HttpStatusCode.NotFound));
        Assert.Equal("validation.agent_run.message", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = " " }), HttpStatusCode.BadRequest));

        // A suspended agent is unavailable at once; an unpublished one is not found.
        await SetStatusAsync(t, agent, AgentStatuses.Suspended);
        Assert.Equal("conflict.agent.suspended", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "Hello" }), HttpStatusCode.Conflict));
        var draft = await AgentAsync(t, "enterprise_knowledge", ["knowledge.search"], "L0", publish: false);
        Assert.Equal("not_found", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{draft}/runs", new { message = "Hello" }), HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task An_L2_action_waits_for_the_person_to_confirm_exactly_the_parameters_on_the_card()
    {
        var t = await FreshAsync();
        var editor = await world.AssignAsync(t, "emp", BuiltInRoles.ContentEditor, "tenant", null);
        var agent = await AgentAsync(t, "hr_assistant", ["knowledge.search", "test.note"], "L2");
        var log = world.Factory.AgentTools;

        var run = await RunAsync(t["emp"].Client, agent, "Remind me to call HR " + Call("test.note", """{"text":"Call HR on Monday"}"""));
        Assert.Equal(RunStatuses.Waiting, run.GetProperty("status").GetString());
        var card = run.GetProperty("confirmation");
        Assert.Equal("test.note", card.GetProperty("tool").GetString());
        Assert.Equal("write_self", card.GetProperty("sideEffect").GetString());
        Assert.Equal("Call HR on Monday", card.GetProperty("parameters").GetProperty("text").GetString());
        Assert.Empty(log.For(t["emp"].Id));
        var id = run.GetProperty("id").GetGuid();
        var confirm = $"{Agents}/runs/{id}/confirmations/{card.GetProperty("id").GetGuid()}";
        var hash = card.GetProperty("hash").GetString();

        // Another person cannot confirm it, and a card whose hash does not match is refused.
        Assert.Equal("not_found", await ProblemAsync(t["out"].Client.PostAsJsonAsync(confirm, new { confirm = true, hash }), HttpStatusCode.NotFound));
        Assert.Equal("conflict.agent_run.parameters_changed",
            await ProblemAsync(t["emp"].Client.PostAsJsonAsync(confirm, new { confirm = true, hash = "0000" }), HttpStatusCode.Conflict));

        var done = await OkAsync(t["emp"].Client.PostAsJsonAsync(confirm, new { confirm = true, hash }));
        Assert.Equal(RunStatuses.Completed, done.GetProperty("status").GetString());
        var call = Assert.Single(log.For(t["emp"].Id));
        Assert.Equal("Call HR on Monday", JsonDocument.Parse(call.Arguments).RootElement.GetProperty("text").GetString());
        Assert.Equal(hash, call.Key);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentActionConfirmed));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentToolCalled));
        // Confirming twice does nothing more.
        Assert.Equal("not_found", await ProblemAsync(t["emp"].Client.PostAsJsonAsync(confirm, new { confirm = true, hash }), HttpStatusCode.NotFound));
        var steps = Steps(await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{id}")));
        Assert.Contains(("confirmation", "test.note", "confirmation_requested", null), steps);
        Assert.Contains(("tool", "test.note", "confirmed", null), steps);

        // Cancelling runs nothing.
        var second = await RunAsync(t["emp"].Client, agent, "Note this " + Call("test.note", """{"text":"Second"}"""));
        var cancel = await OkAsync(t["emp"].Client.PostAsJsonAsync(
            $"{Agents}/runs/{second.GetProperty("id").GetGuid()}/confirmations/{second.GetProperty("confirmation").GetProperty("id").GetGuid()}", new { confirm = false }));
        Assert.Equal(RunStatuses.Completed, cancel.GetProperty("status").GetString());
        Assert.Single(log.For(t["emp"].Id));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentActionRejected));

        // The person can stop a waiting run.
        var third = await RunAsync(t["emp"].Client, agent, "Note this " + Call("test.note", """{"text":"Third"}"""));
        var stopped = await OkAsync(t["emp"].Client.PostAsync($"{Agents}/runs/{third.GetProperty("id").GetGuid()}/stop", null));
        Assert.Equal(RunOutcomes.Stopped, stopped.GetProperty("outcome").GetString());

        // A permission revoked between the card and the confirmation is refused at execution (027 US2 scenario 3).
        var fourth = await RunAsync(t["emp"].Client, agent, "Note this " + Call("test.note", """{"text":"Fourth"}"""));
        (await t["admin"].Client.DeleteAsync($"/api/v1/authz/assignments/{editor}")).EnsureSuccessStatusCode();
        var refused = await OkAsync(t["emp"].Client.PostAsJsonAsync(
            $"{Agents}/runs/{fourth.GetProperty("id").GetGuid()}/confirmations/{fourth.GetProperty("confirmation").GetProperty("id").GetGuid()}",
            new { confirm = true, hash = fourth.GetProperty("confirmation").GetProperty("hash").GetString() }));
        Assert.Single(log.For(t["emp"].Id));
        Assert.Contains(("tool", "test.note", "refused", "tool.denied"), Steps(await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{refused.GetProperty("id").GetGuid()}"))));

        // An agent suspended while a card waits does not act.
        await world.AssignAsync(t, "emp", BuiltInRoles.ContentEditor, "tenant", null);
        var fifth = await RunAsync(t["emp"].Client, agent, "Note this " + Call("test.note", """{"text":"Fifth"}"""));
        await SetStatusAsync(t, agent, AgentStatuses.Suspended);
        var suspended = await OkAsync(t["emp"].Client.PostAsJsonAsync(
            $"{Agents}/runs/{fifth.GetProperty("id").GetGuid()}/confirmations/{fifth.GetProperty("confirmation").GetProperty("id").GetGuid()}",
            new { confirm = true, hash = fifth.GetProperty("confirmation").GetProperty("hash").GetString() }));
        Assert.Equal(RunOutcomes.Suspended, suspended.GetProperty("outcome").GetString());
        Assert.Single(log.For(t["emp"].Id));
    }

    [Fact]
    public async Task The_gateway_refuses_tools_the_agent_does_not_hold_bad_parameters_and_calls_past_the_limit()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t, "enterprise_knowledge", ["knowledge.search"], "L0", maxToolCalls: 1);

        // The model asks for a write tool the L0 agent was never given: the AI gateway drops it, nothing runs.
        var write = await RunAsync(t["emp"].Client, agent, "Write it down " + Call("test.note", """{"text":"x"}"""));
        Assert.Equal(RunOutcomes.Answered, write.GetProperty("outcome").GetString());
        Assert.Empty(world.Factory.AgentTools.For(t["emp"].Id));

        // Parameters that break the schema get a short error and the run goes on.
        var bad = await RunAsync(t["emp"].Client, agent, "Search " + Call("knowledge.search", """{"query":5,"extra":true}"""));
        Assert.Equal(RunOutcomes.Answered, bad.GetProperty("outcome").GetString());
        var steps = Steps(await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{bad.GetProperty("id").GetGuid()}")));
        Assert.Contains(("tool", "knowledge.search", "refused", "tool.invalid_arguments"), steps);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentToolDenied));

        // The model keeps calling: the run stops at the agent's limit of one call.
        var loop = await RunAsync(t["emp"].Client, agent, "Search again [fake:loop] " + Call("knowledge.search", """{"query":"forklift"}"""));
        Assert.Equal(RunStatuses.Stopped, loop.GetProperty("status").GetString());
        Assert.Equal(RunOutcomes.LimitReached, loop.GetProperty("outcome").GetString());
        Assert.Equal("tool_calls", loop.GetProperty("reason").GetString());

        // Provider failures end the run as unavailable, with the reason.
        var down = await RunAsync(t["emp"].Client, agent, "Hello [fake:500]");
        Assert.Equal(RunOutcomes.Unavailable, down.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_test_panel_runs_the_draft_as_the_designer_and_never_executes_a_write()
    {
        var t = await FreshAsync();
        await world.AssignAsync(t, "sara", BuiltInRoles.ContentEditor, "tenant", null);
        var agent = await AgentAsync(t, "hr_assistant", ["knowledge.search", "test.note", "test.draft"], "L2", publish: false);

        var run = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = "Save it " + Call("test.note", """{"text":"Test"}""") }));
        Assert.Equal("test", run.GetProperty("mode").GetString());
        Assert.Equal(RunStatuses.Completed, run.GetProperty("status").GetString());
        Assert.Empty(world.Factory.AgentTools.For(t["sara"].Id));
        Assert.Contains(("tool", "test.note", "would_call", "tool.test_mode"),
            Steps(await OkAsync(t["sara"].Client.GetAsync($"{Agents}/runs/{run.GetProperty("id").GetGuid()}"))));

        // Draft tools run in the test panel: they change nothing.
        var drafted = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = "Draft it " + Call("test.draft", """{"topic":"leave"}""") }));
        Assert.Equal(RunOutcomes.Answered, drafted.GetProperty("outcome").GetString());
        Assert.Single(world.Factory.AgentTools.For(t["sara"].Id));

        // Only designers of the agent's unit test it; overseers are refused, others do not find it.
        Assert.Equal("authz.denied", await ProblemAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = "Hi" }), HttpStatusCode.Forbidden));
        Assert.Equal("not_found", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = "Hi" }), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(t["fadi"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = "Hi" }), HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Old_records_and_expired_cards_are_cleaned_up_by_retention()
    {
        var t = await FreshAsync();
        await world.AssignAsync(t, "emp", BuiltInRoles.ContentEditor, "tenant", null);
        var agent = await AgentAsync(t, "hr_assistant", ["test.note"], "L2");
        var waiting = await RunAsync(t["emp"].Client, agent, "Note " + Call("test.note", """{"text":"Later"}"""));
        var id = waiting.GetProperty("id").GetGuid();

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AgentRuns>().PurgeAsync(DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal("expired", await world.Database.TenantScalarAsync<string>(t.Id, "select outcome from agents.run where id = @id", ("id", id)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.run_checkpoint where run_id = @id", ("id", id)));

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AgentRuns>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(AgentRuns.RecordRetentionDays + 1), CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.run where agent_id = @id", ("id", agent)));
    }
}
