using System.Net;
using System.Net.Http.Headers;
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
/// Spec 020 R2 slice 6 (T021, plan item 6): a turn streams its ID, its tool steps and the answer text, and ends with the
/// checked answer; a person's run keeps its progress after every model turn, so a run a restart cut off is resumed by a
/// worker from its last checkpoint, once, without running a kept tool call again, and its result can be read later by
/// its person only. A holder that lost the run cannot overwrite it. Nothing crosses tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentRunResumeTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Search = "[fake:tool:knowledge__search] [fake:args:{\"query\":\"leave policy\"}]";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("rsm-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "other", "aip", "sec" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "other", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
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

    private async Task<Guid> PublishedAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 1 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static async Task<List<(string Name, JsonElement Data)>> StreamAsync(HttpClient client, Guid agent, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Agents}/{agent}/runs") { Content = JsonContent.Create(new { message }) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => block.Split('\n'))
            .Select(lines => (lines.First(l => l.StartsWith("event: ", StringComparison.Ordinal))[7..],
                JsonDocument.Parse(lines.First(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..]).RootElement.Clone()))
            .ToList();
    }

    /// <summary>
    /// A run a restart cuts off after its tool turn: the request's work up to the last checkpoint is kept, the rest
    /// (the answer) is rolled back, as when the process dies, and the run stays running.
    /// </summary>
    private async Task<Guid> CutOffAsync(TestTenant t, Guid agent)
    {
        Guid? id = null;
        await world.Factory.AsUserAsync(t.Id, t["emp"].Id, async sp =>
        {
            var view = await sp.GetRequiredService<AgentRuns>().RunAsync(agent, new RunRequest($"What does the leave policy say? {Search}", "en"), CancellationToken.None);
            id = view.Id;
            // No commit: the scope ends as a crashed request would.
            return true;
        });
        Assert.Equal("running", await world.Database.TenantScalarAsync<string>(t.Id, "select status from agents.run where id = @id", ("id", id!.Value)));
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set updated_at = now() - interval '5 minutes' where id = @id returning 1", ("id", id.Value));
        return id.Value;
    }

    private Task<int> ResumeAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<AgentRunResumer>().ResumeStalledAsync(CancellationToken.None));

    [Fact]
    public async Task A_turn_streams_its_id_its_tool_steps_and_the_answer_then_the_checked_answer()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t);

        var events = await StreamAsync(t["emp"].Client, agent, $"What does the leave policy say? {Search}");
        Assert.Equal("run", events[0].Name);
        var runId = events[0].Data.GetProperty("id").GetGuid();
        var step = events.Single(e => e.Name == "step").Data;
        Assert.Equal("knowledge.search", step.GetProperty("tool").GetString());
        Assert.Equal("executed", step.GetProperty("decision").GetString());
        Assert.Contains(events, e => e.Name == "delta");
        var done = events.Last();
        Assert.Equal("done", done.Name);
        Assert.Equal(runId, done.Data.GetProperty("id").GetGuid());
        Assert.Equal("completed", done.Data.GetProperty("status").GetString());
        Assert.StartsWith("This is a test answer from a fake model", done.Data.GetProperty("answer").GetString());

        // The result is kept for the person, and only for them.
        var result = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{runId}/result"));
        Assert.Equal(done.Data.GetProperty("answer").GetString(), result.GetProperty("answer").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["other"].Client.GetAsync($"{Agents}/runs/{runId}/result")).StatusCode);
        var stranger = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger["emp"].Client.GetAsync($"{Agents}/runs/{runId}/result")).StatusCode);

        // A refusal before the turn starts is still a normal problem response.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Agents}/{Guid.NewGuid()}/runs") { Content = JsonContent.Create(new { message = "Hello" }) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_run_a_restart_cut_off_is_resumed_once_from_its_checkpoint_without_repeating_a_kept_tool_call()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t);
        var runId = await CutOffAsync(t, agent);
        // The tool turn was kept; the answer that came after it was not.
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.run_step where run_id = @id and kind = 'tool'", ("id", runId)));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.run_checkpoint where run_id = @id", ("id", runId)));

        // The purge leaves a running run's checkpoint for the worker.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AgentRuns>().PurgeAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return true;
        });
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.run_checkpoint where run_id = @id", ("id", runId)));

        Assert.Equal(1, await ResumeAsync(t));
        var result = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{runId}/result"));
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.StartsWith("This is a test answer from a fake model", result.GetProperty("answer").GetString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.run_step where run_id = @id and kind = 'tool'", ("id", runId)));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id, "select attempt from agents.run where id = @id", ("id", runId)));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.run_checkpoint where run_id = @id", ("id", runId)));

        // A finished run is not resumed again, and the attempt that lost it finds nothing to go on with.
        Assert.Equal(0, await ResumeAsync(t));
        Assert.Null(await world.Factory.AsUserAsync(t.Id, t["emp"].Id, sp => sp.GetRequiredService<AgentRuns>().ResumeAsync(runId, 0, CancellationToken.None)));
    }

    [Fact]
    public async Task The_kill_switch_stops_a_cut_off_run_and_runs_are_not_resumed_forever()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t);
        var stopped = await CutOffAsync(t, agent);
        // The kill switch stops a run that is still going and takes it from its holder, so nothing resumes it.
        await OkAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch", new { active = true, confirmation = $"{t.Key} holding", reason = "Checking a problem" }));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id, "select attempt from agents.run where id = @id", ("id", stopped)));
        Assert.Equal(0, await ResumeAsync(t));
        var result = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{stopped}/result"));
        Assert.Equal("stopped", result.GetProperty("status").GetString());
        Assert.Equal("suspended", result.GetProperty("outcome").GetString());
        await OkAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch", new { active = false }));

        // A run that keeps being cut off is resumed at most twice; then the purge closes it as interrupted.
        var stuck = await CutOffAsync(t, agent);
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set attempt = @max where id = @id returning 1", ("id", stuck), ("max", AgentRuns.MaxResumes));
        Assert.Equal(0, await ResumeAsync(t));
        Assert.Equal("running", await world.Database.TenantScalarAsync<string>(t.Id, "select status from agents.run where id = @id", ("id", stuck)));

        // Another tenant's worker never sees the run.
        var other = await FreshAsync();
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run set attempt = 0 where id = @id returning 1", ("id", stuck));
        Assert.Equal(0, await ResumeAsync(other));
        Assert.Equal(0, await world.Database.TenantScalarAsync<int>(t.Id, "select attempt from agents.run where id = @id", ("id", stuck)));
    }
}
