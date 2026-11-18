using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 T019 (FR-017, US4 scenario 6): favorites and recents. A favorite is resolved again on every read, so one whose
/// access is withdrawn shows disabled with a reason and no link; recents keep the path alone and can be cleared. Each
/// person sees only their own, and another tenant sees nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class FavoritesTests(TenantWorld world)
{
    private const string Favorites = "/api/v1/me/favorites";
    private const string Recents = "/api/v1/me/recents";

    private static string Slug(string prefix = "f") => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("fav-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "other", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> PageAsync(TestTenant t, bool publish = true)
    {
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = Slug("p") }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        if (publish)
        {
            await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{id}/publish", new { reason = "first" }));
        }
        return id;
    }

    private static async Task<Guid> NodeAsync(TestTenant t, string slug, Guid page, Guid? id = null)
    {
        var node = id ?? Guid.NewGuid();
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/ia/{node}", new { parentId = (Guid?)null, labels = new { en = "Safety", ar = "السلامة" }, slug, pageId = page }));
        return node;
    }

    private static async Task<Guid> NewsAsync(TestTenant t, string title)
    {
        var body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "Wear a helmet." } } } } };
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/content/items",
            new { type = "news", variants = new Dictionary<string, object> { ["en"] = new { title, body } } }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
        return id;
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

    [Fact]
    public async Task A_favorite_whose_access_is_withdrawn_shows_disabled_with_a_reason_and_no_link()
    {
        var t = await FreshAsync();
        var slug = Slug();
        var live = await PageAsync(t);
        var node = await NodeAsync(t, slug, live);
        var news = await NewsAsync(t, "Plant safety week");
        var client = t["employee"].Client;

        var page = await OkAsync(client.PostAsJsonAsync(Favorites, new { path = "/" + slug.ToUpperInvariant() + "/?tab=1#top", label = "Safety" }));
        Assert.Equal("/" + slug, page.GetProperty("href").GetString());
        Assert.True(page.GetProperty("available").GetBoolean());
        var item = await OkAsync(client.PostAsJsonAsync(Favorites, new { source = "content", itemId = news, label = "Safety week" }));
        Assert.Equal($"/content/read/{news}", item.GetProperty("href").GetString());
        // Adding the same one again returns it rather than a second row.
        var again = await OkAsync(client.PostAsJsonAsync(Favorites, new { path = "/" + slug, label = "Safety" }));
        Assert.Equal(page.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.Equal(2, (await OkAsync(client.GetAsync(Favorites))).GetArrayLength());

        // The entry now points to a draft and the news item is withdrawn: both stay listed, disabled, with no link.
        var draft = await PageAsync(t, publish: false);
        await NodeAsync(t, slug, draft, node);
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/content/items/{news}/unpublish", new { }));
        var list = await OkAsync(client.GetAsync(Favorites));
        Assert.Equal(2, list.GetArrayLength());
        foreach (var favorite in list.EnumerateArray())
        {
            Assert.False(favorite.GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, favorite.GetProperty("href").ValueKind);
            Assert.Equal("not_available", favorite.GetProperty("reason").GetString());
        }

        // Access comes back, and so does the link.
        await NodeAsync(t, slug, live, node);
        list = await OkAsync(client.GetAsync(Favorites));
        Assert.Equal("/" + slug, list.EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "page").GetProperty("href").GetString());

        // Something the person can't open now can't be added.
        var refused = await client.PostAsJsonAsync(Favorites, new { path = "/p/" + Slug(), label = "Nope" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.experience.favorite_target", await ErrorKeysAsync(refused));
        var withdrawn = await client.PostAsJsonAsync(Favorites, new { source = "content", itemId = news, label = "Again" });
        Assert.Equal(HttpStatusCode.BadRequest, withdrawn.StatusCode);
        var unknownSource = await client.PostAsJsonAsync(Favorites, new { source = "nowhere", itemId = news, label = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownSource.StatusCode);
        var noLabel = await client.PostAsJsonAsync(Favorites, new { path = "/" + slug, label = " " });
        Assert.Contains("validation.experience.favorite_label", await ErrorKeysAsync(noLabel));
    }

    [Fact]
    public async Task Favorites_are_the_persons_own_and_another_tenant_sees_none()
    {
        var t = await FreshAsync();
        var slug = Slug();
        await NodeAsync(t, slug, await PageAsync(t));
        var mine = await OkAsync(t["employee"].Client.PostAsJsonAsync(Favorites, new { path = "/" + slug, label = "Mine" }));
        var id = mine.GetProperty("id").GetGuid();

        Assert.Equal(0, (await OkAsync(t["other"].Client.GetAsync(Favorites))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await t["other"].Client.DeleteAsync($"{Favorites}/{id}")).StatusCode);

        var outsider = await world.CreateTenantAsync("fav-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(0, (await OkAsync(outsider["admin"].Client.GetAsync(Favorites))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider["admin"].Client.DeleteAsync($"{Favorites}/{id}")).StatusCode);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from experience.favorite"));

        await OkAsync(t["employee"].Client.DeleteAsync($"{Favorites}/{id}"), HttpStatusCode.NoContent);
        Assert.Equal(0, (await OkAsync(t["employee"].Client.GetAsync(Favorites))).GetArrayLength());
    }

    [Fact]
    public async Task A_person_keeps_at_most_fifty_favorites()
    {
        var t = await FreshAsync();
        var slug = Slug();
        await NodeAsync(t, slug, await PageAsync(t));
        var user = t["employee"].Id;
        for (var i = 0; i < 50; i++)
        {
            await world.Database.TenantScalarAsync<object>(t.Id,
                "insert into experience.favorite (tenant_id, id, user_id, kind, path, label, position, created_at) values (@tenant, gen_random_uuid(), @user, 'page', @path, 'x', @i, now()) returning id",
                ("tenant", t.Id), ("user", user), ("path", $"/filler-{i}"), ("i", i));
        }
        var full = await t["employee"].Client.PostAsJsonAsync(Favorites, new { path = "/" + slug, label = "One more" });
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Contains("conflict.experience.favorites_full", await ErrorKeysAsync(full));
    }

    [Fact]
    public async Task Recents_keep_the_path_alone_the_last_thirty_and_can_be_cleared()
    {
        var t = await FreshAsync();
        var slug = Slug();
        var node = await NodeAsync(t, slug, await PageAsync(t));
        var client = t["employee"].Client;

        await OkAsync(client.PostAsJsonAsync(Recents, new { path = "/" + slug + "?q=salary#x", label = "Safety" }), HttpStatusCode.NoContent);
        await OkAsync(client.PostAsJsonAsync(Recents, new { path = "/leave/requests/abc", label = "My leave" }), HttpStatusCode.NoContent);
        var recents = await OkAsync(client.GetAsync(Recents));
        Assert.Equal(["/leave/requests/abc", "/" + slug], recents.EnumerateArray().Select(r => r.GetProperty("path").GetString()));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from experience.recent_visit where path like '%?%' or path like '%#%'"));
        Assert.Equal(0, (await OkAsync(t["other"].Client.GetAsync(Recents))).GetArrayLength());

        var bad = await client.PostAsJsonAsync(Recents, new { path = "https://example.org/x", label = "Away" });
        Assert.Contains("validation.experience.recent_path", await ErrorKeysAsync(bad));

        // A shell address the person can no longer open drops out of the list.
        await NodeAsync(t, slug, await PageAsync(t, publish: false), node);
        recents = await OkAsync(client.GetAsync(Recents));
        Assert.Equal(["/leave/requests/abc"], recents.EnumerateArray().Select(r => r.GetProperty("path").GetString()));

        for (var i = 0; i < 35; i++)
        {
            await OkAsync(client.PostAsJsonAsync(Recents, new { path = $"/tasks/{i}", label = $"Task {i}" }), HttpStatusCode.NoContent);
        }
        Assert.Equal(30L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from experience.recent_visit where user_id = @user", ("user", t["employee"].Id)));
        recents = await OkAsync(client.GetAsync(Recents));
        Assert.Equal("/tasks/34", recents[0].GetProperty("path").GetString());

        await OkAsync(client.DeleteAsync(Recents), HttpStatusCode.NoContent);
        Assert.Equal(0, (await OkAsync(client.GetAsync(Recents))).GetArrayLength());
    }
}
