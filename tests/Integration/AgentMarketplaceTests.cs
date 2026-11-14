using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 020 R2 slice 2 (T017, US6, FR-010): the marketplace lists agents by audience and lets people outside an
/// agent's audience ask for it when its owners offer that; hidden agents never appear. Requests reach the owners'
/// inbox and the decision reaches the requester. Favourites lead the assistant's agent list, and ratings keep their
/// comments private. Nothing crosses tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentMarketplaceTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("am-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        foreach (var name in new[] { "sara", "emp", "out", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "sara", "Hr");
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

    /// <summary>Installs the knowledge template in Hr, names it, sets who outside Hr sees it, and publishes it.</summary>
    private async Task<Guid> PublishedAsync(TestTenant t, string name, string outside)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["name"] = new Dictionary<string, string> { ["en"] = name, ["ar"] = name };
        definition["audience"] = new { scope = "org_units", orgUnits = new[] { t.Unit("Hr") }, outside };
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string query = "") =>
        (await OkAsync(client.GetAsync($"{Agents}/marketplace{query}"))).GetProperty("agents").EnumerateArray().ToList();

    [Fact]
    public async Task Hidden_agents_never_appear_outside_their_audience_and_access_is_asked_of_the_owners()
    {
        var t = await FreshAsync();
        var hidden = await PublishedAsync(t, "Payroll questions", AgentAudience.Hidden);
        var offered = await PublishedAsync(t, "Leave helper", AgentAudience.RequestAccess);

        // People in the audience see both and may use them.
        var inside = await ListAsync(t["emp"].Client);
        Assert.Equal(2, inside.Count);
        Assert.All(inside, a => Assert.True(a.GetProperty("canUse").GetBoolean()));
        Assert.Equal("Hr", inside[0].GetProperty("department").GetProperty("en").GetString());

        // Outside it, only the offered one shows, to be asked for; search and category narrow the list.
        var outside = Assert.Single(await ListAsync(t["out"].Client));
        Assert.Equal(offered, outside.GetProperty("id").GetGuid());
        Assert.False(outside.GetProperty("canUse").GetBoolean());
        Assert.Single(await ListAsync(t["out"].Client, "?q=leave"));
        Assert.Empty(await ListAsync(t["out"].Client, "?q=payroll"));
        Assert.Empty(await ListAsync(t["out"].Client, "?category=hr"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.PostAsync($"{Agents}/{hidden}/access-requests", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.PostAsJsonAsync($"{Agents}/{offered}/runs", new { message = "Hello" })).StatusCode);

        // The request waits once, in the owner's inbox.
        var request = await OkAsync(t["out"].Client.PostAsync($"{Agents}/{offered}/access-requests", null));
        Assert.Equal(AccessRequestStatuses.Pending, request.GetProperty("status").GetString());
        Assert.Equal(request.GetProperty("id").GetGuid(), (await OkAsync(t["out"].Client.PostAsync($"{Agents}/{offered}/access-requests", null))).GetProperty("id").GetGuid());
        Assert.Equal(AccessRequestStatuses.Pending, Assert.Single(await ListAsync(t["out"].Client)).GetProperty("request").GetString());
        var tasks = await OkAsync(t["sara"].Client.GetAsync("/api/v1/tasks"));
        Assert.Contains(tasks.EnumerateArray(), i => i.GetProperty("source").GetString() == AgentAccessTasks.TaskSource);
        Assert.Equal("conflict.agent.already_in_audience", await ProblemAsync(t["emp"].Client.PostAsync($"{Agents}/{offered}/access-requests", null), HttpStatusCode.Conflict));

        // Only an owner decides; the requester then uses the agent and is told.
        var requestId = request.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.PostAsJsonAsync($"{Agents}/access-requests/{requestId}/decision", new { grant = true })).StatusCode);
        var waiting = Assert.Single((await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{offered}/access-requests"))).GetProperty("requests").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(waiting.GetProperty("userName").GetString()));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/access-requests/{requestId}/decision", new { grant = true }));
        Assert.Equal("conflict.agent.access_decided",
            await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/access-requests/{requestId}/decision", new { grant = false }), HttpStatusCode.Conflict));
        Assert.True(Assert.Single(await ListAsync(t["out"].Client)).GetProperty("canUse").GetBoolean());
        Assert.Contains((await OkAsync(t["out"].Client.GetAsync($"{Agents}/available"))).GetProperty("agents").EnumerateArray(), a => a.GetProperty("id").GetGuid() == offered);
        await OkAsync(t["out"].Client.PostAsJsonAsync($"{Agents}/{offered}/runs", new { message = "When is the leave year?" }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentAccessRequested, offered.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentAccessGranted, offered.ToString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @user and template_key = 'agents.access_granted'", ("user", t["out"].Id)));

        // Another tenant finds nothing.
        var other = await FreshAsync();
        Assert.Empty(await ListAsync(other["out"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await other["out"].Client.PostAsync($"{Agents}/{offered}/access-requests", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["sara"].Client.PostAsJsonAsync($"{Agents}/access-requests/{requestId}/decision", new { grant = false })).StatusCode);
    }

    [Fact]
    public async Task Favourites_lead_the_agent_list_and_rating_comments_stay_private()
    {
        var t = await FreshAsync();
        var first = await PublishedAsync(t, "Leave helper", AgentAudience.Hidden);
        var second = await PublishedAsync(t, "Policy helper", AgentAudience.RequestAccess);
        var emp = t["emp"].Client;

        // The most recently changed agent leads until the person favourites another.
        Assert.Equal(second, (await OkAsync(emp.GetAsync($"{Agents}/available"))).GetProperty("agents")[0].GetProperty("id").GetGuid());
        await OkAsync(emp.PutAsync($"{Agents}/{first}/favourite", null), HttpStatusCode.NoContent);
        var available = (await OkAsync(emp.GetAsync($"{Agents}/available"))).GetProperty("agents");
        Assert.Equal(first, available[0].GetProperty("id").GetGuid());
        Assert.True(available[0].GetProperty("favourite").GetBoolean());
        Assert.True((await ListAsync(emp)).Single(a => a.GetProperty("id").GetGuid() == first).GetProperty("favourite").GetBoolean());
        // A favourite is the person's own, and only of an agent they may use.
        Assert.Equal(second, (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/available"))).GetProperty("agents")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.PutAsync($"{Agents}/{second}/favourite", null)).StatusCode);
        await OkAsync(emp.DeleteAsync($"{Agents}/{first}/favourite"), HttpStatusCode.NoContent);
        Assert.Equal(second, (await OkAsync(emp.GetAsync($"{Agents}/available"))).GetProperty("agents")[0].GetProperty("id").GetGuid());

        // Ratings: the average for everyone who sees the agent, comments for the owners, the AI policy administrator and the author.
        await OkAsync(emp.PutAsJsonAsync($"{Agents}/{second}/rating", new { score = 4, comment = "Quick and clear answers." }), HttpStatusCode.NoContent);
        await OkAsync(emp.PutAsJsonAsync($"{Agents}/{second}/rating", new { score = 2, comment = "Out of date this week." }), HttpStatusCode.NoContent);
        Assert.Equal("validation.agent.rating_score", await ProblemAsync(emp.PutAsJsonAsync($"{Agents}/{second}/rating", new { score = 6 }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.PutAsJsonAsync($"{Agents}/{second}/rating", new { score = 1 })).StatusCode);
        var listed = (await ListAsync(t["out"].Client)).Single(a => a.GetProperty("id").GetGuid() == second);
        Assert.Equal(2.0, listed.GetProperty("rating").GetDouble());
        Assert.Equal(1, listed.GetProperty("ratings").GetInt32());

        var forOutsider = await OkAsync(t["out"].Client.GetAsync($"{Agents}/{second}/ratings"));
        Assert.Equal(1, forOutsider.GetProperty("count").GetInt32());
        Assert.Empty(forOutsider.GetProperty("comments").EnumerateArray());
        Assert.Equal("Out of date this week.", Assert.Single((await OkAsync(emp.GetAsync($"{Agents}/{second}/ratings"))).GetProperty("comments").EnumerateArray())
            .GetProperty("comment").GetString());
        Assert.Single((await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{second}/ratings"))).GetProperty("comments").EnumerateArray());
        Assert.Single((await OkAsync(t["aip"].Client.GetAsync($"{Agents}/{second}/ratings"))).GetProperty("comments").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["out"].Client.GetAsync($"{Agents}/{first}/ratings")).StatusCode);

        // A denied request leaves the person outside, tells them, and they may ask again later.
        var asked = (await OkAsync(t["out"].Client.PostAsync($"{Agents}/{second}/access-requests", null))).GetProperty("id").GetGuid();
        var denied = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/access-requests/{asked}/decision", new { grant = false }));
        Assert.Equal(AccessRequestStatuses.Denied, denied.GetProperty("status").GetString());
        var seen = (await ListAsync(t["out"].Client)).Single(a => a.GetProperty("id").GetGuid() == second);
        Assert.False(seen.GetProperty("canUse").GetBoolean());
        Assert.Equal(AccessRequestStatuses.Denied, seen.GetProperty("request").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentAccessDenied, second.ToString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @user and template_key = 'agents.access_denied'", ("user", t["out"].Id)));
        Assert.NotEqual(asked, (await OkAsync(t["out"].Client.PostAsync($"{Agents}/{second}/access-requests", null))).GetProperty("id").GetGuid());
    }
}
