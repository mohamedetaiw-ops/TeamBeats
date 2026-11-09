using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T007 (adoption basics): weekly active people, active people by unit with units under the minimum group
/// size merged into "other", personal layout use counted from <c>personalization.changed</c> events that Experience
/// emits and hidden for fewer people than the minimum, and pages per template version; for <c>analytics.view</c> only,
/// in the admin navigation, and never across tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AdoptionTests(TenantWorld world)
{
    private const string Path = "/api/v1/analytics/adoption";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private Task AggregateAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var read = await sp.GetRequiredService<AnalyticsAggregator>().RunAsync(5000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return read;
        });

    private static Task SendAsync(HttpClient client, object e) =>
        OkAsync(client.PostAsJsonAsync("/api/v1/analytics/events", new { events = new[] { e } }), HttpStatusCode.Accepted);

    [Fact]
    public async Task Adoption_basics_count_active_people_by_unit_layout_use_and_template_pages_within_the_minimum_group_size()
    {
        var t = await world.CreateTenantAsync("ad-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var units = new Dictionary<string, Guid>();
        foreach (var unit in new[] { "PlantA", "PlantB", "Cairo" })
        {
            units[unit] = await world.CreateUnitAsync(t, unit, holding, "department");
        }
        var people = new List<string>();
        foreach (var (prefix, unit, count) in new[] { ("a", "PlantA", 6), ("b", "PlantB", 4), ("c", "Cairo", 2) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
                people.Add(prefix + i);
            }
        }
        await DispatchAsync();
        foreach (var user in people)
        {
            await SendAsync(t[user].Client, new { name = AnalyticsEvents.PageViewed, fields = new { area = "home" } });
        }

        // Experience reports layout resets as events; five people this week, two in an earlier week up to seven days late.
        foreach (var user in new[] { "a1", "a2", "a3", "b1", "c1" })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await t[user].Client.DeleteAsync("/api/v1/me/layouts")).StatusCode);
        }
        await DispatchAsync();
        var earlier = DateTimeOffset.UtcNow.AddDays(-6.9);
        foreach (var user in new[] { "a5", "a6" })
        {
            await SendAsync(t[user].Client, new { name = AnalyticsEvents.PersonalizationChanged, occurredAt = earlier, fields = new { action = "customize" } });
        }
        await AggregateAsync(t);

        var view = await OkAsync(t["admin"].Client.GetAsync(Path));
        Assert.Equal(5, view.GetProperty("minGroupSize").GetInt32());
        var thisWeek = Adoption.WeekOf(DateOnly.FromDateTime(DateTime.UtcNow));
        var weeks = view.GetProperty("weeks").EnumerateArray().ToDictionary(w => DateOnly.Parse(w.GetProperty("week").GetString()!), w => w.GetProperty("activeUsers").GetInt32());
        Assert.Equal(12, weeks[thisWeek]);
        Assert.Equal(thisWeek, DateOnly.Parse(view.GetProperty("unitsWeek").GetString()!));

        // Plant A's six are shown; Plant B's four and Cairo's two are only seen together.
        var byUnit = view.GetProperty("units").EnumerateArray().ToDictionary(u => u.GetProperty("group").GetString()!, u => u);
        Assert.Equal(2, byUnit.Count);
        Assert.Equal(6, byUnit[units["PlantA"].ToString()].GetProperty("activeUsers").GetInt32());
        Assert.Equal("PlantA", byUnit[units["PlantA"].ToString()].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(6, byUnit[ItemAnalytics.Other].GetProperty("activeUsers").GetInt32());

        // Layout use: five people this week are shown; two people in an earlier week are not.
        var layout = view.GetProperty("personalization").EnumerateArray().ToDictionary(w => DateOnly.Parse(w.GetProperty("week").GetString()!), w => w);
        var current = layout[thisWeek];
        if (Adoption.WeekOf(DateOnly.FromDateTime(earlier.UtcDateTime)) == thisWeek)
        {
            Assert.Equal(7, current.GetProperty("people").GetInt32());
            Assert.Equal(2, current.GetProperty("customized").GetInt32());
        }
        else
        {
            Assert.Equal(5, current.GetProperty("people").GetInt32());
            Assert.Equal(0, current.GetProperty("customized").GetInt32());
            var past = layout[Adoption.WeekOf(DateOnly.FromDateTime(earlier.UtcDateTime))];
            Assert.Equal(JsonValueKind.Null, past.GetProperty("people").ValueKind);
            Assert.Equal(JsonValueKind.Null, past.GetProperty("customized").ValueKind);
        }
        Assert.Equal(5, current.GetProperty("reset").GetInt32());
        Assert.Equal(5, await world.Database.CountInTenantAsync(t.Id, "select count(*) from analytics.event where name = @n and channel = 'server'",
            ("n", AnalyticsEvents.PersonalizationChanged)));

        // Pages per template version come from Experience and add up to every page of the tenant.
        var pages = await world.Database.CountInTenantAsync(t.Id, "select count(*) from experience.page");
        Assert.Equal(pages, view.GetProperty("templates").EnumerateArray().Sum(x => x.GetProperty("pages").GetInt32()));

        // Only analytics.view: the section is in the admin navigation for the administrator, not for an employee.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a1"].Client.GetAsync(Path)).StatusCode);
        var nav = await OkAsync(t["admin"].Client.GetAsync("/api/v1/admin/navigation"));
        Assert.Contains(nav.EnumerateArray(), s => s.GetProperty("key").GetString() == "analytics" && s.GetProperty("path").GetString() == "/admin/analytics");

        // Another tenant sees only its own, empty, numbers.
        var other = await world.CreateTenantAsync("ad-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var theirs = await OkAsync(other["admin"].Client.GetAsync(Path));
        Assert.Equal(0, theirs.GetProperty("weeks").GetArrayLength());
        Assert.Equal(0, theirs.GetProperty("units").GetArrayLength());
        Assert.Equal(0, theirs.GetProperty("personalization").GetArrayLength());
    }

    [Fact]
    public async Task Support_sees_frontend_errors_by_release_with_component_and_correlation_id_and_no_message()
    {
        var t = await world.CreateTenantAsync("fe-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "ali", MembershipTypes.Employee);
        object Error(string release, string kind, string? component, string correlationId) => new
        {
            name = AnalyticsEvents.UiError,
            fields = new { kind, release, component, area = "pages", correlationId, message = "Cannot read name of Fatima's record" },
        };
        foreach (var e in new[]
        {
            Error("r-41", "render", "toolbox.weather@1", "c-1"), Error("r-41", "render", "toolbox.weather@1", "c-2"),
            Error("r-41", "boundary", null, "c-3"), Error("r-42", "chunk", null, "c-4"),
        })
        {
            await SendAsync(t["ali"].Client, e);
        }

        var all = await OkAsync(t["admin"].Client.GetAsync("/api/v1/analytics/errors"));
        Assert.Equal(new[] { "r-42", "r-41" }, all.GetProperty("releases").EnumerateArray().Select(r => r.GetProperty("release").GetString()!).ToArray());
        Assert.Equal(3, all.GetProperty("groups").GetArrayLength());
        Assert.DoesNotContain("Fatima", all.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from analytics.event where fields ? 'message' or user_key is not null"));

        var filtered = await OkAsync(t["admin"].Client.GetAsync("/api/v1/analytics/errors?release=r-41"));
        var top = filtered.GetProperty("groups").EnumerateArray().First();
        Assert.Equal("r-41", filtered.GetProperty("release").GetString());
        Assert.Equal(2, filtered.GetProperty("groups").GetArrayLength());
        Assert.Equal("toolbox.weather@1", top.GetProperty("component").GetString());
        Assert.Equal(2, top.GetProperty("count").GetInt32());
        Assert.Equal(new[] { "c-1", "c-2" }, top.GetProperty("correlationIds").EnumerateArray().Select(c => c.GetString()).Order(StringComparer.Ordinal).ToArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync("/api/v1/analytics/errors")).StatusCode);
        var other = await world.CreateTenantAsync("fe-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(0, (await OkAsync(other["admin"].Client.GetAsync("/api/v1/analytics/errors"))).GetProperty("groups").GetArrayLength());
    }
}
