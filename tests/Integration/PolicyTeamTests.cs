using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T020 (R2 manager dashboards): a line manager sees their direct and indirect reports in every open campaign,
/// with counts, who is paused for long leave or exempt, and who still owes it, overdue first. Nobody else's people show,
/// a person without reports has no dashboard, and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyTeamTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ptm-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "mona", "ali", "bea", "cai", "dan" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        // Mona manages Ali and Bea; Cai reports to Ali; Dan is in nobody's team here.
        foreach (var (report, manager) in new[] { ("ali", "mona"), ("bea", "mona"), ("cai", "ali") })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[report].Id}/manager", new { managerId = t[manager].Id })).StatusCode);
        }
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static async Task<JsonElement[]> TasksAsync(TestTenant t, string user, string source) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == source).ToArray();

    private static async Task<Guid> PublishedAsync(TestTenant t, string code)
    {
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = Both(code), route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var clause = new
        {
            level = 1, titles = Both("Use"),
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Use it well." } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "استخدمه جيدًا." } } } } },
            },
        };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both(code), clauses = new[] { clause } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await TasksAsync(t, "legal", "policy_approval")).Single();
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20; i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
    }

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private static string[] Names(JsonElement owing) =>
        owing.EnumerateArray().Select(o => o.GetProperty("person").GetProperty("displayName").GetString()!.Split(' ')[0].ToLowerInvariant()).ToArray();

    [Fact]
    public async Task A_manager_sees_the_team_across_open_campaigns_with_paused_exempt_and_owing_people()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var coc = await PublishedAsync(t, "COC");
        var first = (await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new { }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{coc}/campaigns", new { }), HttpStatusCode.Created);
        await world.DispatchTenantAsync(t.Id);

        // Ali attests to the AUP; Bea is paused for leave; Cai gets an exemption from the code of conduct.
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{aup}/attest", new { campaignId = first }), HttpStatusCode.Created);
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns/{first}/members/{t["bea"].Id}/pause", new { until = Day(30), reason = "Leave" }));
        await OkAsync(t["cai"].Client.PostAsJsonAsync($"{Policies}/{coc}/exemptions", new { reason = "Contract under review", endsOn = Day(60) }), HttpStatusCode.Created);
        for (var i = 0; i < 4 && (await TasksAsync(t, "co", "policy_exemption")).FirstOrDefault() is { ValueKind: JsonValueKind.Object } task; i++)
        {
            await DispatchAsync();
            await OkAsync(t["co"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve", comment = "Fine" }));
        }

        var team = await OkAsync(t["mona"].Client.GetAsync($"{Policies}/team"));
        Assert.Equal(3, team.GetProperty("reports").GetInt32());
        Assert.Equal(2, team.GetProperty("direct").GetInt32());
        var policies = team.GetProperty("policies").EnumerateArray().ToDictionary(p => p.GetProperty("policy").GetProperty("id").GetGuid());
        Assert.Equal(2, policies.Count);

        var a = policies[aup];
        Assert.Equal(3, a.GetProperty("counts").GetProperty("members").GetInt32());
        Assert.Equal(1, a.GetProperty("counts").GetProperty("attested").GetInt32());
        Assert.Equal(1, a.GetProperty("paused").GetInt32());
        Assert.Equal(["bea", "cai"], Names(a.GetProperty("owing")).Order().ToArray());
        var bea = a.GetProperty("owing").EnumerateArray().Single(o => o.GetProperty("person").GetProperty("id").GetGuid() == t["bea"].Id);
        Assert.NotEqual(JsonValueKind.Null, bea.GetProperty("pausedUntil").ValueKind);
        Assert.True(bea.GetProperty("direct").GetBoolean());
        Assert.False(a.GetProperty("owing").EnumerateArray().Single(o => o.GetProperty("person").GetProperty("id").GetGuid() == t["cai"].Id).GetProperty("direct").GetBoolean());
        Assert.DoesNotContain("Leave", team.ToString());

        var c = policies[coc];
        Assert.Equal(1, c.GetProperty("exempt").GetInt32());
        var cai = c.GetProperty("owing").EnumerateArray().Single(o => o.GetProperty("person").GetProperty("id").GetGuid() == t["cai"].Id);
        Assert.NotEqual(JsonValueKind.Null, cai.GetProperty("exemptUntil").ValueKind);
        Assert.DoesNotContain("Contract under review", team.ToString());
        Assert.DoesNotContain(t["dan"].Id.ToString(), team.ToString());

        // Ali sees only Cai; the register shows the dashboard to managers only.
        var ali = await OkAsync(t["ali"].Client.GetAsync($"{Policies}/team"));
        Assert.Equal(1, ali.GetProperty("reports").GetInt32());
        Assert.All(ali.GetProperty("policies").EnumerateArray(), p => Assert.Equal(["cai"], Names(p.GetProperty("owing"))));
        Assert.True((await OkAsync(t["mona"].Client.GetAsync($"{Policies}/rights"))).GetProperty("team").GetBoolean());
        Assert.False((await OkAsync(t["dan"].Client.GetAsync($"{Policies}/rights"))).GetProperty("team").GetBoolean());
    }

    [Fact]
    public async Task Without_reports_there_is_no_dashboard_and_other_tenants_see_none_of_it()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new { }), HttpStatusCode.Created);
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dan"].Client.GetAsync($"{Policies}/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["co"].Client.GetAsync($"{Policies}/team")).StatusCode);

        var other = await FreshAsync();
        var theirs = await OkAsync(other["mona"].Client.GetAsync($"{Policies}/team"));
        Assert.Empty(theirs.GetProperty("policies").EnumerateArray());
    }
}
