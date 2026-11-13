using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T011, the scopes of search (012), the assistant (014), the task inbox (016) and drafting news (009).
/// Search filters every candidate for the caller in its owning module, so an app with the App reader role at Acme Gulf
/// finds Gulf content and Gulf people only; the assistant answers a person with citations they may read; the task
/// inbox lists the person's own tasks and links to act on them in TeamBeats; a draft is created in the person's name and
/// never published, and an <c>Idempotency-Key</c> replays the first answer.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicActionEndpointTests(TenantWorld world)
{
    private const string Public = "/api/public/v1";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pa-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        foreach (var (name, unit) in new[] { ("gia", "Gulf"), ("eli", "Egypt"), ("pia", "Gulf"), ("co", "Gulf"), ("legal", "Gulf") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return new Org(t, gulf, egypt);
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private Task<HttpResponseMessage> GetAsync(TestTenant t, string token, string path) => SendAsync(world, t, token, HttpMethod.Get, path);

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(TestTenant t, Guid? site, string title, string body)
    {
        var item = Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/items", new
        {
            siteId = site, type = "news", variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } },
        }), HttpStatusCode.Created));
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/content/items/{item}/publish", new { material = true }));
        return item;
    }

    [Fact]
    public async Task Search_finds_only_what_the_caller_may_read()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gulfSite = Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/sites",
            new { names = new Dictionary<string, string> { ["en"] = "Gulf news" }, orgUnitId = o.Gulf }), HttpStatusCode.Created));
        var egyptSite = Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/sites",
            new { names = new Dictionary<string, string> { ["en"] = "Egypt news" }, orgUnitId = o.Egypt }), HttpStatusCode.Created));
        var gulfNews = await NewsAsync(t, gulfSite, "Forklift checklist Gulf", "Check the forklift brakes before every shift.");
        var egyptNews = await NewsAsync(t, egyptSite, "Forklift checklist Egypt", "Check the forklift horn before every shift.");
        await DispatchAsync();

        var gulfApp = await AppAsync(world, t, ["search:query"], "org_unit", o.Gulf);
        var found = await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/search?q=forklift"));
        var ids = found.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("itemId").GetGuid()).ToList();
        Assert.Contains(gulfNews, ids);
        Assert.DoesNotContain(egyptNews, ids);
        Assert.Equal(ids.Count, found.GetProperty("total").GetInt32());
        Assert.StartsWith("https://", found.GetProperty("items")[0].GetProperty("webUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, found.GetProperty("nextCursor").ValueKind);

        // People: the app finds Gulf people only; the person finds everyone the directory shows them.
        var people = (await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/search?q=eli&kind=person"))).GetProperty("items");
        Assert.DoesNotContain(t["eli"].Id, people.EnumerateArray().Select(r => r.GetProperty("itemId").GetGuid()));

        var person = await PersonAsync(world, t, "gia", ["search:query"]);
        var mine = (await JsonAsync(await GetAsync(t, person, $"{Public}/search?q=forklift"))).GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("itemId").GetGuid()).ToList();
        Assert.Contains(gulfNews, mine);
        Assert.Contains(egyptNews, mine);

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(t, person, $"{Public}/search?q=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(t, person, $"{Public}/search?q=forklift&cursor=nope")).StatusCode);
        // Another tenant's search knows nothing of these items.
        var other = await world.CreateTenantAsync("pa-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var foreign = await AppAsync(world, other, ["search:query"], "tenant", null);
        Assert.Empty((await JsonAsync(await GetAsync(other, foreign, $"{Public}/search?q=forklift"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_assistant_answers_a_person_with_citations_they_may_read()
    {
        var o = await FreshAsync();
        var t = o.T;
        var open = await NewsAsync(t, null, "Forklift inspection checklist", "Check the forklift brakes, horn and mast chains before every shift.");
        await DispatchAsync();

        var person = await PersonAsync(world, t, "gia", ["assistant:ask"]);
        var answer = await JsonAsync(await SendAsync(world, t, person, HttpMethod.Post, $"{Public}/assistant/answers",
            new { question = "What do I check on a forklift before a shift?" }));
        Assert.Equal("answered", answer.GetProperty("outcome").GetString());
        Assert.True(answer.GetProperty("generated").GetBoolean());
        Assert.Equal(open, Assert.Single(answer.GetProperty("citations").EnumerateArray()).GetProperty("itemId").GetGuid());
        var conversation = answer.GetProperty("conversationId").GetGuid();
        var next = await JsonAsync(await SendAsync(world, t, person, HttpMethod.Post, $"{Public}/assistant/answers",
            new { question = "And the horn?", conversationId = conversation }));
        Assert.Equal(conversation, next.GetProperty("conversationId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(world, t, person, HttpMethod.Post, $"{Public}/assistant/answers", new { question = "" })).StatusCode);

        // FR-030: when the model provider fails, the answer says so plainly instead of failing the call.
        var down = await JsonAsync(await SendAsync(world, t, person, HttpMethod.Post, $"{Public}/assistant/answers",
            new { question = "What do I check on a forklift before a shift? [fake:500]" }));
        Assert.Equal("unavailable", down.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, down.GetProperty("answer").ValueKind);
        Assert.Empty(down.GetProperty("citations").EnumerateArray());
    }

    [Fact]
    public async Task The_task_inbox_lists_the_persons_own_tasks_with_a_link_to_act_in_TeamBeats()
    {
        var o = await FreshAsync();
        var t = o.T;
        var policy = Id(await JsonAsync(await t["co"].Client.PostAsJsonAsync("/api/v1/policies", new
        {
            code = "T-1", names = new Dictionary<string, string> { ["en"] = "Travel" }, orgUnitId = o.Gulf, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        await JsonAsync(await t["co"].Client.PutAsJsonAsync($"/api/v1/policies/{policy}/draft", new
        {
            titles = new Dictionary<string, string> { ["en"] = "Travel" }, material = true,
            clauses = new[] { new { level = 1, titles = new Dictionary<string, string> { ["en"] = "Flights" }, bodies = new Dictionary<string, object> { ["en"] = Body("Economy") } } },
        }));
        await JsonAsync(await t["co"].Client.PostAsJsonAsync($"/api/v1/policies/{policy}/submit", new { }));

        var legal = await PersonAsync(world, t, "legal", ["tasks:read"]);
        var task = Assert.Single((await JsonAsync(await GetAsync(t, legal, $"{Public}/tasks"))).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("source").GetString() == "policy_approval");
        Assert.StartsWith("https://", task.GetProperty("webUrl").GetString(), StringComparison.Ordinal);
        // Someone else's inbox has no such task, and there is no operation to decide one.
        var gia = await PersonAsync(world, t, "gia", ["tasks:read"]);
        Assert.DoesNotContain((await JsonAsync(await GetAsync(t, gia, $"{Public}/tasks"))).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("taskId").GetGuid() == task.GetProperty("taskId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(world, t, legal, HttpMethod.Post, $"{Public}/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" })).StatusCode);
    }

    [Fact]
    public async Task A_draft_is_created_in_the_persons_name_never_published_and_replayed_for_the_same_key()
    {
        var o = await FreshAsync();
        var t = o.T;
        var author = await PersonAsync(world, t, "pia", ["content:draft", "content:read"]);
        var key = Guid.NewGuid().ToString();
        var request = new { type = "news", title = "Canteen menu", summary = "This week", body = Body("Soup on Monday") };
        var first = await SendAsync(world, t, author, HttpMethod.Post, $"{Public}/content/drafts", request, r => r.Headers.TryAddWithoutValidation("Idempotency-Key", key));
        var draft = await JsonAsync(first, HttpStatusCode.Created);
        Assert.Equal("draft", draft.GetProperty("status").GetString());
        Assert.Equal("Canteen menu", draft.GetProperty("title").GetString());
        var again = await SendAsync(world, t, author, HttpMethod.Post, $"{Public}/content/drafts", request, r => r.Headers.TryAddWithoutValidation("Idempotency-Key", key));
        Assert.Equal(Id(draft), Id(await JsonAsync(again, HttpStatusCode.Created)));
        Assert.Equal("true", again.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from content.content_item where type = 'news' and status = 'draft'"));

        // Readers never see it, and the person sees it in TeamBeats as their own draft.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, author, $"{Public}/content/{Id(draft)}")).StatusCode);
        Assert.Equal(t["pia"].Id, (await JsonAsync(await t["pia"].Client.GetAsync($"/api/v1/content/items/{Id(draft)}"))).GetProperty("createdBy").GetProperty("id").GetGuid());

        // Only news and pages, and only for people who may author content.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(world, t, author, HttpMethod.Post, $"{Public}/content/drafts", request with { type = "announcement" })).StatusCode);
        var reader = await PersonAsync(world, t, "gia", ["content:draft"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(world, t, reader, HttpMethod.Post, $"{Public}/content/drafts", request)).StatusCode);
    }
}
