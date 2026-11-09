using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T007 and T008 (US3, FR-009): completion of the version in force by unit with subtree roll-up and
/// drill-down, by location and by line manager; the per-person view; an org-scoped compliance officer sees only their
/// subtree; a line manager sees only their reports and can remind them; other people and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyComplianceTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private sealed record Org(TestTenant T, Guid Holding, Guid Gulf, Guid Sales, Guid Egypt);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pq-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var sales = await world.CreateUnitAsync(t, "Sales", gulf, "department");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "co", "gco", "legal", "ali", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "bea", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "bea", "Sales");
        await world.SeedUserAsync(t, "cai", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "cai", "Egypt");
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.AssignAsync(t, "gco", BuiltInRoles.ComplianceOfficer, "org_subtree", gulf);
        return new Org(t, holding, gulf, sales, egypt);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static readonly Dictionary<string, string> Titles = new() { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" };

    private static object Clauses(string text) => new[]
    {
        new
        {
            level = 1, titles = new Dictionary<string, string> { ["en"] = text, ["ar"] = text + " ع" },
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " text" } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " نص" } } } } },
            },
        },
    };

    /// <summary>A tenant-wide policy approved by legal and in force, with a campaign launched by the compliance officer.</summary>
    private async Task<(Guid Policy, Guid Campaign)> LaunchedAsync(Org o)
    {
        var t = o.T;
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "P-1", names = Titles, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Titles, clauses = Clauses("Conduct"), material = true }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        var campaign = Id(await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/campaigns", new { }), HttpStatusCode.Created));
        return (id, campaign);
    }

    private static JsonElement Group(JsonElement view, string breakdown, Func<JsonElement, bool> match) =>
        view.GetProperty(breakdown).EnumerateArray().Single(match);

    private static (int Members, int Attested, double Percent) Counts(JsonElement e)
    {
        var c = e.GetProperty("counts");
        return (c.GetProperty("members").GetInt32(), c.GetProperty("attested").GetInt32(), c.GetProperty("percent").GetDouble());
    }

    private static Func<JsonElement, bool> Unit(Guid unit, bool direct = false) =>
        g => g.GetProperty("unitId").ValueKind != JsonValueKind.Null && g.GetProperty("unitId").GetGuid() == unit && g.GetProperty("direct").GetBoolean() == direct;

    private static string[] Names(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("person").GetProperty("displayName").GetString()!.Split(' ')[0]).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 10; i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Completion_rolls_up_by_unit_location_and_manager_and_drills_down_to_people_and_one_person()
    {
        var o = await FreshAsync();
        var t = o.T;
        foreach (var report in new[] { "ali", "bea" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[report].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        }
        var (policy, campaign) = await LaunchedAsync(o);
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);
        // Locations come from the directory; set two of them on the profile read model.
        await OkAsync(t["admin"].Client.GetAsync("/api/v1/people"));
        var located = await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set location_key = 'dubai' where user_id = any(@u)",
            ("u", new[] { t["ali"].Id, t["bea"].Id }));
        Assert.Equal(2, located);

        // The tenant compliance officer opens on the subsidiaries under the one root organization.
        var view = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance"));
        Assert.Equal(ComplianceScopes.Tenant, view.GetProperty("scope").GetString());
        Assert.Equal(campaign, view.GetProperty("campaign").GetProperty("id").GetGuid());
        Assert.Equal(o.Holding, view.GetProperty("unit").GetGuid());
        Assert.Equal((8, 1, 12.5), (view.GetProperty("totals").GetProperty("members").GetInt32(), view.GetProperty("totals").GetProperty("attested").GetInt32(),
            view.GetProperty("totals").GetProperty("percent").GetDouble()));
        Assert.Equal((6, 1, 16.7), Counts(Group(view, "byUnit", Unit(o.Gulf))));
        Assert.Equal((1, 0, 0.0), Counts(Group(view, "byUnit", Unit(o.Egypt))));
        // The administrator has no unit and is a line of their own until a unit is chosen.
        Assert.Equal((1, 0, 0.0), Counts(Group(view, "byUnit", g => g.GetProperty("unitId").ValueKind == JsonValueKind.Null)));
        Assert.Equal("Gulf", Group(view, "byUnit", Unit(o.Gulf)).GetProperty("unitNames").GetProperty("en").GetString());
        Assert.Equal((2, 1, 50.0), Counts(Group(view, "byLocation", g => g.GetProperty("location").GetString() == "dubai")));
        Assert.Equal((6, 0, 0.0), Counts(Group(view, "byLocation", g => g.GetProperty("location").ValueKind == JsonValueKind.Null)));
        var monaTeam = Group(view, "byManager", g => g.GetProperty("manager").ValueKind != JsonValueKind.Null);
        Assert.Equal(t["mona"].Id, monaTeam.GetProperty("manager").GetProperty("id").GetGuid());
        Assert.Equal((2, 1, 50.0), Counts(monaTeam));

        // Drill down into the Gulf: its own people and the Sales department below it.
        var gulf = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance?unit={o.Gulf}"));
        Assert.Equal([o.Holding, o.Gulf], gulf.GetProperty("unitPath").EnumerateArray().Select(u => u.GetGuid()));
        Assert.Equal((5, 1, 20.0), Counts(Group(gulf, "byUnit", Unit(o.Gulf, direct: true))));
        Assert.Equal((1, 0, 0.0), Counts(Group(gulf, "byUnit", Unit(o.Sales))));
        Assert.Equal(["bea"], Names(await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?unit={o.Sales}"))));
        Assert.Equal(["ali", "co", "gco", "legal", "mona"], Names(await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?unit={o.Gulf}&direct=true"))));
        Assert.Equal(["ali", "bea"], Names(await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?location=dubai"))));
        Assert.Equal(["bea"], Names(await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?manager={t["mona"].Id}&status=open"))));
        var paged = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?status=open&limit=2&offset=2"));
        Assert.Equal(7, paged.GetProperty("total").GetInt32());
        Assert.Equal(2, paged.GetProperty("items").GetArrayLength());
        var bad = await t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people?status=gone&limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badText = await bad.Content.ReadAsStringAsync();
        Assert.Contains("validation.policies.compliance_filter", badText);
        Assert.Contains("validation.policies.compliance_page", badText);

        // One person: every policy that applies to them, with the last version attested and when.
        var person = await OkAsync(t["co"].Client.GetAsync($"{Policies}/compliance/people/{t["ali"].Id}"));
        var item = person.GetProperty("policies").EnumerateArray().Single(p => p.GetProperty("policy").GetProperty("id").GetGuid() == policy);
        Assert.Equal(1, item.GetProperty("attestedVersion").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("attestedAt").ValueKind);
        var owing = (await OkAsync(t["co"].Client.GetAsync($"{Policies}/compliance/people/{t["bea"].Id}"))).GetProperty("policies").EnumerateArray().Single();
        Assert.Equal(campaign, owing.GetProperty("due").GetProperty("campaignId").GetGuid());
        Assert.Equal(JsonValueKind.Null, owing.GetProperty("attestedVersion").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await t["co"].Client.GetAsync($"{Policies}/compliance/people/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Org_scoped_officers_and_line_managers_see_only_their_people_and_others_see_nothing()
    {
        var o = await FreshAsync();
        var t = o.T;
        foreach (var report in new[] { "ali", "bea" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[report].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        }
        var (policy, campaign) = await LaunchedAsync(o);
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);

        // The Gulf compliance officer sees the Gulf and its departments, never Egypt.
        var gulf = await OkAsync(t["gco"].Client.GetAsync($"{Policies}/{policy}/compliance"));
        Assert.Equal(ComplianceScopes.Units, gulf.GetProperty("scope").GetString());
        Assert.Equal(6, gulf.GetProperty("totals").GetProperty("members").GetInt32());
        Assert.DoesNotContain(gulf.GetProperty("byUnit").EnumerateArray().ToList(), g => Unit(o.Egypt)(g));
        Assert.Empty((await OkAsync(t["gco"].Client.GetAsync($"{Policies}/{policy}/compliance/people?unit={o.Egypt}"))).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await OkAsync(t["gco"].Client.GetAsync($"{Policies}/{policy}/compliance?unit={o.Egypt}"))).GetProperty("totals").GetProperty("members").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await t["gco"].Client.GetAsync($"{Policies}/compliance/people/{t["cai"].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["gco"].Client.GetAsync($"{Policies}/compliance/people/{t["bea"].Id}")).StatusCode);

        // A line manager sees their two reports and reminds the one who has not attested.
        var team = await OkAsync(t["mona"].Client.GetAsync($"{Policies}/{policy}/compliance"));
        Assert.Equal(ComplianceScopes.Reports, team.GetProperty("scope").GetString());
        Assert.Equal((2, 1), (team.GetProperty("totals").GetProperty("members").GetInt32(), team.GetProperty("totals").GetProperty("attested").GetInt32()));
        Assert.Equal(["ali", "bea"], Names(await OkAsync(t["mona"].Client.GetAsync($"{Policies}/{policy}/compliance/people"))));
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Policies}/compliance/people/{t["cai"].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["mona"].Client.GetAsync($"{Policies}/compliance/people/{t["bea"].Id}")).StatusCode);
        var reminded = await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Policies}/{policy}/compliance/remind", new { }));
        Assert.Equal(campaign, reminded.GetProperty("campaignId").GetGuid());
        Assert.Equal(1, reminded.GetProperty("recipients").GetInt32());
        // Naming someone outside the team reminds nobody.
        Assert.Equal(0, (await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Policies}/{policy}/compliance/remind", new { userIds = new[] { t["cai"].Id } })))
            .GetProperty("recipients").GetInt32());
        await DispatchAsync();
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["bea"].Id), ("k", PolicyCampaigns.ReminderTemplate)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["cai"].Id), ("k", PolicyCampaigns.ReminderTemplate)));

        // An employee with no reports and no compliance role is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["cai"].Client.GetAsync($"{Policies}/{policy}/compliance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["cai"].Client.GetAsync($"{Policies}/compliance/people/{t["ali"].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["cai"].Client.PostAsJsonAsync($"{Policies}/{policy}/compliance/remind", new { })).StatusCode);

        // A closed campaign takes no reminders.
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{policy}/campaigns/{campaign}/close", null));
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsJsonAsync($"{Policies}/{policy}/compliance/remind", new { })).StatusCode);

        // Another tenant's compliance officer finds neither the policy nor the person.
        var stranger = (await world.CreateTenantAsync("pq-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus"))["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{policy}/compliance")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{policy}/compliance/people")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{Policies}/{policy}/compliance/remind", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/compliance/people/{t["ali"].Id}")).StatusCode);
    }
}
