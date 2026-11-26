using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Experience.Personalization;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 R3 US6 and FR-033 (T027): behavioral personalization is off unless the package has it and the tenant turns it
/// on; under opt-in only people who agreed are counted, under opt-out everyone who has not declined; declining deletes
/// the person's signals and keeps their other settings; quick navigation puts the person's most used matches first and
/// marks them; signals go after 90 days and are never read for anyone else or another tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BehaviorPersonalizationTests(TenantWorld world)
{
    private const string Policy = "/api/v1/experience/behavior-policy";
    private const string Choice = "/api/v1/me/personalization/behavior";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed record World(TestTenant T, string Prefix);

    /// <summary>Three top level entries the employee may open, all matching one prefix, in this order.</summary>
    private async Task<World> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("bp-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "employee", "employee");
        await world.SeedUserAsync(t, "other", "employee");
        var prefix = "bp" + Guid.NewGuid().ToString("N")[..6];
        foreach (var (slug, url) in new[] { ($"{prefix}-a", "/directory"), ($"{prefix}-b", "/learning"), ($"{prefix}-c", "/services") })
        {
            var response = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/ia/{Guid.NewGuid()}", new { labels = new { en = slug, ar = slug }, slug, url });
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        return new World(t, prefix);
    }

    private static async Task VisitAsync(HttpClient client, string path, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/me/recents", new { path, label = path })).StatusCode);
        }
    }

    private static async Task<JsonElement[]> QuickAsync(HttpClient client, string prefix) =>
        (await OkAsync(client.GetAsync($"/api/v1/experience/quick-nav?q={prefix}"))).EnumerateArray().ToArray();

    private static async Task<JsonElement[]> SignalsAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/preferences/export"))).GetProperty("signals").EnumerateArray().ToArray();

    [Fact]
    public async Task Nothing_is_counted_or_ranked_until_the_tenant_turns_it_on_and_the_person_agrees()
    {
        var w = await FreshAsync();
        var employee = w.T["employee"].Client;
        var policy = await OkAsync(w.T["admin"].Client.GetAsync(Policy));
        Assert.True(policy.GetProperty("available").GetBoolean());
        Assert.False(policy.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync(Policy)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync(Policy, new { enabled = true })).StatusCode);

        // Off: visits are recents only.
        await VisitAsync(employee, "/services", 3);
        Assert.Empty(await SignalsAsync(employee));
        Assert.All(await QuickAsync(employee, w.Prefix), q => Assert.False(q.GetProperty("ranked").GetBoolean()));
        Assert.False((await OkAsync(employee.GetAsync(Choice))).GetProperty("available").GetBoolean());

        // On with opt-in: still nothing until the person agrees.
        await OkAsync(w.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = true, consentMode = "opt_in" }));
        await world.Database.AssertAuditedAsync(w.T.Id, AuditActions.ExperienceBehavioralPersonalizationChanged, "tenant");
        var choice = await OkAsync(employee.GetAsync(Choice));
        Assert.True(choice.GetProperty("available").GetBoolean());
        Assert.False(choice.GetProperty("active").GetBoolean());
        await VisitAsync(employee, "/services", 2);
        Assert.Empty(await SignalsAsync(employee));

        Assert.True((await OkAsync(employee.PutAsJsonAsync(Choice, new { consented = true }))).GetProperty("active").GetBoolean());
        await VisitAsync(employee, "/services", 3);
        await VisitAsync(employee, "/learning", 1);
        var quick = await QuickAsync(employee, w.Prefix);
        Assert.Equal(["/services", "/learning", "/directory"], quick.Select(q => q.GetProperty("href").GetString()));
        Assert.Equal([true, true, false], quick.Select(q => q.GetProperty("ranked").GetBoolean()));
        Assert.Equal(3, (await SignalsAsync(employee)).Single(s => s.GetProperty("key").GetString() == "/services").GetProperty("count").GetInt32());

        // Another person's use never ranks for them.
        var other = await QuickAsync(w.T["other"].Client, w.Prefix);
        Assert.Equal("/directory", other[0].GetProperty("href").GetString());
        Assert.Empty(await SignalsAsync(w.T["other"].Client));

        // Declining deletes the signals and keeps recents.
        Assert.False((await OkAsync(employee.PutAsJsonAsync(Choice, new { consented = false }))).GetProperty("active").GetBoolean());
        Assert.Empty(await SignalsAsync(employee));
        Assert.Contains((await OkAsync(employee.GetAsync("/api/v1/me/recents"))).EnumerateArray(), r => r.GetProperty("path").GetString() == "/services");
        Assert.Equal("/directory", (await QuickAsync(employee, w.Prefix))[0].GetProperty("href").GetString());
    }

    [Fact]
    public async Task Opt_out_counts_until_the_person_declines_and_signals_go_after_90_days()
    {
        var w = await FreshAsync();
        var employee = w.T["employee"].Client;
        await OkAsync(w.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = true, consentMode = "opt_out" }));
        var choice = await OkAsync(employee.GetAsync(Choice));
        Assert.True(choice.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, choice.GetProperty("consented").ValueKind);
        await VisitAsync(employee, "/learning", 2);
        Assert.Single(await SignalsAsync(employee));

        await world.Factory.InScopeAsync(w.T.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var behavior = sp.GetRequiredService<BehaviorPersonalization>();
            await behavior.PurgeAsync(DateTimeOffset.UtcNow.AddDays(89), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Single(await SignalsAsync(employee));
        await world.Factory.InScopeAsync(w.T.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<BehaviorPersonalization>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(91), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Empty(await SignalsAsync(employee));

        // Turning the policy off stops counting at once.
        await OkAsync(w.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = false }));
        await VisitAsync(employee, "/learning", 1);
        Assert.Empty(await SignalsAsync(employee));
        Assert.Equal("validation.experience.behavior_consent_mode",
            (await (await w.T["admin"].Client.PutAsJsonAsync(Policy, new { consentMode = "always" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task Without_the_package_feature_it_cannot_be_turned_on_and_other_tenants_never_see_signals()
    {
        var plain = await FreshAsync("workplace_plus");
        var refused = await plain.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.experience.behavior_unavailable", await refused.Content.ReadAsStringAsync());
        Assert.False((await OkAsync(plain.T["admin"].Client.GetAsync(Policy))).GetProperty("available").GetBoolean());

        var w = await FreshAsync();
        await OkAsync(w.T["admin"].Client.PutAsJsonAsync(Policy, new { enabled = true, consentMode = "opt_out" }));
        await VisitAsync(w.T["employee"].Client, "/services", 2);
        Assert.Single(await SignalsAsync(w.T["employee"].Client));
        // The other tenant has its own policy, off, and none of these signals.
        Assert.False((await OkAsync(plain.T["employee"].Client.GetAsync(Choice))).GetProperty("active").GetBoolean());
        Assert.Empty(await SignalsAsync(plain.T["employee"].Client));
    }
}
