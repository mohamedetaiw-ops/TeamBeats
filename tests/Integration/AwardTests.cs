using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T012 (US5, FR-014): an engagement administrator opens a period for a person or a team award, people
/// nominate someone in scope with a reason (never themselves, one nomination each, changed until close), only
/// recognition approvers in scope see the nominations and pick winners from them, which ends the period, tells the
/// winners and starts an announcement draft in content for communicators. Also tenant isolation. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AwardTests(TenantWorld world)
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

    private sealed record Setup(TestTenant T, Guid Finance, Guid Sales);

    private async Task<Setup> SetupAsync()
    {
        var t = await world.CreateTenantAsync("aw-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "department");
        var sales = await world.CreateUnitAsync(t, "Sales", null, "department");
        foreach (var name in new[] { "sara", "ahmed", "mona", "kofi", "lina" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "sara", "Finance");
        await world.AddMembershipAsync(t, "ahmed", "Finance");
        await world.AddMembershipAsync(t, "kofi", "Sales");
        // Mona approves recognition for Finance and everything below it, and nowhere else.
        await world.AssignAsync(t, "mona", BuiltInRoles.EngagementAdministrator, "org_subtree", finance);
        return new Setup(t, finance, sales);
    }

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client, string kind, Guid? unit, DateOnly? closeOn = null, string title = "Star of the quarter") =>
        client.PostAsJsonAsync($"{Api}/awards", new { kind, title, orgUnitId = unit, nominationsCloseOn = closeOn ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7) });

    private static Task<HttpResponseMessage> NominateAsync(HttpClient client, Guid award, Guid nominee, string reason = "Led the month-end closing with patience") =>
        client.PutAsJsonAsync($"{Api}/awards/{award}/nomination", new { nomineeId = nominee, reason });

    [Fact]
    public async Task People_nominate_approvers_in_scope_choose_and_an_announcement_draft_starts()
    {
        var (t, finance, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await OpenAsync(t["sara"].Client, "person", null)).StatusCode);
        var award = await OkAsync(OpenAsync(t["admin"].Client, "person", finance), HttpStatusCode.Created);
        var id = award.GetProperty("id").GetGuid();
        Assert.True(award.GetProperty("nominationsOpen").GetBoolean());

        // Nominations: in scope only, never yourself, one each and replaced when sent again.
        Assert.Contains("validation.engagement.award_self", await ErrorKeysAsync(await NominateAsync(t["sara"].Client, id, t["sara"].Id)));
        Assert.Contains("validation.engagement.award_nominee", await ErrorKeysAsync(await NominateAsync(t["lina"].Client, id, t["kofi"].Id)));
        Assert.Contains("validation.engagement.award_reason", await ErrorKeysAsync(await NominateAsync(t["lina"].Client, id, t["ahmed"].Id, " ")));
        await OkAsync(NominateAsync(t["sara"].Client, id, t["ahmed"].Id));
        await OkAsync(NominateAsync(t["kofi"].Client, id, t["sara"].Id));
        var mine = await OkAsync(NominateAsync(t["kofi"].Client, id, t["ahmed"].Id, "Always helps the new joiners"));
        Assert.Equal(t["ahmed"].Id, mine.GetProperty("myNomination").GetProperty("nominee").GetProperty("id").GetGuid());
        await OkAsync(NominateAsync(t["lina"].Client, id, t["sara"].Id));

        // Only approvers in scope see nominations; others see their own and nothing else.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.GetAsync($"{Api}/awards/{id}/nominations")).StatusCode);
        Assert.False((await OkAsync(t["sara"].Client.GetAsync($"{Api}/awards/{id}"))).GetProperty("canDecide").GetBoolean());
        Assert.True((await OkAsync(t["mona"].Client.GetAsync($"{Api}/awards/{id}"))).GetProperty("canDecide").GetBoolean());
        var nominations = (await OkAsync(t["mona"].Client.GetAsync($"{Api}/awards/{id}/nominations"))).EnumerateArray().ToList();
        Assert.Equal(2, nominations.Count);
        Assert.Equal(t["ahmed"].Id, nominations[0].GetProperty("nominee").GetProperty("id").GetGuid());
        Assert.Equal(2, nominations[0].GetProperty("count").GetInt32());

        // Winners come from the nominees; deciding ends the period, tells the winner and drafts the announcement.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.PostAsJsonAsync($"{Api}/awards/{id}/winners", new { nomineeIds = new[] { t["ahmed"].Id } })).StatusCode);
        Assert.Contains("validation.engagement.award_winners",
            await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync($"{Api}/awards/{id}/winners", new { nomineeIds = new[] { t["kofi"].Id } })));
        var decided = await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Api}/awards/{id}/winners", new { nomineeIds = new[] { t["ahmed"].Id } }));
        Assert.Equal("decided", decided.GetProperty("status").GetString());
        var item = decided.GetProperty("announcementItemId").GetGuid();
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from content.content_item where id = '{item}' and type = 'announcement' and status = 'draft'"));
        var draft = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/content/items/{item}"));
        Assert.Contains("Star of the quarter", draft.ToString());
        Assert.Contains("ahmed", draft.ToString());
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id,
            "select coalesce(sum(group_count), 0)::bigint from notifications.notification where user_id = @u and template_key = 'engagement.award.won'", ("u", t["ahmed"].Id)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from audit.audit_event where target_id = '{id}' and action = '{AuditActions.EngagementAwardDecided}'"));

        var seen = await OkAsync(t["sara"].Client.GetAsync($"{Api}/awards/{id}"));
        Assert.Equal(t["ahmed"].Id, seen.GetProperty("winners")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("announcementItemId").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await NominateAsync(t["lina"].Client, id, t["ahmed"].Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsJsonAsync($"{Api}/awards/{id}/winners", new { nomineeIds = new[] { t["sara"].Id } })).StatusCode);
    }

    [Fact]
    public async Task Team_awards_take_units_in_scope_and_close_after_the_last_day()
    {
        var (t, finance, sales) = await SetupAsync();
        var past = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        Assert.Contains("validation.engagement.award_close_on", await ErrorKeysAsync(await OpenAsync(t["admin"].Client, "team", null, past)));
        Assert.Contains("validation.engagement.award_kind", await ErrorKeysAsync(await OpenAsync(t["admin"].Client, "cat", null)));
        var id = (await OkAsync(OpenAsync(t["admin"].Client, "team", null, title: "Team of the year"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from audit.audit_event where target_id = '{id}' and action = '{AuditActions.EngagementAwardOpened}'"));
        Assert.Contains("validation.engagement.award_nominee", await ErrorKeysAsync(await NominateAsync(t["sara"].Client, id, Guid.NewGuid())));
        Assert.Contains("validation.engagement.award_nominee", await ErrorKeysAsync(await NominateAsync(t["sara"].Client, id, t["ahmed"].Id)));
        var mine = await OkAsync(NominateAsync(t["sara"].Client, id, sales, "Closed the biggest deal of the year together"));
        Assert.Equal("Sales", mine.GetProperty("myNomination").GetProperty("nominee").GetProperty("unitNames").GetProperty("en").GetString());

        // A tenant-wide award needs a tenant-wide approver: Mona approves only for Finance.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync($"{Api}/awards/{id}/nominations")).StatusCode);
        Assert.Single((await OkAsync(t["admin"].Client.GetAsync($"{Api}/awards/{id}/nominations"))).EnumerateArray());

        await world.Database.OwnerExecInTenantAsync(t.Id, "update engagement.award_period set nominations_close_on = current_date - 1 where id = @id", ("id", id));
        Assert.False((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/awards/{id}"))).GetProperty("nominationsOpen").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await NominateAsync(t["kofi"].Client, id, finance)).StatusCode);
        // Approvers still choose after nominations close.
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/awards/{id}/winners", new { nomineeIds = new[] { sales } }));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where template_key = 'engagement.award.won'"));
    }

    [Fact]
    public async Task Awards_stay_in_their_tenant()
    {
        var (t, _, _) = await SetupAsync();
        var id = (await OkAsync(OpenAsync(t["admin"].Client, "person", null), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(NominateAsync(t["sara"].Client, id, t["ahmed"].Id));
        var (other, _, _) = await SetupAsync();
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync($"{Api}/awards"))).GetProperty("periods").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/awards/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/awards/{id}/nominations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await NominateAsync(other["sara"].Client, id, other["ahmed"].Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Api}/awards/{id}/winners", new { nomineeIds = new[] { t["ahmed"].Id } })).StatusCode);
        // Someone from another tenant cannot be nominated.
        var local = (await OkAsync(OpenAsync(other["admin"].Client, "person", null), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Contains("validation.engagement.award_nominee", await ErrorKeysAsync(await NominateAsync(other["sara"].Client, local, t["ahmed"].Id)));
    }
}
