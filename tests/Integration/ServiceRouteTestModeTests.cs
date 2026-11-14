using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 R2 T024 test mode (US8 scenario 6): the draft route runs in the sandbox as a sample employee and shows each
/// step's resolved assignee and the branch taken, with nothing created; owners only, and another tenant gets not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceRouteTestModeTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static object Step(string id, string type, object? extra = null)
    {
        var step = new Dictionary<string, object?> { ["id"] = id, ["type"] = type, ["names"] = Names(id, id) };
        foreach (var p in (extra ?? new { }).GetType().GetProperties())
        {
            step[p.Name] = p.GetValue(extra);
        }
        return step;
    }

    private static object Link(string from, string to, object[]? when = null) => new { from, to, when };

    [Fact]
    public async Task A_test_run_shows_assignees_and_the_branch_taken_and_creates_nothing()
    {
        var t = await world.CreateTenantAsync("rtm-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "mona", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        var created = await t["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Training", "تدريب") });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        var draft = await t["admin"].Client.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "cost", type = "number", labels = Names("Cost", "التكلفة") } } },
            route = new
            {
                graph = new
                {
                    steps = new[]
                    {
                        Step("manager", "approval", new { assignee = new { kind = "line_manager" } }), Step("costly", "condition"),
                        Step("head", "approval", new { assignee = new { kind = "users", users = new[] { t["kofi"].Id } } }), Step("done", "end", new { result = "approved" }),
                    },
                    links = new[] { Link("manager", "costly"), Link("costly", "head", [new { field = "cost", @operator = "gt", value = 1000 }]), Link("costly", "done"), Link("head", "done") },
                },
            },
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());

        async Task<JsonElement> RunAsync(HttpClient client, object body)
        {
            var response = await client.PostAsJsonAsync($"{Admin}/{id}/test", body);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await response.ReadAsync<JsonElement>();
        }
        static List<string> StepsOf(JsonElement run) => run.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("stepId").GetString()!).Distinct().ToList();

        var cheap = await RunAsync(t["admin"].Client, new { requesterId = t["sara"].Id, data = new { cost = 400 } });
        Assert.Equal(["manager", "costly", "done"], StepsOf(cheap));
        var manager = cheap.GetProperty("steps").EnumerateArray().First(s => s.GetProperty("stepId").GetString() == "manager" && s.GetProperty("assignees").ValueKind == JsonValueKind.Array);
        Assert.Contains(manager.GetProperty("assignees").EnumerateArray(), a => a.GetProperty("id").GetGuid() == t["mona"].Id);

        var costly = await RunAsync(t["admin"].Client, new { requesterId = t["sara"].Id, data = new { cost = 4000 } });
        Assert.Contains("head", StepsOf(costly));
        var rejected = await RunAsync(t["admin"].Client, new { requesterId = t["sara"].Id, data = new { cost = 4000 }, outcomes = new Dictionary<string, string> { ["manager"] = "reject" } });
        Assert.DoesNotContain("head", StepsOf(rejected));
        Assert.NotEqual(costly.GetProperty("outcome").GetString(), rejected.GetProperty("outcome").GetString());

        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from services.request"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from workflow.process_instance"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsJsonAsync($"{Admin}/{id}/test", new { })).StatusCode);
        var other = await world.CreateTenantAsync("rtm-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/test", new { })).StatusCode);
    }

    [Fact]
    public async Task Owners_list_the_tenant_groups_for_queues_and_others_cannot()
    {
        var t = await world.CreateTenantAsync("rtg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        var group = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var id = await sp.GetRequiredService<IAuthorizationProvisioning>().SyncDirectoryGroupAsync("logistics", "Logistics", [t["sara"].Id]);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return id;
        });
        var list = (await (await t["admin"].Client.GetAsync($"{Admin}/groups")).ReadAsync<JsonElement>()).EnumerateArray().ToList();
        var logistics = Assert.Single(list, g => g.GetProperty("id").GetGuid() == group);
        Assert.Equal("Logistics", logistics.GetProperty("name").GetString());
        Assert.Equal(1, logistics.GetProperty("members").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Admin}/groups")).StatusCode);

        // Another tenant never sees this tenant's groups.
        var other = await world.CreateTenantAsync("rtg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var theirs = (await (await other["admin"].Client.GetAsync($"{Admin}/groups")).ReadAsync<JsonElement>()).EnumerateArray();
        Assert.DoesNotContain(theirs, g => g.GetProperty("id").GetGuid() == group);
    }
}
