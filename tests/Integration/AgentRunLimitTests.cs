using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Run limits and interrupted runs (spec 027 FR-015, spec 020 T004): a run stops when its working time passes the tenant
/// limit, with time spent waiting for a confirmation left out; an agent refuses new conversations past its daily limit
/// while the test panel still works; the limits are validated; and a run a restart cut off is closed as interrupted, in
/// its own tenant only.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentRunLimitTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("rl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
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

    private async Task<Guid> PublishedAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search", "test.note" };
        definition["autonomy"] = "L2";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static string Note(string text) => $"Note this [fake:tool:test__note] [fake:args:{{\"text\":\"{text}\"}}]";

    private Task PurgeAsync(Guid tenant, DateTimeOffset now) =>
        world.Factory.InScopeAsync(tenant, async sp =>
        {
            await sp.GetRequiredService<AgentRuns>().PurgeAsync(now, CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return 0;
        });

    [Fact]
    public async Task A_run_stops_when_its_working_time_passes_the_limit_and_waiting_for_a_confirmation_does_not_count()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t);

        // A run waits for the person for longer than the run limit; on confirming it goes on, since waiting is not work.
        var waiting = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = Note("Call HR") }));
        var id = waiting.GetProperty("id").GetGuid();
        var card = waiting.GetProperty("confirmation");
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set updated_at = now() - interval '10 minutes' where id = @id returning 1", ("id", id));
        var done = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/runs/{id}/confirmations/{card.GetProperty("id").GetGuid()}",
            new { confirm = true, hash = card.GetProperty("hash").GetString() }));
        Assert.Equal(RunStatuses.Completed, done.GetProperty("status").GetString());

        // A run that has already worked past the limit before its wait stops at the next step after the confirmation.
        var second = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = Note("Call IT") }));
        var secondId = second.GetProperty("id").GetGuid();
        var secondCard = second.GetProperty("confirmation");
        await world.Database.TenantScalarAsync<int>(t.Id,
            "update agents.run_checkpoint set state = jsonb_set(state, '{activeSeconds}', '1000') where run_id = @id returning 1", ("id", secondId));
        var stopped = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/runs/{secondId}/confirmations/{secondCard.GetProperty("id").GetGuid()}",
            new { confirm = true, hash = secondCard.GetProperty("hash").GetString() }));
        Assert.Equal(RunStatuses.Stopped, stopped.GetProperty("status").GetString());
        Assert.Equal(RunOutcomes.LimitReached, stopped.GetProperty("outcome").GetString());
        Assert.Equal("duration", stopped.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_agent_refuses_new_conversations_past_its_daily_limit_and_the_limits_are_validated()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t);

        Assert.Equal("validation.agent_policy.max_run_seconds", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy",
            new { version = 0, policy = AgentPolicy.Default with { MaxRunSeconds = 10 } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.agent_policy.max_daily_runs", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy",
            new { version = 0, policy = AgentPolicy.Default with { MaxDailyRunsPerAgent = 0 } }), HttpStatusCode.BadRequest));
        var saved = await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = AgentPolicy.Default with { MaxDailyRunsPerAgent = 1 } }));
        Assert.Equal(1, saved.GetProperty("policy").GetProperty("maxDailyRunsPerAgent").GetInt32());
        Assert.Equal(AgentPolicy.DefaultRunSeconds, saved.GetProperty("policy").GetProperty("maxRunSeconds").GetInt32());

        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "What is the leave policy?" }));
        Assert.Equal("conflict.agent_run.daily_limit", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs",
            new { message = "And the travel policy?" }), HttpStatusCode.Conflict));

        // The test panel does not count against the limit and is not refused by it.
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = "What is the leave policy?" }));

        // Yesterday's conversations do not count.
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set created_at = created_at - interval '1 day' where agent_id = @id and mode = 'run' returning 1",
            ("id", agent));
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "And the travel policy?" }));
    }

    [Fact]
    public async Task A_run_cut_off_by_a_restart_is_closed_as_interrupted_in_its_own_tenant_only()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var agent = await PublishedAsync(t);
        var otherAgent = await PublishedAsync(other);
        var mine = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "What is the leave policy?" }));
        var theirs = await OkAsync(other["emp"].Client.PostAsJsonAsync($"{Agents}/{otherAgent}/runs", new { message = "What is the leave policy?" }));
        var mineId = mine.GetProperty("id").GetGuid();
        var theirsId = theirs.GetProperty("id").GetGuid();
        const string Cut = "update agents.run set status = 'running', outcome = null, finished_at = null, updated_at = now() - interval '20 minutes' where id = @id returning 1";
        await world.Database.TenantScalarAsync<int>(t.Id, Cut, ("id", mineId));
        await world.Database.TenantScalarAsync<int>(other.Id, Cut, ("id", theirsId));

        // A run still going within the limit is left alone.
        var fresh = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "And the travel policy?" }));
        var freshId = fresh.GetProperty("id").GetGuid();
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set status = 'running', outcome = null, finished_at = null where id = @id returning 1", ("id", freshId));

        await PurgeAsync(t.Id, DateTimeOffset.UtcNow);

        Assert.Equal("failed/interrupted", await world.Database.TenantScalarAsync<string>(t.Id, "select status || '/' || outcome from agents.run where id = @id", ("id", mineId)));
        Assert.Equal("running", await world.Database.TenantScalarAsync<string>(t.Id, "select status from agents.run where id = @id", ("id", freshId)));
        Assert.Equal("running", await world.Database.TenantScalarAsync<string>(other.Id, "select status from agents.run where id = @id", ("id", theirsId)));

        var record = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{mineId}"));
        Assert.Equal(RunOutcomes.Interrupted, record.GetProperty("run").GetProperty("outcome").GetString());
    }
}
