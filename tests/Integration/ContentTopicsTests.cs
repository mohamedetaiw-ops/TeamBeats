using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R2 US9 scenarios 2 and 3 and FR-017 (T027): a mention in a published item or a comment tells the person only
/// when they may read the item, and nobody can mention someone they cannot see; hashtags make topic pages that list
/// only permitted items; people follow sites and topics, and the following feed never hides a critical item.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentTopicsTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private sealed record Org(TestTenant T, Guid Dubai);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ctp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "pia", "mia", "rex", "gus" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "gus", "Dubai");
        await world.AddMembershipAsync(t, "mia", "Dubai");
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return new Org(t, dubai);
    }

    private static object Run(string text, Guid? mention = null) => mention is { } m ? new { text, mention = m } : new { text };

    private static object Body(params object[] runs) => new { blocks = new object[] { new { type = "paragraph", children = runs } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string?> ErrorKeyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static IEnumerable<Guid> FeedIds(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(Id);

    private static async Task<Guid> PublishAsync(HttpClient client, string title, object body, Guid? siteId = null, string classification = "internal", object? critical = null,
        string type = "news")
    {
        var item = Id(await OkAsync(client.PostAsJsonAsync(Items, new
        {
            siteId, type, classification, critical,
            variants = new Dictionary<string, object> { ["en"] = new { title, body } },
        }), HttpStatusCode.Created));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{item}/publish", new { material = true }));
        return item;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    [Fact]
    public async Task A_mention_tells_only_people_who_may_read_and_nobody_mentions_someone_hidden()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var mia = o.T["mia"];
        var rex = o.T["rex"];

        var open = await PublishAsync(pia, "Canteen menu", Body(Run("Thanks to "), Run("Mia", mia.Id), Run(" for the new menu.")));
        // Rex holds no grant at the tenant, so a confidential item is not his to read, and he is not told about it.
        var closed = await PublishAsync(pia, "Board notes", Body(Run("Ask "), Run("Rex", rex.Id), Run(" about it.")), classification: "confidential");
        await DispatchAsync();
        Assert.Contains(await NotificationLinksAsync(mia.Client), l => l.Contains(open.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(await NotificationLinksAsync(rex.Client), l => l.Contains(closed.ToString(), StringComparison.Ordinal));
        // The author is never told of their own item.
        Assert.DoesNotContain(await NotificationLinksAsync(pia), l => l.Contains(open.ToString(), StringComparison.Ordinal));

        // A person of another tenant cannot be mentioned: they are not in this directory.
        var other = await world.CreateTenantAsync("ctp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal("validation.content.mention", await ErrorKeyAsync(await pia.PostAsJsonAsync(Items, new
        {
            type = "news", variants = new Dictionary<string, object> { ["en"] = new { title = "Hi", body = Body(Run("Hi "), Run("Stranger", other["admin"].Id)) } },
        })));
        Assert.Equal("validation.content.mention", await ErrorKeyAsync(await mia.Client.PostAsJsonAsync($"{Items}/{open}/comments",
            new { body = "Look", mentions = new[] { other["admin"].Id } })));

        // A comment mention reaches Rex on an item he may read, and the comment names him.
        var comment = await OkAsync(mia.Client.PostAsJsonAsync($"{Items}/{open}/comments", new { body = "Rex, have a look", mentions = new[] { rex.Id } }), HttpStatusCode.Created);
        Assert.Equal(rex.Id, comment.GetProperty("mentions")[0].GetProperty("id").GetGuid());
        Assert.Contains(await NotificationLinksAsync(rex.Client), l => l.EndsWith($"#comment-{Id(comment)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Topics_list_permitted_items_and_the_following_feed_keeps_critical_items()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var admin = o.T["admin"].Client;
        var gus = o.T["gus"].Client;
        var dubaiSite = Id(await OkAsync(admin.PostAsJsonAsync("/api/v1/content/sites",
            new { names = new Dictionary<string, string> { ["en"] = "Dubai", ["ar"] = "دبي" }, orgUnitId = o.Dubai }), HttpStatusCode.Created));

        var wellness = await PublishAsync(pia, "Wellness week", Body(Run("Join the #Wellness week, #wellness for all.")));
        var run = await PublishAsync(admin, "Dubai run", Body(Run("Dubai #wellness run, see #Run2026")), dubaiSite);
        var secret = await PublishAsync(pia, "Board wellness", Body(Run("Board #wellness budget")), classification: "confidential");

        var read = await OkAsync(gus.GetAsync($"{Items}/{wellness}/view"));
        Assert.Equal(["wellness"], read.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));

        // The topic lists what Gus may read and is targeted at him; the confidential item stays out.
        var topic = FeedIds(await OkAsync(gus.GetAsync("/api/v1/content/topics/WELLNESS"))).ToList();
        Assert.Contains(wellness, topic);
        Assert.Contains(run, topic);
        Assert.DoesNotContain(secret, topic);
        Assert.Contains(secret, FeedIds(await OkAsync(pia.GetAsync("/api/v1/content/topics/wellness"))));
        Assert.Equal(HttpStatusCode.BadRequest, (await gus.GetAsync("/api/v1/content/topics/a")).StatusCode);

        // Following a topic, then a site.
        var banner = await PublishAsync(admin, "Road closed", Body(Run("Use the north gate.")), critical: new { presentation = "banner" }, type: "announcement");
        await OkAsync(gus.PostAsJsonAsync("/api/v1/content/follows", new { kind = "topic", key = "#Run2026" }));
        var following = FeedIds(await OkAsync(gus.GetAsync("/api/v1/content/feed?following=true"))).ToList();
        Assert.Contains(run, following);
        Assert.DoesNotContain(wellness, following);
        Assert.Contains(banner, following); // A critical item is never hidden by a preference.

        await OkAsync(gus.PostAsJsonAsync("/api/v1/content/follows", new { kind = "site", key = dubaiSite.ToString() }));
        Assert.Equal(HttpStatusCode.NoContent, (await gus.DeleteAsync("/api/v1/content/follows/topic/run2026")).StatusCode);
        var follows = await OkAsync(gus.GetAsync("/api/v1/content/follows"));
        Assert.Equal([("site", dubaiSite.ToString())], follows.EnumerateArray().Select(f => (f.GetProperty("kind").GetString()!, f.GetProperty("key").GetString()!)));
        Assert.Contains(run, FeedIds(await OkAsync(gus.GetAsync("/api/v1/content/feed?following=true"))));

        // A bad kind, a site that is not a site, and another tenant's site are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await gus.PostAsJsonAsync("/api/v1/content/follows", new { kind = "person", key = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await gus.PostAsJsonAsync("/api/v1/content/follows", new { kind = "site", key = "nope" })).StatusCode);
        var other = await world.CreateTenantAsync("ctp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync("/api/v1/content/follows", new { kind = "site", key = dubaiSite.ToString() })).StatusCode);
    }
}
