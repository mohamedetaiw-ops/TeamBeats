using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
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
/// Spec 020 R2 slice 3 (T018, US9, FR-015): persistent agent memory. Nothing is kept without the tenant switch, a
/// persistent memory area and the person's acceptance; Restricted content is refused; the person views, edits and deletes
/// their notes, which nobody else reads; notes go after retention without use, on withdrawal, on leaver and when the
/// tenant turns memory off. Nothing crosses tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentMemoryTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Note = "Answers in Arabic and works in the Riyadh office.";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("mem-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "ali", "out", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "ali", "Hr");
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

    private static string Remember(string text, string? classification = null) =>
        "[fake:tool:memory__remember] [fake:args:" + JsonSerializer.Serialize(classification is null ? new { text } : (object)new { text, classification }) + "]";

    private async Task SetMemoryPolicyAsync(TestTenant t, bool on)
    {
        var policy = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(policy.GetProperty("policy").GetRawText())!;
        body["persistentMemory"] = on;
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = policy.GetProperty("version").GetInt32(), policy = body }));
    }

    /// <summary>Installs the knowledge template in Hr with the given memory area and publishes it.</summary>
    private async Task<Guid> PublishedAsync(TestTenant t, object? memory)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["memory"] = memory;
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static async Task<string?> RememberStepAsync(HttpClient client, Guid agent, string message)
    {
        var run = await OkAsync(client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message }));
        var record = await OkAsync(client.GetAsync($"{Agents}/runs/{run.GetProperty("id").GetGuid()}"));
var step = record.GetProperty("steps").EnumerateArray().FirstOrDefault(s => s.GetProperty("tool").GetString() == MemoryRememberTool.Name);
        return step.ValueKind == JsonValueKind.Undefined ? null : step.GetProperty("decision").GetString() + ":" + step.GetProperty("reason").GetString();
    }

    private Task<long> ItemsAsync(TestTenant t, string name) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.agent_memory where user_id = @user", ("user", t[name].Id));

    private static async Task<JsonElement> ViewAsync(HttpClient client, Guid agent) => await OkAsync(client.GetAsync($"{Agents}/{agent}/memory"));

    [Fact]
    public async Task Nothing_is_kept_without_the_switch_the_memory_area_and_the_persons_acceptance()
    {
        var t = await FreshAsync();
        var emp = t["emp"].Client;

        // Session memory is the default: the model is offered no memory tool, so nothing is called or kept.
        var session = await PublishedAsync(t, null);
        Assert.Null(await RememberStepAsync(emp, session, "Remember me " + Remember(Note)));
        Assert.False((await ViewAsync(emp, session)).GetProperty("offered").GetBoolean());

        // Persistent memory needs the tenant switch first.
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var draftId = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var draft = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        draft["memory"] = new { mode = "persistent", retentionDays = 30 };
        Assert.Equal("validation.agent.memory_off",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{draftId}/draft", new { revision = 1, definition = draft }), HttpStatusCode.BadRequest));
        await SetMemoryPolicyAsync(t, true);
        draft["memory"] = new { mode = "persistent", retentionDays = 400 };
        Assert.Equal("validation.agent.memory",
            await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{draftId}/draft", new { revision = 1, definition = draft }), HttpStatusCode.BadRequest));

        var agent = await PublishedAsync(t, new { mode = "persistent", retentionDays = 30 });
        var offer = await ViewAsync(emp, agent);
        Assert.True(offer.GetProperty("offered").GetBoolean());
        Assert.False(offer.GetProperty("accepted").GetBoolean());
        Assert.Equal(30, offer.GetProperty("retentionDays").GetInt32());

        // Until the person accepts, the agent has no memory tool.
        Assert.Null(await RememberStepAsync(emp, agent, "Remember me " + Remember(Note)));
        Assert.Equal(0L, await ItemsAsync(t, "emp"));

        Assert.True((await OkAsync(emp.PutAsync($"{Agents}/{agent}/memory/consent", null))).GetProperty("accepted").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentMemoryAccepted, agent.ToString());
        Assert.Equal("executed:", await RememberStepAsync(emp, agent, "Remember me " + Remember(Note)));
        var item = Assert.Single((await ViewAsync(emp, agent)).GetProperty("items").EnumerateArray());
        Assert.Equal(Note, item.GetProperty("text").GetString());
        Assert.Equal("internal", item.GetProperty("classification").GetString());

        // Restricted content is never kept, and nothing above the agent's ceiling either.
        Assert.Equal("refused:tool.memory_restricted", await RememberStepAsync(emp, agent, "Remember the merger " + Remember("Merger code name Falcon.", "restricted")));
        Assert.Equal("refused:tool.classification_blocked", await RememberStepAsync(emp, agent, "Remember the bonus " + Remember("Bonus pool figure.", "confidential")));
        Assert.Equal(1L, await ItemsAsync(t, "emp"));

        // A later conversation reads the note, which counts as use.
        var itemId = item.GetProperty("id").GetGuid();
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent_memory set last_used_at = now() - interval '20 days' where id = @id returning 1", ("id", itemId));
        await OkAsync(emp.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "Which office am I in?" }));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.agent_memory where id = @id and last_used_at > now() - interval '1 day'", ("id", itemId)));

        // Someone outside the audience cannot accept memory for the agent.
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.PutAsync($"{Agents}/{agent}/memory/consent", null)).StatusCode);
    }

    [Fact]
    public async Task The_person_alone_reads_edits_and_deletes_their_notes_and_they_go_on_withdrawal_retention_leaver_and_policy_off()
    {
        var t = await FreshAsync();
        await SetMemoryPolicyAsync(t, true);
        var agent = await PublishedAsync(t, new { mode = "persistent", retentionDays = 30 });
        var emp = t["emp"].Client;
        await OkAsync(emp.PutAsync($"{Agents}/{agent}/memory/consent", null));
        await RememberStepAsync(emp, agent, "Remember me " + Remember(Note));
        var itemId = (await ViewAsync(emp, agent)).GetProperty("items")[0].GetProperty("id").GetGuid();

        // Only the person: a colleague in the audience sees none of it, someone outside it or another tenant finds nothing.
        Assert.Empty((await ViewAsync(t["ali"].Client, agent)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.GetAsync($"{Agents}/{agent}/memory")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.PutAsJsonAsync($"{Agents}/memory/{itemId}", new { text = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ali"].Client.DeleteAsync($"{Agents}/memory/{itemId}")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["emp"].Client.GetAsync($"{Agents}/{agent}/memory")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["emp"].Client.DeleteAsync($"{Agents}/memory/{itemId}")).StatusCode);

        // Edit and delete.
        Assert.Equal("validation.agent.memory_text", await ProblemAsync(emp.PutAsJsonAsync($"{Agents}/memory/{itemId}", new { text = " " }), HttpStatusCode.BadRequest));
        Assert.Equal("Works in the Jeddah office.", (await OkAsync(emp.PutAsJsonAsync($"{Agents}/memory/{itemId}", new { text = "Works in the Jeddah office." })))
            .GetProperty("text").GetString());
        Assert.Single((await OkAsync(emp.GetAsync($"{Agents}/memory"))).GetProperty("agents").EnumerateArray());
        await OkAsync(emp.DeleteAsync($"{Agents}/memory/{itemId}"), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentMemoryDeleted, agent.ToString());
        Assert.Equal(0L, await ItemsAsync(t, "emp"));

        // Retention: a note unused for the agent's 30 days goes.
        await RememberStepAsync(emp, agent, "Remember me " + Remember(Note));
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.agent_memory set last_used_at = now() - interval '31 days' where user_id = @user returning 1", ("user", t["emp"].Id));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<AgentMemory>().PurgeAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        Assert.Equal(0L, await ItemsAsync(t, "emp"));

        // Withdrawal deletes every note and the acceptance.
        await RememberStepAsync(emp, agent, "Remember me " + Remember(Note));
        await OkAsync(emp.DeleteAsync($"{Agents}/{agent}/memory/consent"), HttpStatusCode.NoContent);
        Assert.Equal(0L, await ItemsAsync(t, "emp"));
        Assert.False((await ViewAsync(emp, agent)).GetProperty("accepted").GetBoolean());

        // A leaver's notes go.
        await OkAsync(t["ali"].Client.PutAsync($"{Agents}/{agent}/memory/consent", null));
        await RememberStepAsync(t["ali"].Client, agent, "Remember me " + Remember(Note));
        Assert.Equal(1L, await ItemsAsync(t, "ali"));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserDeprovisioned(t["ali"].Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        Assert.Equal(0L, await ItemsAsync(t, "ali"));

        // Turning the switch off deletes everything left and ends the offer.
        await OkAsync(emp.PutAsync($"{Agents}/{agent}/memory/consent", null));
        await RememberStepAsync(emp, agent, "Remember me " + Remember(Note));
        Assert.Equal(1L, await ItemsAsync(t, "emp"));
        await SetMemoryPolicyAsync(t, false);
        Assert.Equal(0L, await ItemsAsync(t, "emp"));
        var after = await ViewAsync(emp, agent);
        Assert.False(after.GetProperty("offered").GetBoolean());
        Assert.False(after.GetProperty("accepted").GetBoolean());
    }
}
