using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T020 (edge cases, long leave): a compliance officer pauses a person's attestation deadline until the day
/// they are back, with a reason. The deadline moves by the time paused, an overdue person is open again, no reminder or
/// overdue notice goes out meanwhile, and the compliance list shows the pause without the reason. Employees cannot
/// pause, someone who has attested has nothing to pause, and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyPauseTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pps-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali", "bea", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
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

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

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
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    private static Task<HttpResponseMessage> PauseAsync(HttpClient client, Guid policy, Guid campaign, Guid user, object body) =>
        client.PostAsJsonAsync($"{Policies}/{policy}/campaigns/{campaign}/members/{user}/pause", body);

    private static string Day(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days).ToString("yyyy-MM-dd");

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PolicyCampaigns>().RunDueAsync(now, 1000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private Task<long> CountAsync(TestTenant t, Guid campaign, string user, string condition) =>
        world.Database.CountInTenantAsync(t.Id, $"select count(*) from policies.campaign_member where campaign_id = @c and user_id = @u and {condition}",
            ("c", campaign), ("u", t[user].Id));

    private async Task<DateTimeOffset> DueAsync(TestTenant t, Guid policy, string user) =>
        (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/compliance/people"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("person").GetProperty("id").GetGuid() == t[user].Id).GetProperty("dueAt").GetDateTimeOffset();

    [Fact]
    public async Task A_pause_moves_the_deadline_and_holds_reminders_and_overdue_until_the_person_is_back()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var launched = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new { }), HttpStatusCode.Created);
        var campaign = launched.GetProperty("id").GetGuid();
        var deadline = launched.GetProperty("deadline").GetDateTimeOffset();
        await world.DispatchTenantAsync(t.Id);
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/attest", new { campaignId = campaign }), HttpStatusCode.Created);

        // Only someone who runs the campaign pauses; dates and the reason are checked; only someone who owes it.
        Assert.Equal(HttpStatusCode.Forbidden, (await PauseAsync(t["ali"].Client, aup, campaign, t["bea"].Id, new { until = Day(60), reason = "Maternity leave" })).StatusCode);
        var bad = await ErrorKeysAsync(await PauseAsync(t["co"].Client, aup, campaign, t["bea"].Id, new { until = Day(-1), reason = "" }));
        Assert.Contains("validation.policies.pause_until", bad);
        Assert.Contains("validation.policies.pause_reason", bad);
        Assert.Contains("validation.policies.pause_until", await ErrorKeysAsync(await PauseAsync(t["co"].Client, aup, campaign, t["bea"].Id,
            new { until = Day(PolicyCampaigns.MaxPauseDays + 2), reason = "Leave" })));
        Assert.Contains("validation.policies.pause_until", await ErrorKeysAsync(await PauseAsync(t["co"].Client, aup, campaign, t["bea"].Id, new { reason = "Leave" })));
        Assert.Contains("validation.policies.pause_reason", await ErrorKeysAsync(await PauseAsync(t["co"].Client, aup, campaign, t["bea"].Id,
            new { until = Day(60), reason = new string('x', PolicyCampaigns.MaxPauseReasonLength + 1) })));
        Assert.Contains("conflict.policies.pause_not_owed", await ErrorKeysAsync(await PauseAsync(t["co"].Client, aup, campaign, t["co"].Id, new { until = Day(60), reason = "Leave" })));
        Assert.Equal(HttpStatusCode.NotFound, (await PauseAsync(t["co"].Client, aup, campaign, Guid.NewGuid(), new { until = Day(60), reason = "Leave" })).StatusCode);

        // Bea is away for sixty days: her deadline moves past her return by the time she still had.
        var before = await DueAsync(t, aup, "bea");
        await OkAsync(PauseAsync(t["co"].Client, aup, campaign, t["bea"].Id, new { until = Day(60), reason = "Maternity leave" }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyAttestationPaused, aup.ToString());
        var after = await DueAsync(t, aup, "bea");
        Assert.True(after > before.AddDays(58), $"{before} then {after}");
        Assert.True(after > DateTimeOffset.UtcNow.AddDays(60));
        Assert.Contains("conflict.policies.already_paused", await ErrorKeysAsync(await PauseAsync(t["co"].Client, aup, campaign, t["bea"].Id, new { until = Day(10), reason = "Leave" })));

        // The compliance list shows the pause, never the reason.
        var people = await OkAsync(t["co"].Client.GetAsync($"{Policies}/{aup}/compliance/people?status=open"));
        var bea = people.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("person").GetProperty("id").GetGuid() == t["bea"].Id);
        Assert.NotEqual(JsonValueKind.Null, bea.GetProperty("pausedUntil").ValueKind);
        Assert.DoesNotContain("Maternity", people.ToString());

        // Reminders and overdue pass Bea by while she is away; Ali gets both.
        Assert.True(await RunDueAsync(t, deadline.AddDays(-7).AddHours(1)) > 0);
        Assert.Equal(1, await CountAsync(t, campaign, "ali", "cardinality(reminders_sent) > 0"));
        Assert.Equal(0, await CountAsync(t, campaign, "bea", "cardinality(reminders_sent) > 0"));
        Assert.True(await RunDueAsync(t, deadline.AddHours(1)) > 0);
        Assert.Equal(1, await CountAsync(t, campaign, "ali", "status = 'overdue'"));
        Assert.Equal(1, await CountAsync(t, campaign, "bea", "status = 'open'"));

        // A manual reminder skips her too; once she is back and past her new deadline she is overdue like anyone.
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{aup}/campaigns/{campaign}/remind", null));
        Assert.Equal(0, await CountAsync(t, campaign, "bea", "cardinality(reminders_sent) > 0"));
        Assert.True(await RunDueAsync(t, after.AddHours(1)) > 0);
        Assert.Equal(1, await CountAsync(t, campaign, "bea", "status = 'overdue'"));

        // An overdue person paused is open again.
        await OkAsync(PauseAsync(t["co"].Client, aup, campaign, t["ali"].Id, new { until = Day(20), reason = "Sick leave" }));
        Assert.Equal(1, await CountAsync(t, campaign, "ali", "status = 'open' and paused_until is not null and pause_reason = 'Sick leave'"));
    }

    [Fact]
    public async Task Other_tenants_and_unknown_campaigns_reach_nothing()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var campaign = (await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new { }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await PauseAsync(other["co"].Client, aup, campaign, t["bea"].Id, new { until = Day(30), reason = "Leave" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PauseAsync(other["co"].Client, aup, campaign, other["bea"].Id, new { until = Day(30), reason = "Leave" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PauseAsync(t["co"].Client, aup, Guid.NewGuid(), t["bea"].Id, new { until = Day(30), reason = "Leave" })).StatusCode);
        Assert.Equal(0, await CountAsync(t, campaign, "bea", "paused_until is not null"));
    }
}
