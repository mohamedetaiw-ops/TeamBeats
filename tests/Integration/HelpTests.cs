using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 T024 (FR-020): help hints and short tours are versioned, schema-validated tenant configuration published by
/// people with help.edit; each person sees those of the page they are on in their language, dismisses them and reopens
/// them, and another person or tenant is unaffected.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class HelpTests(TenantWorld world)
{
    private static object Definition => new
    {
        items = new object[]
        {
            new { key = "leave-intro", kind = "hint", path = "/leave", title = new { en = "Requesting leave", ar = "طلب إجازة" }, body = new { en = "Pick the days, then send.", ar = "اختر الأيام ثم أرسل." } },
            new
            {
                key = "home-tour", kind = "tour", path = "/", title = new { en = "Welcome", ar = "مرحبًا" },
                steps = new object[]
                {
                    new { title = new { en = "Search" }, body = new { en = "Find pages and people." } },
                    new { title = new { en = "Settings" }, body = new { en = "Change how the app looks." } },
                },
            },
        },
    };

    private static async Task<JsonElement> HelpAsync(HttpClient client, string path) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/me/help?path={Uri.EscapeDataString(path)}");

    [Fact]
    public async Task Help_is_published_in_versions_shown_per_page_and_dismissed_per_person()
    {
        var t = await world.CreateTenantAsync("help-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "employee", "employee");
        await world.SeedUserAsync(t, "coworker", "employee");

        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PutAsJsonAsync("/api/v1/experience/help", Definition)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync("/api/v1/experience/help")).StatusCode);
        var published = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/help", Definition);
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        Assert.Equal(1, (await published.ReadAsync<JsonElement>()).GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HelpPublished);

        // On a leave page the hint and the home tour apply; elsewhere only the tour.
        var leave = await HelpAsync(t["employee"].Client, "/leave/requests");
        Assert.Equal(["leave-intro", "home-tour"], leave.EnumerateArray().Select(h => h.GetProperty("key").GetString()));
        Assert.Equal("Pick the days, then send.", leave[0].GetProperty("body").GetString());
        Assert.Equal(2, leave[1].GetProperty("steps").GetArrayLength());
        Assert.Equal(["home-tour"], (await HelpAsync(t["employee"].Client, "/leaves")).EnumerateArray().Select(h => h.GetProperty("key").GetString()));

        // Dismissed for the employee only, and reopened.
        Assert.Equal(HttpStatusCode.NoContent, (await t["employee"].Client.PostAsync("/api/v1/me/help/leave-intro/dismiss", null)).StatusCode);
        Assert.True((await HelpAsync(t["employee"].Client, "/leave"))[0].GetProperty("dismissed").GetBoolean());
        Assert.False((await HelpAsync(t["coworker"].Client, "/leave"))[0].GetProperty("dismissed").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await t["employee"].Client.DeleteAsync("/api/v1/me/help/leave-intro/dismiss")).StatusCode);
        Assert.False((await HelpAsync(t["employee"].Client, "/leave"))[0].GetProperty("dismissed").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.PostAsync("/api/v1/me/help/unknown-key/dismiss", null)).StatusCode);

        // Another tenant has none of it.
        var outsider = await world.CreateTenantAsync("help-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Empty((await HelpAsync(outsider["admin"].Client, "/leave")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider["admin"].Client.PostAsync("/api/v1/me/help/leave-intro/dismiss", null)).StatusCode);

        // A new version replaces it; the first stays in the history.
        Assert.True((await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/help", new { items = Array.Empty<object>() })).IsSuccessStatusCode);
        Assert.Empty((await HelpAsync(t["employee"].Client, "/leave")).EnumerateArray());
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from experience.help_config"));
    }

    [Fact]
    public async Task Help_configuration_is_checked_against_its_schema()
    {
        var t = await world.CreateTenantAsync("help-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var bad = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/help", new
        {
            items = new object[]
            {
                new { key = "Bad Key", kind = "hint", path = "/x", title = new { en = "T" }, body = new { en = "B" } },
                new { key = "a", kind = "banner", path = "/x", title = new { en = "T" } },
                new { key = "a", kind = "tour", path = "x?y", title = new { de = "T" }, steps = Array.Empty<object>() },
                new { key = "b", kind = "hint", path = "/x", title = new { en = new string('x', 121) }, body = new { en = "B" }, steps = new object[] { new { title = new { en = "s" }, body = new { en = "s" } } } },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.help.key", "validation.help.kind", "validation.help.key_duplicate", "validation.help.path", "validation.help.locale",
            "validation.help.steps", "validation.help.text_length", "validation.help.hint_steps" })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from experience.help_config"));
    }
}
