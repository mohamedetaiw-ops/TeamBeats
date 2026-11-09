using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Agent-prepared requests (spec 020 T013, US8 scenario 1, FR-013): the requests agent prepares a service request,
/// which waits in the person's task inbox marked as prepared by AI and is never submitted until the person submits the
/// form themselves. Only fields of the form are kept, the test panel stores nothing, a discarded draft leaves the inbox,
/// and nobody else, in this tenant or another, can open it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentPreparedRequestTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "omar", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "omar", "Hr");
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

    private static async Task<Guid> ServiceAsync(TestTenant t)
    {
        var admin = t["admin"].Client;
        var created = await OkAsync(admin.PostAsJsonAsync("/api/v1/services/admin", new { kind = "form", names = Names("Business cards", "بطاقات العمل") }), HttpStatusCode.Created);
        var id = created.GetProperty("service").GetProperty("id").GetGuid();
        var form = new
        {
            fields = new object[]
            {
                new { key = "name", type = "text", labels = Names("Name", "الاسم"), required = true, prefill = "profile.display_name" },
                new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true },
            },
        };
        await OkAsync(admin.PutAsJsonAsync($"/api/v1/services/admin/{id}/draft", new { form }));
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/services/admin/{id}/publish", new { }));
        return id;
    }

    private async Task<Guid> AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "workflow_requests", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        Assert.Contains("requests.prepare", ((JsonElement)definition["tools"]!).EnumerateArray().Select(x => x.GetString()));
        definition["tools"] = new[] { "knowledge.search", "requests.prepare" };
        definition["autonomy"] = "L1";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static string Prepare(Guid service, string data) =>
        $$"""Please prepare business cards [fake:tool:requests__prepare] [fake:args:{"serviceId":"{{service}}","fields":{{data}}}]""";

    private Task<long> CountAsync(TestTenant t, string sql, Guid user) => world.Database.TenantScalarAsync<long>(t.Id, sql, ("user", user));

    private static async Task<JsonElement?> PreparedTaskAsync(TestTenant t, string user) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"))).EnumerateArray()
        .Select(i => (JsonElement?)i).FirstOrDefault(i => i!.Value.GetProperty("source").GetString() == PreparedRequests.TaskSource);

    [Fact]
    public async Task A_prepared_request_waits_in_the_inbox_and_is_submitted_only_by_the_person()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t);
        var agent = await AgentAsync(t);

        var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = Prepare(service, """[{"key":"boxes","value":"2"},{"key":"bogus","value":"x"}]""") }));
        Assert.Equal(RunStatuses.Completed, run.GetProperty("status").GetString());
        Assert.Equal(0L, await CountAsync(t, "select count(*) from services.request where requester_user_id = @user", t["emp"].Id));

        // The inbox shows it as prepared by AI, linking to the form with the draft.
        var task = await PreparedTaskAsync(t, "emp") ?? throw new Xunit.Sdk.XunitException("no prepared task");
        var preparedId = task.GetProperty("taskId").GetGuid();
        Assert.Equal(PreparedRequests.Link(service, preparedId), task.GetProperty("link").GetString());
        Assert.Equal("Business cards", task.GetProperty("title").GetProperty("en").GetString());
        Assert.Null(await PreparedTaskAsync(t, "omar"));

        // The draft keeps only fields of the form, and only the person can open it.
        var draft = await OkAsync(t["emp"].Client.GetAsync($"/api/v1/requests/prepared/{preparedId}"));
        Assert.Equal(2, draft.GetProperty("data").GetProperty("boxes").GetInt32());
        Assert.False(draft.GetProperty("data").TryGetProperty("bogus", out _));
        Assert.Equal(agent, draft.GetProperty("agentId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.GetAsync($"/api/v1/requests/prepared/{preparedId}")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["emp"].Client.GetAsync($"/api/v1/requests/prepared/{preparedId}")).StatusCode);

        // Submitting the form from it files the request and clears the task.
        await OkAsync(t["emp"].Client.PostAsJsonAsync("/api/v1/requests",
            new { serviceId = service, data = new { name = "Emp", boxes = 2 }, idempotencyKey = "k1", preparedId }), HttpStatusCode.Created);
        Assert.Equal(1L, await CountAsync(t, "select count(*) from services.request where requester_user_id = @user", t["emp"].Id));
        Assert.Equal("submitted", (await OkAsync(t["emp"].Client.GetAsync($"/api/v1/requests/prepared/{preparedId}"))).GetProperty("status").GetString());
        Assert.Null(await PreparedTaskAsync(t, "emp"));
    }

    [Fact]
    public async Task A_draft_can_be_discarded_and_the_test_panel_or_an_unknown_service_stores_nothing()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t);
        var agent = await AgentAsync(t);

        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = Prepare(service, """[{"key":"boxes","value":"1"}]""") }));
        var preparedId = (await PreparedTaskAsync(t, "emp"))!.Value.GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.PostAsync($"/api/v1/requests/prepared/{preparedId}/discard", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["emp"].Client.PostAsync($"/api/v1/requests/prepared/{preparedId}/discard", null)).StatusCode);
        Assert.Null(await PreparedTaskAsync(t, "emp"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.PostAsync($"/api/v1/requests/prepared/{preparedId}/discard", null)).StatusCode);

        // The designer's test panel runs draft tools without storing a draft.
        var test = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{agent}/test", new { message = Prepare(service, """[{"key":"boxes","value":"1"}]""") }));
        Assert.Equal(RunStatuses.Completed, test.GetProperty("status").GetString());
        Assert.Equal(0L, await CountAsync(t, "select count(*) from services.prepared_request where user_id = @user", t["sara"].Id));

        // A service that does not exist, or one of another tenant, is refused by the tool.
        var other = await FreshAsync();
        var foreign = await ServiceAsync(other);
        foreach (var id in new[] { Guid.NewGuid(), foreign })
        {
            var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = Prepare(id, """[{"key":"boxes","value":"1"}]""") }));
            var record = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{run.GetProperty("id").GetGuid()}"));
            Assert.Contains(record.GetProperty("steps").EnumerateArray(), s => s.GetProperty("tool").GetString() == "requests.prepare" && s.GetProperty("reason").GetString() == "tool.not_found");
        }
        Assert.Equal(1L, await CountAsync(t, "select count(*) from services.prepared_request where user_id = @user", t["emp"].Id));
    }
}
