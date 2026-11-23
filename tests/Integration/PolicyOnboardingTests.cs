using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T018 (FR-017): compliance officers mark a policy in force as required at onboarding from its page; the
/// change is audited; the onboarding journey's attestation step then asks joiners for it (the journey side is in
/// <c>JourneyStepTypeTests</c>). Employees cannot change it, a retired policy cannot change, other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyOnboardingTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pob-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali" })
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

    private Task<int> RequireAsync(TestTenant t, string user, string key) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var owed = await sp.GetRequiredService<IPolicyObligations>().RequireOnboardingAsync(t[user].Id, key, DateTimeOffset.UtcNow.AddDays(5), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return owed;
        });

    [Fact]
    public async Task Officers_mark_a_policy_required_at_onboarding_and_joiners_are_then_asked_for_it()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var url = $"{Policies}/{aup}/onboarding";
        Assert.False((await OkAsync(t["co"].Client.GetAsync($"{Policies}/{aup}"))).GetProperty("onboardingRequired").GetBoolean());
        Assert.Equal(0, await RequireAsync(t, "ali", "journey:one"));

        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PutAsJsonAsync(url, new { required = true })).StatusCode);
        Assert.Contains("validation.policies.onboarding_required", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(url, new { })));
        Assert.True((await OkAsync(t["co"].Client.PutAsJsonAsync(url, new { required = true }))).GetProperty("onboardingRequired").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyChanged, aup.ToString());
        Assert.Equal(1, await RequireAsync(t, "ali", "journey:two"));

        // Turning it off leaves open steps as they are and asks nobody new.
        Assert.False((await OkAsync(t["co"].Client.PutAsJsonAsync(url, new { required = false }))).GetProperty("onboardingRequired").GetBoolean());
        Assert.Equal(0, await RequireAsync(t, "legal", "journey:three"));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.obligation where status = 'open'"));

        // A retired policy keeps its setting; another tenant does not find the policy.
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/retire", new { }));
        Assert.Contains("conflict.policies.retired", await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync(url, new { required = true })));
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PutAsJsonAsync(url, new { required = true })).StatusCode);
    }
}
