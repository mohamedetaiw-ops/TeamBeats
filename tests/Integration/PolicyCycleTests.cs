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
/// Spec 025 T022 (FR-008, fixed cycles): someone who runs a policy's campaigns sets a cycle in months; when the last
/// campaign is that old and none is open, maintenance launches a new one for the version in force with the last one's
/// settings and as long to attest, launched by the person who set the cycle. Nothing launches before, while a campaign
/// is open, for a policy that never had one, or without <c>policy.campaigns</c>; employees and other tenants cannot set
/// a cycle.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyCycleTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pcy-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
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

    private static Task<HttpResponseMessage> CycleAsync(HttpClient client, Guid policy, int? months) =>
        client.PutAsJsonAsync($"{Policies}/{policy}/cycle", new { months });

    private Task<int> RunCyclesAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var launched = await sp.GetRequiredService<PolicyCampaigns>().RunCyclesAsync(now, 50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return launched;
        });

    private static async Task<JsonElement[]> CampaignsAsync(TestTenant t, Guid policy) =>
        (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{policy}/campaigns"))).EnumerateArray().ToArray();

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<PolicyCampaigns>().RunDueAsync(now, 1000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    [Fact]
    public async Task A_cycle_launches_the_next_campaign_with_the_last_ones_settings_when_it_is_due()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");

        // Only someone who runs the campaigns sets a cycle, from 1 to 60 months.
        Assert.Equal(HttpStatusCode.Forbidden, (await CycleAsync(t["ali"].Client, aup, 12)).StatusCode);
        Assert.Contains("validation.policies.cycle_months", await ErrorKeysAsync(await CycleAsync(t["co"].Client, aup, 0)));
        Assert.Contains("validation.policies.cycle_months", await ErrorKeysAsync(await CycleAsync(t["co"].Client, aup, 61)));
        var set = await OkAsync(CycleAsync(t["co"].Client, aup, 12));
        Assert.Equal(12, set.GetProperty("months").GetInt32());
        Assert.Equal(JsonValueKind.Null, set.GetProperty("nextLaunchAt").ValueKind);
        Assert.Equal(12, (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{aup}"))).GetProperty("attestationCycleMonths").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await OkAsync(t["ali"].Client.GetAsync($"{Policies}/{aup}"))).GetProperty("attestationCycleMonths").ValueKind);

        // Without a first campaign nothing launches; the first one is compliance's.
        Assert.Equal(0, await RunCyclesAsync(t, DateTimeOffset.UtcNow.AddYears(2)));
        var first = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new
        {
            deadline = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20), reminderDays = new[] { 5 }, lateJoinerDays = 10, excludedMembershipTypes = new[] { "contractor" },
        }), HttpStatusCode.Created);
        var firstId = first.GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);

        // While it is open, or before twelve months pass, nothing launches.
        Assert.Equal(0, await RunCyclesAsync(t, DateTimeOffset.UtcNow.AddMonths(13)));
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{aup}/campaigns/{firstId}/close", null));
        Assert.Equal(0, await RunCyclesAsync(t, DateTimeOffset.UtcNow.AddMonths(11)));
        Assert.Equal(1, await RunCyclesAsync(t, DateTimeOffset.UtcNow.AddMonths(12).AddDays(1)));
        Assert.Equal(0, await RunCyclesAsync(t, DateTimeOffset.UtcNow.AddMonths(12).AddDays(2)));

        var campaigns = await CampaignsAsync(t, aup);
        Assert.Equal(2, campaigns.Length);
        var next = campaigns.Single(c => c.GetProperty("id").GetGuid() != firstId);
        Assert.Equal("open", next.GetProperty("status").GetString());
        Assert.Equal(1, next.GetProperty("version").GetInt32());
        Assert.Equal([5], next.GetProperty("reminderDays").EnumerateArray().Select(d => d.GetInt32()).ToArray());
        Assert.Equal(10, next.GetProperty("lateJoinerDays").GetInt32());
        Assert.Equal(t["co"].Id, next.GetProperty("launchedBy").GetProperty("id").GetGuid());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.campaign where cycle and launched_by = @co", ("co", t["co"].Id)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyCampaignLaunched, aup.ToString());

        // Clearing the cycle stops it.
        await OkAsync(t["co"].Client.PostAsync($"{Policies}/{aup}/campaigns/{next.GetProperty("id").GetGuid()}/close", null));
        Assert.Equal(JsonValueKind.Null, (await OkAsync(CycleAsync(t["co"].Client, aup, null))).GetProperty("months").ValueKind);
        Assert.Equal(0, await RunCyclesAsync(t, DateTimeOffset.UtcNow.AddYears(5)));
    }

    [Fact]
    public async Task Without_unlimited_campaigns_there_is_no_cycle_and_other_tenants_reach_nothing()
    {
        var plain = await FreshAsync("workplace");
        var small = await PublishedAsync(plain, "AUP");
        Assert.Equal(HttpStatusCode.Forbidden, (await CycleAsync(plain["co"].Client, small, 12)).StatusCode);
        Assert.Equal(0, await RunCyclesAsync(plain, DateTimeOffset.UtcNow.AddYears(2)));

        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await CycleAsync(other["co"].Client, aup, 12)).StatusCode);
    }
}
