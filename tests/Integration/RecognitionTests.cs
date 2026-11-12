using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T010 (US4, FR-011 to FR-013): shout-outs to people or a team with a company value and a badge the giver
/// may give, no self recognition, the wall and the profile (only where the person allows it), notices to the people
/// thanked and their managers, points per action within the daily cap and the giver's daily limit, points switched
/// off hiding every count and leaderboard, leaderboards off by default and leaving out people who opted out, and
/// tenant isolation. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RecognitionTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    private async Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        await world.Database.TenantScalarAsync<long>(t.Id, "select coalesce(sum(group_count), 0)::bigint from notifications.notification where user_id = @u and template_key = @k",
            ("u", t[user].Id), ("k", template));

    private async Task<TestTenant> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("rc-" + Guid.NewGuid().ToString("N")[..8], package);
        var finance = await world.CreateUnitAsync(t, "Finance", null, "department");
        await world.CreateUnitAsync(t, "Sales", null, "department");
        foreach (var name in new[] { "sara", "ahmed", "mona", "kofi", "lina" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "sara", "Finance");
        await world.AddMembershipAsync(t, "ahmed", "Finance");
        await world.AddMembershipAsync(t, "kofi", "Sales");
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["ahmed"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/settings/recognition", new
        {
            values = new[] { new { key = "teamwork", names = new Dictionary<string, string> { ["en"] = "Teamwork", ["ar"] = "العمل الجماعي" } } },
            badges = new object[]
            {
                new { key = "month_end_hero", names = new Dictionary<string, string> { ["en"] = "Month-end hero" }, icon = "star", giverUnitId = finance },
                new { key = "helping_hand", names = new Dictionary<string, string> { ["en"] = "Helping hand" }, icon = "people", giverUnitId = (Guid?)null },
            },
        }));
        return t;
    }

    private static Guid Find(JsonElement list, string key) =>
        list.EnumerateArray().Single(x => x.GetProperty("key").GetString() == key).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> ThankAsync(HttpClient client, Guid[] recipients, string message = "Thank you for the help with month-end closing", Guid? value = null,
        Guid? badge = null, Guid? team = null) =>
        client.PostAsJsonAsync($"{Api}/recognitions", new { recipients, teamUnitId = team, message, valueId = value, badgeId = badge });

    private static async Task<int?> PointsAsync(HttpClient client)
    {
        var response = await client.GetAsync($"{Api}/points/me");
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await OkAsync(Task.FromResult(response))).GetProperty("today").GetInt32();
    }

    private static async Task SetPointsAsync(TestTenant t, object settings)
    {
        var current = await t["admin"].Client.GetAsync($"{Api}/settings/points");
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/points") { Content = JsonContent.Create(settings) };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(t["admin"].Client.SendAsync(request));
    }

    [Fact]
    public async Task A_shout_out_reaches_the_wall_the_person_and_their_manager_and_earns_points()
    {
        var t = await SetupAsync();
        var options = await OkAsync(t["sara"].Client.GetAsync($"{Api}/recognition/options"));
        var teamwork = Find(options.GetProperty("values"), "teamwork");
        var hero = Find(options.GetProperty("badges"), "month_end_hero");
        Assert.True(options.GetProperty("pointsOn").GetBoolean());
        Assert.False(options.GetProperty("leaderboard").GetBoolean());

        // A badge limited to Finance is offered to Sara (Finance) but not to Kofi (Sales), and Kofi cannot give it.
        var kofiBadges = (await OkAsync(t["kofi"].Client.GetAsync($"{Api}/recognition/options"))).GetProperty("badges");
        Assert.DoesNotContain(kofiBadges.EnumerateArray(), b => b.GetProperty("id").GetGuid() == hero);
        Assert.Contains("validation.engagement.recognition_badge", await ErrorKeysAsync(await ThankAsync(t["kofi"].Client, [t["ahmed"].Id], badge: hero)));

        // Nobody recognizes themselves.
        Assert.Contains("validation.engagement.recognition_self", await ErrorKeysAsync(await ThankAsync(t["sara"].Client, [t["sara"].Id])));
        Assert.Contains("validation.engagement.recognition_recipients", await ErrorKeysAsync(await ThankAsync(t["sara"].Client, [])));

        var given = await OkAsync(ThankAsync(t["sara"].Client, [t["ahmed"].Id], value: teamwork, badge: hero), HttpStatusCode.Created);
        var id = given.GetProperty("id").GetGuid();
        Assert.Equal("Teamwork", given.GetProperty("value").GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("star", given.GetProperty("badge").GetProperty("icon").GetString());
        Assert.Equal(t["ahmed"].Id, Assert.Single(given.GetProperty("recipients").EnumerateArray()).GetProperty("id").GetGuid());

        var wall = await OkAsync(t["kofi"].Client.GetAsync($"{Api}/recognitions"));
        Assert.Equal(id, wall.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.False(wall.GetProperty("items")[0].GetProperty("canRemove").GetBoolean());
        Assert.Equal(1, await NotificationsAsync(t, "ahmed", "engagement.recognition.received"));
        Assert.Equal(1, await NotificationsAsync(t, "mona", "engagement.recognition.report"));
        Assert.Equal(0, await NotificationsAsync(t, "sara", "engagement.recognition.received"));
        Assert.Equal(1, await world.Database.OutboxCountAsync("RecognitionGiven", t.Id));

        // The default 10 points for a shout-out received.
        Assert.Equal(10, await PointsAsync(t["ahmed"].Client));
        Assert.Equal(0, await PointsAsync(t["sara"].Client));

        // On Ahmed's profile until he hides it; hidden looks the same as having none. He still sees his own.
        Assert.Single((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/people/{t["ahmed"].Id}/recognitions"))).EnumerateArray());
        await OkAsync(t["ahmed"].Client.PutAsJsonAsync($"{Api}/recognition/preferences", new { showOnProfile = false }));
        Assert.Empty((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/people/{t["ahmed"].Id}/recognitions"))).EnumerateArray());
        Assert.Single((await OkAsync(t["ahmed"].Client.GetAsync($"{Api}/people/{t["ahmed"].Id}/recognitions"))).EnumerateArray());

        // A team shout-out names the unit and earns nobody points.
        var team = await OkAsync(ThankAsync(t["kofi"].Client, [], "Great quarter, Finance", team: t.Unit("Finance")), HttpStatusCode.Created);
        Assert.Equal(t.Unit("Finance"), team.GetProperty("team").GetProperty("unitId").GetGuid());
        Assert.Equal(10, await PointsAsync(t["ahmed"].Client));

        // Only the giver or an engagement administrator removes a shout-out, and its points go with it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.DeleteAsync($"{Api}/recognitions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["sara"].Client.DeleteAsync($"{Api}/recognitions/{id}")).StatusCode);
        Assert.Equal(0, await PointsAsync(t["ahmed"].Client));
        Assert.DoesNotContain((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/recognitions"))).GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task Points_keep_to_the_caps_and_disappear_when_switched_off()
    {
        var t = await SetupAsync();
        // Only the giver's first shout-outs of the day count: with a limit of 2, the third one earns nothing.
        await SetPointsAsync(t, new { giverDailyLimit = 2, dailyCap = 25 });
        for (var i = 0; i < 3; i++)
        {
            await OkAsync(ThankAsync(t["sara"].Client, [t["ahmed"].Id], $"Thanks again, number {i + 1}"), HttpStatusCode.Created);
        }
        Assert.Equal(20, await PointsAsync(t["ahmed"].Client));
        // The person's daily cap of 25 stops the rest: Kofi's shout-out gives only 5 more.
        await OkAsync(ThankAsync(t["kofi"].Client, [t["ahmed"].Id]), HttpStatusCode.Created);
        Assert.Equal(25, await PointsAsync(t["ahmed"].Client));

        // A named poll vote and a post earn points too; an anonymous poll vote does not.
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = new Dictionary<string, string> { ["en"] = "Lunch club" }, kind = "open" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["kofi"].Client.PostAsync($"{Api}/communities/{community}:join", null));
        foreach (var anonymous in new[] { false, true })
        {
            var poll = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities/{community}/polls", new
            {
                question = "Pizza or salad?", options = new[] { "Pizza", "Salad" }, anonymous, closesAt = DateTimeOffset.UtcNow.AddDays(1),
            }), HttpStatusCode.Created);
            await OkAsync(t["kofi"].Client.PutAsJsonAsync($"{Api}/polls/{poll.GetProperty("id").GetGuid()}/vote",
                new { optionIds = new[] { poll.GetProperty("options")[0].GetProperty("id").GetGuid() } }));
        }
        await OkAsync(t["kofi"].Client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new
        {
            body = new { blocks = new[] { new { type = "paragraph", children = new[] { new { text = "Who is in for lunch?" } } } } },
        }), HttpStatusCode.Created);
        await world.DispatchTenantAsync(t.Id);
        // Kofi gave a shout-out (which earns the giver nothing), voted once in a named poll (1) and posted (2).
        Assert.Equal(3, await PointsAsync(t["kofi"].Client));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from engagement.point_entry e join engagement.poll p on p.id = e.source_id where p.anonymous"));

        // Switched off, points are gone everywhere: no counts, no leaderboard, nothing new recorded.
        await SetPointsAsync(t, new { enabled = false });
        Assert.Null(await PointsAsync(t["ahmed"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await t["ahmed"].Client.GetAsync($"{Api}/leaderboard")).StatusCode);
        Assert.False((await OkAsync(t["sara"].Client.GetAsync($"{Api}/recognition/options"))).GetProperty("pointsOn").GetBoolean());
        var before = await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.point_entry");
        await OkAsync(ThankAsync(t["lina"].Client, [t["ahmed"].Id]), HttpStatusCode.Created);
        Assert.Equal(before, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.point_entry"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementPointsConfigChanged);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementRecognitionCatalogueChanged);

        // Settings are for engagement administrators only and need the current version.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Api}/settings/points")).StatusCode);
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/points") { Content = JsonContent.Create(new { enabled = true }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"p1\"");
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Leaderboards_are_off_until_turned_on_and_leave_out_people_who_opted_out()
    {
        var t = await SetupAsync();
        await OkAsync(ThankAsync(t["sara"].Client, [t["ahmed"].Id]), HttpStatusCode.Created);
        await OkAsync(ThankAsync(t["ahmed"].Client, [t["kofi"].Id]), HttpStatusCode.Created);
        await OkAsync(ThankAsync(t["sara"].Client, [t["kofi"].Id], "And again"), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"{Api}/leaderboard")).StatusCode);

        await SetPointsAsync(t, new { leaderboard = true });
        var board = await OkAsync(t["sara"].Client.GetAsync($"{Api}/leaderboard?period=month"));
        Assert.Equal(new[] { t["kofi"].Id, t["ahmed"].Id }, board.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("person").GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(20, board.GetProperty("entries")[0].GetProperty("points").GetInt32());

        // Per org unit: Finance shows only Ahmed. Kofi opts out and leaves the tenant board.
        var finance = await OkAsync(t["sara"].Client.GetAsync($"{Api}/leaderboard?period=week&unit={t.Unit("Finance")}"));
        Assert.Equal(t["ahmed"].Id, Assert.Single(finance.GetProperty("entries").EnumerateArray()).GetProperty("person").GetProperty("id").GetGuid());
        await OkAsync(t["kofi"].Client.PutAsJsonAsync($"{Api}/recognition/preferences", new { leaderboardOptOut = true }));
        var after = await OkAsync(t["sara"].Client.GetAsync($"{Api}/leaderboard?period=quarter"));
        Assert.DoesNotContain(after.GetProperty("entries").EnumerateArray(), e => e.GetProperty("person").GetProperty("id").GetGuid() == t["kofi"].Id);
        Assert.Contains("validation.engagement.leaderboard_period", await ErrorKeysAsync(await t["sara"].Client.GetAsync($"{Api}/leaderboard?period=year")));
    }

    [Fact]
    public async Task Recognition_stays_in_its_tenant_and_points_need_the_package()
    {
        var t = await SetupAsync();
        var id = (await OkAsync(ThankAsync(t["sara"].Client, [t["ahmed"].Id]), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var other = await SetupAsync();
        Assert.DoesNotContain((await OkAsync(other["sara"].Client.GetAsync($"{Api}/recognitions"))).GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.DeleteAsync($"{Api}/recognitions/{id}")).StatusCode);
        // A person from another tenant has no profile here.
        Assert.Equal(HttpStatusCode.NotFound, (await other["sara"].Client.GetAsync($"{Api}/people/{t["ahmed"].Id}/recognitions")).StatusCode);
        // Someone from another tenant is not a valid recipient.
        Assert.Contains("validation.engagement.recognition_recipient", await ErrorKeysAsync(await ThankAsync(other["sara"].Client, [t["ahmed"].Id])));

        // Workplace has recognition but no points: shout-outs work, and nothing about points shows.
        var w = await SetupAsync("workplace");
        await OkAsync(ThankAsync(w["sara"].Client, [w["ahmed"].Id]), HttpStatusCode.Created);
        Assert.Null(await PointsAsync(w["ahmed"].Client));
        Assert.False((await OkAsync(w["sara"].Client.GetAsync($"{Api}/recognition/options"))).GetProperty("pointsOn").GetBoolean());
        Assert.Equal(0, await world.Database.CountInTenantAsync(w.Id, "select count(*) from engagement.point_entry"));
    }
}
