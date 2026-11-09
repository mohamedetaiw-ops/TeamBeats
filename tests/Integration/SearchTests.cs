using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 slice 1 (US1, FR-001 to FR-005, FR-009, FR-014, SC-001): published news, documents, policy clauses,
/// services and people are indexed from their owners' events and found by keyword in English and Arabic, with semantic
/// passages fused in; every result, count, facet and suggestion holds only what the person may read now; superseded,
/// unpublished and deleted items leave the index; search degrades to keyword only when embeddings fail; rebuilds need
/// <c>search.manage</c>; other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchTests(TenantWorld world)
{
    private const string Search = "/api/v1/search";
    private const string Items = "/api/v1/content/items";
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Policies = "/api/v1/policies";
    private const string Services = "/api/v1/services/admin";

    private sealed record Org(TestTenant T, Guid Dubai, Guid Cairo);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("se-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        var cairo = await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "pia", "ed", "emp", "cai", "lm", "co", "legal" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "emp", "Dubai");
        await world.AddMembershipAsync(t, "cai", "Cairo");
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        await world.AssignAsync(t, "lm", BuiltInRoles.OrganizationAdministrator, "org_subtree", dubai);
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return new Org(t, dubai, cairo);
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

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static Task<JsonElement> SearchAsync(HttpClient client, string q, string extra = "") =>
        OkAsync(client.GetAsync($"{Search}?q={Uri.EscapeDataString(q)}{extra}"));

    private static Guid[] ItemIds(JsonElement page) =>
        page.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("itemId").GetGuid()).ToArray();

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(HttpClient client, string en, string body, string? ar = null, string? classification = null)
    {
        var variants = new Dictionary<string, object> { ["en"] = new { title = en, body = Body(body) } };
        if (ar is not null)
        {
            variants["ar"] = new { title = ar, body = Body(ar) };
        }
        var id = Id(await OkAsync(client.PostAsJsonAsync(Items, new { type = "news", classification, variants }), HttpStatusCode.Created));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private Task<long> CountAsync(TestTenant t, string sql) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<long>(sql, null, CancellationToken.None));

    [Fact]
    public async Task News_is_found_in_english_and_arabic_and_every_number_counts_only_what_the_reader_may_open()
    {
        var o = await FreshAsync();
        var t = o.T;
        var open = await NewsAsync(t["pia"].Client, "Forklift safety week", "Every forklift gets a brake check this week.", "أسبوع سلامة الرافعات");
        var closed = await NewsAsync(t["pia"].Client, "Forklift budget for the board", "Confidential numbers for new forklifts.", classification: "confidential");
        var leave = await NewsAsync(t["pia"].Client, "Summer leave", "Plan your summer leave early.", "إجازة الصيف");
        await DispatchAsync();

        // The employee finds the open item only; nothing about the confidential one shows in the total or the facets.
        var page = await SearchAsync(t["emp"].Client, "forklift");
        Assert.Equal([open], ItemIds(page));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.False(page.GetProperty("more").GetBoolean());
        Assert.False(page.GetProperty("degraded").GetBoolean());
        Assert.Equal(1, page.GetProperty("facets").GetProperty("content").GetInt32());
        var hit = page.GetProperty("results")[0];
        Assert.Equal("content", hit.GetProperty("source").GetString());
        Assert.Equal($"/content/read/{open}", hit.GetProperty("link").GetString());
        Assert.Contains("forklift", hit.GetProperty("snippet").GetString()!, StringComparison.OrdinalIgnoreCase);
        // An editor holds content.view across the tenant and finds both.
        var editor = await SearchAsync(t["ed"].Client, "forklifts");
        Assert.Equal(2, editor.GetProperty("total").GetInt32());
        Assert.Contains(closed, ItemIds(editor));

        // Arabic folding: alef with hamza and ta marbuta meet their plain forms; the Arabic unit links to the same item.
        var arabic = await SearchAsync(t["emp"].Client, "اجازه");
        Assert.Equal([leave], ItemIds(arabic));
        Assert.Equal("ar", arabic.GetProperty("results")[0].GetProperty("locale").GetString());
        Assert.Equal([open], ItemIds(await SearchAsync(t["emp"].Client, "الرافعات")));

        // Type-ahead completes the last word on titles and leaves out what the reader cannot open.
        var suggestions = await OkAsync(t["emp"].Client.GetAsync($"{Search}/suggest?q=fork"));
        Assert.Equal([open], suggestions.EnumerateArray().Select(s => s.GetProperty("itemId").GetGuid()).ToArray());
        Assert.Equal([closed], (await OkAsync(t["ed"].Client.GetAsync($"{Search}/suggest?q=forklift%20b"))).EnumerateArray().Select(s => s.GetProperty("itemId").GetGuid()).ToArray());
        Assert.Empty((await OkAsync(t["emp"].Client.GetAsync($"{Search}/suggest?q=f"))).EnumerateArray());

        // Passages carry embeddings from the gateway; restricted text is never embedded.
        Assert.True(await CountAsync(t, "select count(*) from search.unit where embedding is not null") >= 3);

        // Unpublished news leaves the index.
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{open}/unpublish", new { }));
        await DispatchAsync();
        Assert.Empty(ItemIds(await SearchAsync(t["emp"].Client, "forklift")));
        Assert.Equal(0, await CountAsync(t, $"select count(*) from search.unit where item_id = '{open}'"));

        // Another tenant finds nothing of this one.
        var other = await world.CreateTenantAsync("se-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var foreign = await SearchAsync(other["admin"].Client, "forklift");
        Assert.Equal(0, foreign.GetProperty("total").GetInt32());
        Assert.Empty(ItemIds(await SearchAsync(other["admin"].Client, "summer leave")));
    }

    [Fact]
    public async Task Documents_policy_clauses_and_services_are_found_by_people_who_can_open_them()
    {
        var o = await FreshAsync();
        var t = o.T;

        // A Dubai library: Dubai members find its document by file name; the Cairo member does not.
        var library = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Quality", ["ar"] = "الجودة" }, scope = "org_unit", scopeId = o.Dubai,
        }), HttpStatusCode.Created));
        var upload = await t["lm"].Client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString("Scaffolding inspection.pdf")}&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes("inspection steps")));
        var doc = Id(await OkAsync(Task.FromResult(upload), HttpStatusCode.Created));

        // A policy with a gifts clause, published as version 1 after one approval.
        var policy = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "POL-777", names = new Dictionary<string, string> { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" },
            route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        await ReviseAsync(t, policy, "Gifts and hospitality", material: true);

        // A service everyone may request.
        var service = Id((await OkAsync(t["admin"].Client.PostAsJsonAsync(Services, new
        {
            kind = "form", names = new Dictionary<string, string> { ["en"] = "Parking permit", ["ar"] = "تصريح موقف" },
        }), HttpStatusCode.Created)).GetProperty("service"));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Services}/{service}/draft", new
        {
            form = new { fields = new object[] { new { key = "plate", type = "text", labels = new Dictionary<string, string> { ["en"] = "Plate", ["ar"] = "اللوحة" }, required = true } } },
        }));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Services}/{service}/publish", new { }));
        await DispatchAsync();

        Assert.Equal([doc], ItemIds(await SearchAsync(t["emp"].Client, "scaffolding")));
        Assert.Empty(ItemIds(await SearchAsync(t["cai"].Client, "scaffolding")));
        Assert.Equal(0, (await SearchAsync(t["cai"].Client, "scaffolding")).GetProperty("total").GetInt32());

        var clause = await SearchAsync(t["cai"].Client, "hospitality");
        Assert.Equal([policy], ItemIds(clause));
        Assert.Equal("policy", clause.GetProperty("results")[0].GetProperty("kind").GetString());
        Assert.StartsWith($"/policies/{policy}", clause.GetProperty("results")[0].GetProperty("link").GetString());
        Assert.Contains("#clause-", clause.GetProperty("results")[0].GetProperty("link").GetString());

        var parking = await SearchAsync(t["emp"].Client, "parking", "&kind=service");
        Assert.Equal([service], ItemIds(parking));
        Assert.Equal($"/services/{service}", parking.GetProperty("results")[0].GetProperty("link").GetString());
        Assert.Equal([service], ItemIds(await SearchAsync(t["emp"].Client, "تصريح")));

        // Nothing matches every word, and the query is too far from every passage for a semantic match: services that
        // match any word are offered instead (US1 scenario 5).
        var none = await SearchAsync(t["emp"].Client, "parking zebra giraffe lion tiger otter");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Contains(none.GetProperty("alternatives").EnumerateArray(), a => a.GetProperty("itemId").GetGuid() == service);

        // A new version supersedes the gifts clause: only the version in force is found.
        await ReviseAsync(t, policy, "Remote work", material: false);
        await DispatchAsync();
        Assert.Empty(ItemIds(await SearchAsync(t["emp"].Client, "hospitality")));
        Assert.Equal([policy], ItemIds(await SearchAsync(t["emp"].Client, "remote")));

        // A deleted document leaves the index.
        await OkAsync(t["lm"].Client.DeleteAsync($"/api/v1/documents/{doc}"), HttpStatusCode.NoContent);
        await DispatchAsync();
        Assert.Empty(ItemIds(await SearchAsync(t["emp"].Client, "scaffolding")));
    }

    private static async Task ReviseAsync(TestTenant t, Guid policy, string clause, bool material)
    {
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = new Dictionary<string, string> { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" },
            clauses = new[]
            {
                new
                {
                    level = 1, titles = new Dictionary<string, string> { ["en"] = clause, ["ar"] = clause + " ع" },
                    bodies = new Dictionary<string, object> { ["en"] = Body(clause + " rules apply to everyone."), ["ar"] = Body(clause + " نص") },
                },
            },
            material,
        }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
    }

    [Fact]
    public async Task Search_falls_back_to_keyword_only_when_embeddings_fail()
    {
        var o = await FreshAsync();
        var t = o.T;
        var provider = world.Factory.OpenRouter;
        try
        {
            provider.Unavailable = true;
            // Indexing still writes keyword rows, without embeddings.
            var id = await NewsAsync(t["pia"].Client, "Canteen menu", "Lentil soup on Monday.");
            await DispatchAsync();
            Assert.Equal(0, await CountAsync(t, "select count(*) from search.unit where embedding is not null"));
            var page = await SearchAsync(t["emp"].Client, "lentil");
            Assert.Equal([id], ItemIds(page));
            Assert.True(page.GetProperty("degraded").GetBoolean());
        }
        finally
        {
            provider.Unavailable = false;
        }
        Assert.False((await SearchAsync(t["emp"].Client, "lentil")).GetProperty("degraded").GetBoolean());
    }

    [Fact]
    public async Task A_rebuild_needs_search_manage_and_indexes_every_owner_including_people()
    {
        var o = await FreshAsync();
        var t = o.T;
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.PostAsJsonAsync($"{Search}/rebuilds", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync($"{Search}/rebuilds")).StatusCode);

        // People are indexed by a rebuild (the bootstrap gives every tenant one) and by profile changes.
        await DispatchAsync();
        Assert.Empty(ItemIds(await SearchAsync(t["emp"].Client, "legal")));
        var queued = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/rebuilds", new { }), HttpStatusCode.Accepted);
        await DispatchAsync();
        var done = Assert.Single((await OkAsync(t["admin"].Client.GetAsync($"{Search}/rebuilds"))).EnumerateArray());
        Assert.Equal(Id(queued), Id(done));
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("completedAt").ValueKind);
        Assert.True(done.GetProperty("items").GetInt32() >= 8, done.ToString());

        var person = await SearchAsync(t["emp"].Client, "legal");
        Assert.Equal([t["legal"].Id], ItemIds(person));
        Assert.Equal("person", person.GetProperty("results")[0].GetProperty("kind").GetString());
        Assert.Equal($"/people/{t["legal"].Id}", person.GetProperty("results")[0].GetProperty("link").GetString());

        // A disabled member leaves the index.
        Assert.True((await t["admin"].Client.PostAsync($"/api/v1/admin/users/{t["legal"].Id}:disable", null)).IsSuccessStatusCode);
        await DispatchAsync();
        Assert.Empty(ItemIds(await SearchAsync(t["emp"].Client, "legal")));
        Assert.Equal(0, await CountAsync(t, $"select count(*) from search.unit where item_id = '{t["legal"].Id}'"));

        // Rows a rebuild did not touch are removed.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await session.ExecAsync(
                """
                insert into search.unit (tenant_id, id, source, item_id, unit_key, position, kind, locale, title, body, link, classification, updated_at, synced_at, tsv)
                values (platform.current_tenant_id(), gen_random_uuid(), 'content', gen_random_uuid(), 'en', 0, 'content', 'en', 'Orphan', 'Orphan', '/x', 'internal', now(), now() - interval '1 day', to_tsvector('simple', 'orphan'))
                """, null, CancellationToken.None);
            await session.CommitAsync();
            return 0;
        });
        Assert.Equal(1, await CountAsync(t, "select count(*) from search.unit where title = 'Orphan'"));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Search}/rebuilds", new { }), HttpStatusCode.Accepted);
        await DispatchAsync();
        Assert.Equal(0, await CountAsync(t, "select count(*) from search.unit where title = 'Orphan'"));
    }

    [Fact]
    public async Task Bad_queries_are_refused_with_resource_keys()
    {
        var o = await FreshAsync();
        var emp = o.T["emp"].Client;
        Assert.Contains("validation.search.query", await ErrorKeysAsync(await emp.GetAsync($"{Search}?q=")));
        Assert.Contains("validation.search.query", await ErrorKeysAsync(await emp.GetAsync($"{Search}?q={new string('a', 201)}")));
        Assert.Contains("validation.search.kind", await ErrorKeysAsync(await emp.GetAsync($"{Search}?q=x&kind=email")));
        Assert.Contains("validation.search.page", await ErrorKeysAsync(await emp.GetAsync($"{Search}?q=x&page=0")));
        Assert.Contains("validation.search.page", await ErrorKeysAsync(await emp.GetAsync($"{Search}?q=x&page=51")));
        // Query operators typed by a person are plain words.
        Assert.Equal(0, (await SearchAsync(emp, "a & | ! ( ) :* '")).GetProperty("total").GetInt32());
        // Without a session there is no search.
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(o.T.Key, null).GetAsync($"{Search}?q=x")).StatusCode);
    }
}
