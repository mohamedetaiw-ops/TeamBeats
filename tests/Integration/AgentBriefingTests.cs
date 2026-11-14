using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 020 R2 slice 4 (T019, US8 scenarios 2 to 4, FR-014; spec 027 AGV-003): daily briefings. Offered only when the
/// agent offers one, the person may use it and the tenant allows scheduled agents of its risk class; a due briefing runs
/// as the subscriber, read only, and arrives as a notification; opting out, suspension, a policy change or leaving stop
/// it. Nothing crosses tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentBriefingTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private static readonly object Briefing = new { enabled = true, prompt = new Dictionary<string, string> { ["en"] = "Summarize my pending tasks.", ["ar"] = "لخص مهامي المعلقة." } };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("brf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "out", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "out", "Finance");
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

    private async Task PolicyAsync(TestTenant t, bool scheduled, string[]? classes = null)
    {
        var policy = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(policy.GetProperty("policy").GetRawText())!;
        body["scheduledAgents"] = scheduled;
        body["scheduledRiskClasses"] = classes;
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = policy.GetProperty("version").GetInt32(), policy = body }));
    }

    private async Task<Guid> PublishedAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["briefing"] = Briefing;
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    /// <summary>Makes every briefing of the tenant due and runs the worker once, as it runs in the background.</summary>
    private async Task<int> RunDueAsync(TestTenant t)
    {
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent_briefing set next_run_at = now() - interval '1 minute' returning 1");
        return await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<AgentBriefingRunner>().RunDueAsync(CancellationToken.None));
    }

    private Task<long> BriefingNotificationsAsync(TestTenant t, string name) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @user and template_key = 'agents.briefing'", ("user", t[name].Id));

    [Fact]
    public async Task A_subscriber_gets_a_read_only_briefing_as_themselves_until_policy_suspension_or_opting_out_stops_it()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;

        // A briefing needs scheduled agents in the tenant policy, and its question in every language.
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var draftId = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var draft = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        draft["briefing"] = Briefing;
        Assert.Equal("validation.agent.briefing_off",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{draftId}/draft", new { revision = 1, definition = draft }), HttpStatusCode.BadRequest));
        await PolicyAsync(t, true);
        draft["briefing"] = new { enabled = true, prompt = new Dictionary<string, string> { ["en"] = "Summarize my pending tasks." } };
        Assert.Equal("validation.agent.briefing_prompt",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{draftId}/draft", new { revision = 1, definition = draft }), HttpStatusCode.BadRequest));

        var agent = await PublishedAsync(t);
        var offer = await OkAsync(emp.GetAsync($"{Agents}/{agent}/briefing"));
        Assert.True(offer.GetProperty("offered").GetBoolean());
        Assert.Equal(JsonValueKind.Null, offer.GetProperty("subscription").ValueKind);

        // Opt in at 08:00 on working days in Riyadh.
        Assert.Equal("validation.agent.briefing_time", await ProblemAsync(emp.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "8am" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.agent.briefing_days", await ProblemAsync(emp.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "08:00", days = new[] { 9 } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.agent.briefing_time_zone",
            await ProblemAsync(emp.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "08:00", timeZone = "Mars/Olympus" }), HttpStatusCode.BadRequest));
        var subscribed = await OkAsync(emp.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "08:00", days = new[] { 1, 2, 3, 4, 5 }, timeZone = "Asia/Riyadh" }));
        var next = subscribed.GetProperty("subscription").GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.Equal(5, next.Hour);
        Assert.InRange(next.DayOfWeek, DayOfWeek.Monday, DayOfWeek.Friday);

        // Someone outside the audience, and another tenant, find nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.GetAsync($"{Agents}/{agent}/briefing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "08:00" })).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["emp"].Client.GetAsync($"{Agents}/{agent}/briefing")).StatusCode);

        // The worker runs the due briefing as the subscriber and delivers the answer.
        Assert.Equal(1, await RunDueAsync(t));
        Assert.Equal(1L, await BriefingNotificationsAsync(t, "emp"));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.run where mode = 'briefing' and user_id = @user", ("user", t["emp"].Id)));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.agent_briefing where next_run_at > now() and last_run_id is not null"));
        Assert.Equal(0, await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<AgentBriefingRunner>().RunDueAsync(CancellationToken.None)));

        // A policy that no longer allows scheduled agents of this risk class ends the offer and the runs.
        await PolicyAsync(t, true, ["medium"]);
        Assert.False((await OkAsync(emp.GetAsync($"{Agents}/{agent}/briefing"))).GetProperty("offered").GetBoolean());
        Assert.Equal("conflict.agent.briefing_not_offered", await ProblemAsync(emp.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "09:00" }), HttpStatusCode.Conflict));
        Assert.Equal(0, await RunDueAsync(t));
        await PolicyAsync(t, true);

        // A suspended agent sends nothing.
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent set status = 'suspended' where id = @id returning 1", ("id", agent));
        Assert.Equal(0, await RunDueAsync(t));
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent set status = 'published' where id = @id returning 1", ("id", agent));
        Assert.Equal(1, await RunDueAsync(t));

        // Opting out stops it.
        await OkAsync(emp.DeleteAsync($"{Agents}/{agent}/briefing"), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await emp.DeleteAsync($"{Agents}/{agent}/briefing")).StatusCode);
        Assert.Equal(0, await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<AgentBriefingRunner>().RunDueAsync(CancellationToken.None)));
        Assert.Equal(2L, await BriefingNotificationsAsync(t, "emp"));
    }

    [Fact]
    public async Task A_leaver_gets_no_more_briefings()
    {
        var t = await FreshAsync();
        await PolicyAsync(t, true);
        var agent = await PublishedAsync(t);
        await OkAsync(t["emp"].Client.PutAsJsonAsync($"{Agents}/{agent}/briefing", new { time = "07:30" }));

        // The person leaves: their briefing ends with them.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserDeprovisioned(t["emp"].Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.agent_briefing"));
        Assert.Equal(0L, await BriefingNotificationsAsync(t, "emp"));
    }
}
