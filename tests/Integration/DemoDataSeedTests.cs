using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBeats.Api.DevHost;
using TeamBeats.Api.DevHost.Demo;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// The demo data package (docs/testing/demo-data.md): from the checked-in files it fills every in-app area of a tenant
/// through the modules' use cases, as the people the files name; a second run adds nothing, a person who does not exist
/// is skipped without stopping the rest, and another tenant sees none of it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DemoDataSeedTests(TenantWorld world)
{
    /// <summary>Personas the test creates; the rest of the pack's people (such as the chief executive) are left out on purpose.</summary>
    private static readonly string[] Personas =
        ["layla", "omar", "sarah", "noura", "ahmed", "fatima", "daniel", "maryam", "youssef", "khalid", "priya", "huda", "tariq", "reem", "james", "salma", "lina", "marco", "abdulrahman", "emily"];

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task The_pack_fills_every_area_once_as_its_people_and_stays_in_its_tenant()
    {
        var t = await world.CreateTenantAsync("demo-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov", "en", ["ar", "en"]);
        var settings = new Dictionary<string, string?>
        {
            [DemoDataSeeder.FolderKey] = "seeds/demo",
            [TestTenantSeeder.Section + ":Admins:0"] = t["admin"].Email,
        };
        foreach (var persona in Personas)
        {
            settings[$"{DemoDataSeeder.PeopleKey}:{persona}"] = (await world.SeedUserAsync(t, persona, MembershipTypes.Employee)).Email;
        }
        var other = await world.CreateTenantAsync("demo-o-" + Guid.NewGuid().ToString("N")[..6], "enterprise_gov");
        var seeder = new DemoDataSeeder(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), NullLogger<DemoDataSeeder>.Instance);
        var pack = DemoDataSeeder.Load("seeds/demo");

        var first = await seeder.SeedAsync(t.Key, CancellationToken.None);
        Assert.NotNull(first);
        var added = first.Added;
        // This tenant has no directory, so the organization cannot be mapped from it; everything else must go in.
        var problems = string.Join("\n", first.Skipped.Where(s => !s.EndsWith(" not found", StringComparison.Ordinal) && s != "org units: no directory synced"));
        Assert.True(problems.Length == 0, problems);
        Assert.Equal(pack.News.Count(n => n.Type == "news"), added.GetValueOrDefault("news"));
        Assert.Equal(pack.News.Count(n => n.Type == "announcement"), added.GetValueOrDefault("announcement"));
        Assert.Equal(pack.Knowledge.Count, added.GetValueOrDefault("knowledge"));
        Assert.Equal(pack.Services.Count, added.GetValueOrDefault("services"));
        Assert.Equal(pack.Requests.Count, added.GetValueOrDefault("requests"));
        Assert.Equal(pack.Requests.Sum(r => r.Decisions?.Count ?? 0), added.GetValueOrDefault("decisions"));
        Assert.Equal(pack.Libraries.Sum(l => l.Documents.Count), added.GetValueOrDefault("documents"));
        Assert.Equal(pack.Policies.Count, added.GetValueOrDefault("policies"));
        Assert.Equal(1, added.GetValueOrDefault("campaigns"));
        Assert.Equal(pack.Policies.Sum(p => p.Campaign?.AttestedBy?.Count(Personas.Contains) ?? 0), added.GetValueOrDefault("attestations"));
        Assert.True(added.GetValueOrDefault("holidays") > 40);
        Assert.Equal(pack.Search!.Synonyms!.Count, added.GetValueOrDefault("synonym sets"));
        Assert.Equal(pack.Spaces.Count, added.GetValueOrDefault("spaces"));
        Assert.Equal(pack.Spaces.Sum(s => s.Tasks?.Count ?? 0), added.GetValueOrDefault("space tasks"));
        Assert.Equal(pack.Spaces.Sum(s => s.Milestones?.Count ?? 0), added.GetValueOrDefault("space milestones"));
        // The chief executive is not a user here: their story is written by the administrator, their reactions are skipped.
        Assert.Contains("hassan not found", first.Skipped);
        Assert.DoesNotContain(first.Skipped, s => s.Contains("failed", StringComparison.Ordinal));

        // What people see: the news feed, the catalogue, approvals waiting in the administrator's and Ahmed's inboxes, their own requests.
        var feed = (await OkAsync(t["layla"].Client.GetAsync("/api/v1/content/feed?limit=50"))).GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(feed, i => i.GetProperty("title").GetString() == "Q3 results: what they mean for our teams");
        var catalogue = await OkAsync(t["fatima"].Client.GetAsync("/api/v1/services"));
        Assert.Equal(pack.ServiceCategories.Count, catalogue.GetProperty("categories").GetArrayLength());
        Assert.Contains(catalogue.GetProperty("services").EnumerateArray(), s => s.GetProperty("names").GetProperty("ar").GetString() == "طلب إجازة");
        var adminTasks = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().ToArray();
        Assert.True(adminTasks.Length >= 8, $"{adminTasks.Length} tasks for the administrator");
        Assert.Contains((await OkAsync(t["ahmed"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray(), i => i.GetProperty("number").ValueKind == JsonValueKind.String);
        Assert.Equal(pack.Requests.Count(r => r.Requester == "priya"), (await OkAsync(t["priya"].Client.GetAsync("/api/v1/requests"))).GetArrayLength());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from services.request where status = 'rejected'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from services.request where status = 'returned'"));
        Assert.Equal(pack.Policies.Count, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.policy where current_version = 1"));
        Assert.True(await world.Database.CountInTenantAsync(t.Id, "select count(*) from content.reaction") > 30);
        Assert.Equal(Personas.Length, await world.Database.CountInTenantAsync(t.Id, "select count(*) from people.profile where names ? 'ar'"));

        // Restarting adds nothing.
        var second = await seeder.SeedAsync(t.Key, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Empty(second.Added);
        Assert.DoesNotContain(second.Skipped, s => !s.EndsWith(" not found", StringComparison.Ordinal) && s != "org units: no directory synced");

        // Nothing reaches another tenant.
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from content.content_item"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from services.request"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from policies.policy"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from spaces.space"));
    }

    [Fact]
    public async Task Nothing_is_seeded_before_the_administrator_signs_in_or_without_a_folder()
    {
        var t = await world.CreateTenantAsync("demo-n-" + Guid.NewGuid().ToString("N")[..6], "enterprise_gov");
        var scopes = world.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var noFolder = new DemoDataSeeder(scopes, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TestTenantSeeder.Section + ":Admins:0"] = t["admin"].Email,
        }).Build(), NullLogger<DemoDataSeeder>.Instance);
        Assert.Null(await noFolder.SeedAsync(t.Key, CancellationToken.None));

        var unknownAdmin = new DemoDataSeeder(scopes, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DemoDataSeeder.FolderKey] = "seeds/demo",
            [TestTenantSeeder.Section + ":Admins:0"] = "nobody@" + t.Key + ".example",
        }).Build(), NullLogger<DemoDataSeeder>.Instance);
        Assert.Null(await unknownAdmin.SeedAsync(t.Key, CancellationToken.None));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from content.content_item"));
    }
}
