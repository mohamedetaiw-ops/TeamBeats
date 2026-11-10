using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBeats.Api.DevHost;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// The staging home seed (docs/testing/fake-systems.md): from the checked-in file it publishes fictional news, form
/// services and a global menu, then the v2 home with the briefing on top and the campaign band at the bottom linking to the seeded story, and mail and calendar
/// placed lower in the grid; a second run adds nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class StagingHomeSeedTests(TenantWorld world)
{
    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task The_seed_publishes_news_services_menu_and_the_v2_home_once()
    {
        var t = await world.CreateTenantAsync("hs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StagingHomeSeeder.FileKey] = "seeds/staging-home.json",
            [TestTenantSeeder.Section + ":Admins:0"] = t["admin"].Email,
        }).Build();
        var seeder = new StagingHomeSeeder(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<StagingHomeSeeder>.Instance);
        await seeder.SeedAsync(t.Key, CancellationToken.None);
        await seeder.SeedAsync(t.Key, CancellationToken.None);

        var emp = t["emp"].Client;
        var feed = (await OkAsync(emp.GetAsync("/api/v1/content/feed?limit=20"))).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, feed.Length);
        var safety = feed.Single(i => i.GetProperty("title").GetString() == "Safety Week starts on 16 November");
        Assert.True(safety.GetProperty("pinned").GetBoolean());

        var services = await OkAsync(emp.GetAsync("/api/v1/services"));
        var names = services.GetProperty("services").EnumerateArray().Select(s => s.GetProperty("names").GetProperty("en").GetString()).ToArray();
        foreach (var name in new[] { "Salary letter", "IT support", "System access", "Parking permit", "Business cards" })
        {
            Assert.Single(names, n => n == name);
        }

        var shell = await OkAsync(emp.GetAsync("/api/v1/experience/shell"));
        Assert.Equal(["Home", "Services", "Tasks", "People", "Files", "Knowledge", "Policies"],
            shell.GetProperty("globalMenu").EnumerateArray().Select(l => l.GetProperty("label").GetString()!).ToArray());
        Assert.Equal("home", shell.GetProperty("home").GetString());

        var page = await OkAsync(emp.GetAsync("/api/v1/experience/render?slug=home"));
        var regions = page.GetProperty("regions").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        var hero = regions["hero"].GetProperty("placements").EnumerateArray().ToArray();
        Assert.Equal(["briefing", "tasks"], hero.Select(p => p.GetProperty("id").GetString()!).ToArray());
        var grid = regions["grid"].GetProperty("placements").EnumerateArray().ToArray();
        Assert.Equal(["news", "services", "home-outlook-my-calendar", "home-outlook-my-mail", "campaign-band"], grid.Select(p => p.GetProperty("id").GetString()!).ToArray());
        // The Safety Week band closes the home (spec 008 home composition) and links to the seeded story.
        Assert.Equal($"/content/read/{safety.GetProperty("id").GetGuid()}", grid[^1].GetProperty("props").GetProperty("link").GetString());
    }
}
