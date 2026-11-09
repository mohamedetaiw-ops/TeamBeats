using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T005 and T006 (US2, FR-006 to FR-008): launch creates inbox tasks, notices and My policies entries; the
/// attestation record with its hash never changes; reminders before the due date, overdue after it with the line
/// manager's list; late joiners with their own due date and leavers marked; the material revision choice; contractors
/// left out; one open campaign without <c>policy.campaigns</c>; other tenants and people outside reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyCampaignTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pc-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali", "bea", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "con", MembershipTypes.Contractor);
        await world.AddMembershipAsync(t, "con", "Gulf");
        await world.SeedUserAsync(t, "cai", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "cai", "Egypt");
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return new Org(t, gulf, egypt);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

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

    private static readonly Dictionary<string, string> Titles = new() { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" };

    /// <summary>A policy with one approval step by legal, published as version 1.</summary>
    private async Task<Guid> PublishedAsync(Org o, string code, Guid? unit = null)
    {
        var t = o.T;
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = Titles, orgUnitId = unit, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        await ReviseAsync(o, id, "Conduct", material: true);
        return id;
    }

    private async Task ReviseAsync(Org o, Guid id, string text, bool material)
    {
        var t = o.T;
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Titles, clauses = Clauses(text), material }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
    }

    private static async Task<JsonElement[]> AttestationTasksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(t => t.GetProperty("source").GetString() == "policy_attestation").ToArray();

    private async Task DispatchUntilAsync(Func<Task<bool>> done)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 50 && !await done(); i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
        Assert.True(await done());
    }

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PolicyCampaigns>().RunDueAsync(now, 1000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private Task<long> MembersAsync(TestTenant t, Guid campaign, string status) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.campaign_member where campaign_id = @c and status = @s", ("c", campaign), ("s", status));

    [Fact]
    public async Task A_campaign_asks_its_audience_and_each_attestation_is_a_fixed_record_then_reminders_and_overdue_follow()
    {
        var o = await FreshAsync();
        var t = o.T;
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["bea"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        var policy = await PublishedAsync(o, "COC");

        // Contractors are left out; validation of the settings first.
        Assert.Contains("validation.policies.reminder_days", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns",
            new { reminderDays = new[] { 7, 7 } })));
        Assert.Contains("validation.policies.deadline", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns",
            new { deadline = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { })).StatusCode);
        var campaign = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns",
            new { excludedMembershipTypes = new[] { MembershipTypes.Contractor } }), HttpStatusCode.Created);
        var campaignId = Id(campaign);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCampaignLaunched, policy.ToString());
        Assert.Equal(1, campaign.GetProperty("version").GetInt32());
        Assert.Equal([7, 2], campaign.GetProperty("reminderDays").EnumerateArray().Select(d => d.GetInt32()));
        Assert.Equal("قرأت هذه السياسة وفهمتها وسألتزم بها.", campaign.GetProperty("attestationText").GetProperty("ar").GetString());
        Assert.Contains("conflict.policies.campaign_open", await ErrorKeysAsync(await t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { })));

        // Every employee has the task in the inbox, the notice and the action on My policies; the contractor has nothing.
        var task = Assert.Single(await AttestationTasksAsync(t["ali"].Client));
        Assert.Equal($"/policies/{policy}?attest=1", task.GetProperty("link").GetString());
        Assert.Equal("Code of conduct", task.GetProperty("title").GetProperty("en").GetString());
        Assert.Empty(await AttestationTasksAsync(t["con"].Client));
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "ali", PolicyCampaigns.AttestationDueTemplate) == 1);
        Assert.Equal(0, await NotificationsAsync(t, "con", PolicyCampaigns.AttestationDueTemplate));
        var mine = Assert.Single((await OkAsync(t["ali"].Client.GetAsync("/api/v1/me/policies"))).EnumerateArray());
        Assert.Equal(campaignId, mine.GetProperty("due").GetProperty("campaignId").GetGuid());
        var counter = await OkAsync(t["ali"].Client.GetAsync("/api/v1/tasks/count"));
        Assert.True(counter.GetProperty("open").GetInt32() >= 1);

        // Ali attests in Arabic on mobile: the record keeps the text, its hash and the channel, and it cannot change.
        var due = await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{policy}/attestation"));
        Assert.Equal(1, due.GetProperty("due").GetProperty("version").GetInt32());
        Assert.Contains("validation.policies.channel", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { channel = "paper" })));
        var record = await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { locale = "ar", channel = "mobile" }), HttpStatusCode.Created);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyAttested, policy.ToString());
        Assert.Equal(64, record.GetProperty("textHash").GetString()!.Length);
        Assert.Equal("mobile", record.GetProperty("channel").GetString());
        Assert.Equal("ar", record.GetProperty("locale").GetString());
        Assert.Contains("conflict.policies.nothing_to_attest", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { })));
        Assert.Contains("conflict.policies.nothing_to_attest", await ErrorKeysAsync(await t["con"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { })));
        Assert.Empty(await AttestationTasksAsync(t["ali"].Client));
        mine = Assert.Single((await OkAsync(t["ali"].Client.GetAsync("/api/v1/me/policies"))).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("due").ValueKind);
        Assert.Equal(1, mine.GetProperty("attestedVersion").GetInt32());
        foreach (var sql in new[] { "update policies.attestation set channel = 'web'", "delete from policies.attestation" })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<IDbSession>().ExecAsync(sql);
                return 0;
            }));
            Assert.Equal("42501", refused.SqlState);
        }

        // Seven days before the deadline the rest are reminded once; after it they are overdue and Bea's manager hears.
        var deadline = campaign.GetProperty("deadline").GetDateTimeOffset();
        Assert.True(await RunDueAsync(t, deadline.AddDays(-7).AddHours(1)) > 0);
        Assert.Equal(0, await RunDueAsync(t, deadline.AddDays(-7).AddHours(2)));
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "bea", PolicyCampaigns.ReminderTemplate) == 1);
        Assert.Equal(0, await NotificationsAsync(t, "ali", PolicyCampaigns.ReminderTemplate));
        Assert.True(await RunDueAsync(t, deadline.AddHours(1)) > 0);
        Assert.Equal(1, await NotificationsAsync(t, "mona", PolicyCampaigns.TeamOverdueTemplate));
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "bea", PolicyCampaigns.OverdueTemplate) == 1);
        var view = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/campaigns/{campaignId}"));
        Assert.Equal(1, view.GetProperty("counts").GetProperty("attested").GetInt32());
        Assert.Equal(0, view.GetProperty("counts").GetProperty("open").GetInt32());
        Assert.True(view.GetProperty("counts").GetProperty("overdue").GetInt32() >= 3);
        // Overdue people can still attest; the task shows as overdue meanwhile.
        Assert.True(Assert.Single((await OkAsync(t["bea"].Client.GetAsync("/api/v1/me/policies"))).EnumerateArray()).GetProperty("due").GetProperty("overdue").GetBoolean());

        // Compliance reminds the rest by hand, then closes the campaign: its tasks leave the inbox.
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{policy}/campaigns/{campaignId}/remind", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCampaignReminded, policy.ToString());
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{policy}/campaigns/{campaignId}/close", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCampaignClosed, policy.ToString());
        Assert.Empty(await AttestationTasksAsync(t["bea"].Client));
        Assert.Contains("conflict.policies.campaign_closed", await ErrorKeysAsync(await t["co"].Client.PostAsync($"{Policies}/{policy}/campaigns/{campaignId}/close", null)));

        // A yearly re-attestation of the same version is a new campaign; Ali attests again.
        var again = Id(await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { }), HttpStatusCode.Created));
        await OkAsync(t["ali"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { campaignId = again }), HttpStatusCode.Created);
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.attestation where user_id = @u", ("u", t["ali"].Id)));
    }

    [Fact]
    public async Task Late_joiners_get_their_own_due_date_leavers_are_marked_and_a_material_revision_leaves_the_choice_to_compliance()
    {
        var o = await FreshAsync();
        var t = o.T;
        var policy = await PublishedAsync(o, "GULF-1", o.Gulf);
        var campaignId = Id(await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { }), HttpStatusCode.Created));
        // A Gulf policy: the Egypt employee is not asked and cannot see it.
        Assert.Empty(await AttestationTasksAsync(t["cai"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { })).StatusCode);

        // A transfer into the Gulf joins with 14 days from today.
        await world.AddMembershipAsync(t, "cai", "Gulf", "secondary");
        await DispatchUntilAsync(async () => await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from policies.campaign_member where campaign_id = @c and user_id = @u and status = 'open'", ("c", campaignId), ("u", t["cai"].Id)) == 1);
        var due = Assert.Single(await AttestationTasksAsync(t["cai"].Client)).GetProperty("dueAt").GetDateTimeOffset();
        Assert.InRange(due, DateTimeOffset.UtcNow.AddDays(13.9), DateTimeOffset.UtcNow.AddDays(14.1));

        // Ali leaves the Gulf before attesting: out of the open list, kept as left audience.
        var membership = (await OkAsync(t["admin"].Client.GetAsync($"/api/v1/org/units/{o.Gulf}/memberships"))).EnumerateArray()
            .First(m => m.GetProperty("userId").GetGuid() == t["ali"].Id).GetProperty("id").GetGuid();
        Assert.True((await t["admin"].Client.PostAsync($"/api/v1/org/memberships/{membership}:end", null)).IsSuccessStatusCode);
        await DispatchUntilAsync(async () => await MembersAsync(t, campaignId, MemberStatuses.LeftAudience) == 1);
        Assert.Empty(await AttestationTasksAsync(t["ali"].Client));

        // A material revision comes into force: compliance and the owner are told; the campaign stays for version 1 until they choose.
        await ReviseAsync(o, policy, "Conduct revised", material: true);
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "co", PolicyCampaigns.OutdatedTemplate) == 1);
        var record = await OkAsync(t["bea"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);
        Assert.Equal(1, record.GetProperty("version").GetInt32());
        // They close it and launch one for version 2.
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{policy}/campaigns/{campaignId}/close", null));
        var next = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { }), HttpStatusCode.Created);
        Assert.Equal(2, next.GetProperty("version").GetInt32());
        Assert.Equal(2, (await OkAsync(t["bea"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created)).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Without_unlimited_campaigns_one_is_open_at_a_time_and_other_tenants_and_employees_reach_no_campaign()
    {
        var o = await FreshAsync("workplace");
        var t = o.T;
        var first = await PublishedAsync(o, "P-1");
        var second = await PublishedAsync(o, "P-2");
        var campaignId = Id(await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{first}/campaigns", new { }), HttpStatusCode.Created));
        var refused = await t["co"].Client.PostAsJsonAsync($"{Policies}/{second}/campaigns", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement", await refused.Content.ReadAsStringAsync());

        // Without escalation in the package, overdue people hear it but managers get no list.
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["bea"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        var deadline = (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{first}/campaigns/{campaignId}"))).GetProperty("deadline").GetDateTimeOffset();
        Assert.True(await RunDueAsync(t, deadline.AddHours(1)) > 0);
        Assert.Equal(0, await NotificationsAsync(t, "mona", PolicyCampaigns.TeamOverdueTemplate));

        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{first}/campaigns/{campaignId}/close", null));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{second}/campaigns", new { }), HttpStatusCode.Created);

        // Employees do not run campaigns; another tenant finds nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync($"{Policies}/{first}/campaigns")).StatusCode);
        var stranger = (await world.CreateTenantAsync("pc-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus"))["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{first}/campaigns/{campaignId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"{Policies}/{first}/campaigns/{campaignId}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{Policies}/{first}/attest", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Policies}/{first}/attestation")).StatusCode);
        Assert.Empty((await OkAsync(stranger.GetAsync("/api/v1/me/policies"))).EnumerateArray());
    }
}
