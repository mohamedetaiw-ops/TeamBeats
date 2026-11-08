using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 slice 2 (US1 scenarios 1 to 6, FR-003, FR-005): the approval route on the process engine with its task in
/// the spec 016 inbox; return with a comment kept in the history; approval before or after the wanted time; nobody
/// approves their own item; a live item stays readable while its next version is reviewed; and the feed shows live
/// items to their audience only, while access stays apart from targeting. Tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentReviewTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private sealed record Org(TestTenant T, Guid Site, Guid Ksa, Guid Riyadh, Guid Cairo);

    private async Task<Org> FreshAsync(params string[] approvers)
    {
        var t = await world.CreateTenantAsync("cr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var ksa = await world.CreateUnitAsync(t, "KSA", holding, "subsidiary");
        var riyadh = await world.CreateUnitAsync(t, "Riyadh", ksa, "department");
        var cairo = await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "layla", "pia", "kim", "rana", "omar", "ksa_staff" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "rana", "Riyadh");
        await world.AddMembershipAsync(t, "ksa_staff", "KSA");
        await world.AddMembershipAsync(t, "omar", "Cairo");
        await world.AssignAsync(t, "layla", BuiltInRoles.ContentEditor, "tenant", null);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var sites = await (await t["admin"].Client.GetAsync("/api/v1/content/sites")).ReadAsync<JsonElement>();
        var site = sites[0].GetProperty("id").GetGuid();
        var org = new Org(t, site, ksa, riyadh, cairo);
        await RouteAsync(org, approvers.Length == 0 ? null : approvers.Select(a => t[a].Id).ToArray());
        return org;
    }

    private static async Task RouteAsync(Org o, Guid[]? approvers)
    {
        var routes = approvers is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object> { ["news"] = new { approval = true, approvers } };
        Assert.Equal(HttpStatusCode.OK, (await o.T["admin"].Client.PutAsJsonAsync($"/api/v1/content/sites/{o.Site}", new { routes })).StatusCode);
    }

    private static object Variants(string en) => new Dictionary<string, object>
    {
        ["en"] = new { title = en, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = en } } } } } },
    };

    private static async Task<Guid> CreateAsync(HttpClient client, string title, object? audience = null, string type = "news", string? classification = null,
        bool pinned = false)
    {
        var response = await client.PostAsJsonAsync(Items, new { type, variants = Variants(title), audience, classification, pinned });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string?> DetailKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : problem.GetProperty("code").GetString();
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().ToArray();

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid task, string outcome, string? comment = null) =>
        client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment });

    private static string Status(JsonElement e) => e.GetProperty("status").GetString()!;

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    private Task<int> RunScheduleAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private static DateTime Local(DateTimeOffset utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai")), DateTimeKind.Unspecified);

    private static async Task<Guid[]> FeedAsync(HttpClient client, string query = "") =>
        (await OkAsync(client.GetAsync("/api/v1/content/feed" + query))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task A_submitted_item_goes_to_the_named_approver_and_publishes_at_the_wanted_time_for_its_audience_only()
    {
        var o = await FreshAsync("kim");
        var layla = o.T["layla"].Client;
        var audience = new { anyOf = new[] { new { orgUnits = new[] { new { id = o.Ksa, subtree = true } } } } };
        var id = await CreateAsync(layla, "New Riyadh office", audience);

        // The site routes news through approval, so even a publisher cannot put the draft live directly.
        Assert.Equal("conflict.content.approval_required", await DetailKeyAsync(await o.T["pia"].Client.PostAsJsonAsync($"{Items}/{id}/publish", new { }), HttpStatusCode.Conflict));

        var at = DateTimeOffset.UtcNow.AddHours(2);
        var submitted = await OkAsync(layla.PostAsJsonAsync($"{Items}/{id}/submit", new { publishAt = Local(at) }));
        Assert.Equal("in_review", Status(submitted));
        Assert.True(submitted.GetProperty("inReview").GetBoolean());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentSubmitted, id.ToString());
        Assert.Equal("conflict.content.in_review", await DetailKeyAsync(await layla.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Changed") }), HttpStatusCode.Conflict));

        await DispatchAsync();
        Assert.Contains(await NotificationLinksAsync(o.T["kim"].Client), l => l.StartsWith($"/content/items/{id}", StringComparison.Ordinal));

        var task = Assert.Single(await InboxAsync(o.T["kim"].Client));
        Assert.Equal("content_review", task.GetProperty("source").GetString());
        Assert.Equal("New Riyadh office", task.GetProperty("title").GetProperty("en").GetString());
        Assert.Equal($"/content/items/{id}", task.GetProperty("link").GetString());
        Assert.Equal(o.T["layla"].Id, task.GetProperty("requester").GetProperty("id").GetGuid());
        Assert.Empty(await InboxAsync(o.T["pia"].Client));
        Assert.Empty(await InboxAsync(layla));
        Assert.Equal(1, (await OkAsync(o.T["kim"].Client.GetAsync("/api/v1/tasks/count"))).GetProperty("open").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(o.T["kim"].Client, task.GetProperty("taskId").GetGuid(), "approve")).StatusCode);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentApproved, id.ToString());
        var approved = await OkAsync(layla.GetAsync($"{Items}/{id}"));
        Assert.Equal("scheduled", Status(approved));
        Assert.False(approved.GetProperty("inReview").GetBoolean());
        Assert.Empty(await FeedAsync(o.T["rana"].Client));

        Assert.Equal(1, await RunScheduleAsync(o.T, at.AddMinutes(1)));
        Assert.Equal("published", Status(await OkAsync(layla.GetAsync($"{Items}/{id}"))));
        var versions = (await OkAsync(layla.GetAsync($"{Items}/{id}/versions"))).EnumerateArray().ToArray();
        Assert.Equal(o.T["kim"].Id, versions[0].GetProperty("approver").GetProperty("id").GetGuid());

        // Rana is in Riyadh, inside the KSA subtree; Omar is in Cairo, a sibling. Targeting is not access: Omar may still open it by link.
        Assert.Contains(id, await FeedAsync(o.T["rana"].Client));
        Assert.DoesNotContain(id, await FeedAsync(o.T["omar"].Client));
        Assert.Equal(HttpStatusCode.OK, (await o.T["omar"].Client.GetAsync($"{Items}/{id}/view")).StatusCode);

        var history = (await OkAsync(layla.GetAsync($"{Items}/{id}/reviews"))).EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToArray();
        Assert.Equal(["submitted", "approved"], history);
    }

    [Fact]
    public async Task A_returned_item_goes_back_to_draft_with_the_comment_in_its_history_and_its_writer_is_told()
    {
        var o = await FreshAsync("kim");
        var layla = o.T["layla"].Client;
        var id = await CreateAsync(layla, "Holiday schedule");
        await OkAsync(layla.PostAsJsonAsync($"{Items}/{id}/submit", new { }));
        var task = Assert.Single(await InboxAsync(o.T["kim"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(o.T["kim"].Client, task, "return", "Fix the dates")).StatusCode);

        var item = await OkAsync(layla.GetAsync($"{Items}/{id}"));
        Assert.Equal("draft", Status(item));
        Assert.False(item.GetProperty("inReview").GetBoolean());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentReturned, id.ToString());
        var returned = (await OkAsync(layla.GetAsync($"{Items}/{id}/reviews"))).EnumerateArray().Last();
        Assert.Equal("returned", returned.GetProperty("event").GetString());
        Assert.Equal("Fix the dates", returned.GetProperty("comment").GetString());
        Assert.Equal(o.T["kim"].Id, returned.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Contains($"/content/items/{id}", await NotificationLinksAsync(layla));
        Assert.Empty(await InboxAsync(o.T["kim"].Client));

        // Fixed and sent again: a new review starts.
        await OkAsync(layla.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Holiday schedule 2027") }));
        Assert.Equal("in_review", Status(await OkAsync(layla.PostAsJsonAsync($"{Items}/{id}/submit", new { }))));
        Assert.Single(await InboxAsync(o.T["kim"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["rana"].Client.GetAsync($"{Items}/{id}/reviews")).StatusCode);
    }

    [Fact]
    public async Task Approval_after_the_wanted_time_publishes_at_once_and_tells_the_person_who_submitted()
    {
        var o = await FreshAsync("kim");
        var layla = o.T["layla"].Client;
        var id = await CreateAsync(layla, "Town hall moved");
        await OkAsync(layla.PostAsJsonAsync($"{Items}/{id}/submit", new { publishAt = Local(DateTimeOffset.UtcNow.AddMinutes(30)) }));
        // The wanted time passes while the item waits; the scheduler does not publish an item still in review.
        await world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            await sp.GetRequiredService<IDbSession>().ExecAsync("update content.content_item set publish_at = now() - interval '1 hour' where id = @id", new { id });
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(0, await RunScheduleAsync(o.T, DateTimeOffset.UtcNow));

        var task = Assert.Single(await InboxAsync(o.T["kim"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(o.T["kim"].Client, task, "approve")).StatusCode);
        Assert.Equal("published", Status(await OkAsync(layla.GetAsync($"{Items}/{id}"))));
        var events = (await OkAsync(layla.GetAsync($"{Items}/{id}/reviews"))).EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToArray();
        Assert.Equal(["submitted", "approved", "schedule_missed"], events);
        Assert.Contains($"/content/items/{id}", await NotificationLinksAsync(layla));
    }

    [Fact]
    public async Task Nobody_approves_their_own_item_and_the_site_publishers_decide_when_the_writer_is_the_only_approver()
    {
        var o = await FreshAsync("layla");
        var layla = o.T["layla"].Client;
        var id = await CreateAsync(layla, "My own news");
        await OkAsync(layla.PostAsJsonAsync($"{Items}/{id}/submit", new { }));
        Assert.Empty(await InboxAsync(layla));
        Assert.Empty(await InboxAsync(o.T["kim"].Client));
        var task = Assert.Single(await InboxAsync(o.T["pia"].Client)).GetProperty("taskId").GetGuid();
        // A crafted call by the writer is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(layla, task, "approve")).StatusCode);
        // Publishers decide by permission at the site's unit; a plain member does not.
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(o.T["rana"].Client, task, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(o.T["pia"].Client, task, "approve")).StatusCode);
        Assert.Equal("published", Status(await OkAsync(layla.GetAsync($"{Items}/{id}"))));

        // A publisher who wrote the item and submits it does not see their own review either.
        var own = await CreateAsync(o.T["pia"].Client, "Publisher's own news");
        await OkAsync(o.T["pia"].Client.PostAsJsonAsync($"{Items}/{own}/submit", new { }));
        Assert.DoesNotContain(own.ToString(), (await InboxAsync(o.T["pia"].Client)).Select(t => t.GetProperty("link").GetString()!.Split('/').Last()));
    }

    [Fact]
    public async Task A_live_item_stays_readable_while_its_next_version_is_reviewed_and_a_withdrawn_review_leaves_the_inbox()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var id = await CreateAsync(o.T["layla"].Client, "Canteen opening hours");
        await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        Assert.Equal("conflict.content.no_route", await DetailKeyAsync(await o.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/submit", new { }), HttpStatusCode.Conflict));
        await RouteAsync(o, [o.T["kim"].Id]);

        await OkAsync(o.T["layla"].Client.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Canteen opening hours, Ramadan") }));
        var submitted = await OkAsync(o.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/submit", new { }));
        Assert.Equal("published", Status(submitted));
        Assert.True(submitted.GetProperty("inReview").GetBoolean());
        Assert.Equal("Canteen opening hours", (await OkAsync(o.T["rana"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("title").GetString());
        Assert.Contains(id, await FeedAsync(o.T["rana"].Client));

        // Withdrawn by the person who submitted it: the task leaves the inbox and the draft can be edited again.
        // A reader who does not work on the site gets not found; an author who did not submit it is refused.
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["rana"].Client.PostAsJsonAsync($"{Items}/{id}/withdraw", new { })).StatusCode);
        var withdrawn = await OkAsync(o.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/withdraw", new { }));
        Assert.False(withdrawn.GetProperty("inReview").GetBoolean());
        Assert.Equal("published", Status(withdrawn));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentWithdrawn, id.ToString());
        Assert.Empty(await InboxAsync(o.T["kim"].Client));
        Assert.Equal("conflict.content.not_in_review", await DetailKeyAsync(await o.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/withdraw", new { }), HttpStatusCode.Conflict));

        await OkAsync(o.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/submit", new { }));
        var task = Assert.Single(await InboxAsync(o.T["kim"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(o.T["kim"].Client, task, "approve")).StatusCode);
        Assert.Equal("Canteen opening hours, Ramadan", (await OkAsync(o.T["rana"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("title").GetString());
        // The decided task cannot be decided again.
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(o.T["kim"].Client, task, "approve")).StatusCode);
    }

    [Fact]
    public async Task The_feed_pages_pinned_first_and_respects_unit_targets_without_subtree_and_classification()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        async Task<Guid> LiveAsync(string title, object? audience = null, string? classification = null, bool pinned = false)
        {
            var id = await CreateAsync(pia, title, audience, "announcement", classification, pinned);
            await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
            return id;
        }
        var first = await LiveAsync("First");
        var pinned = await LiveAsync("Pinned", pinned: true);
        var third = await LiveAsync("Third");
        var ksaOnly = await LiveAsync("KSA office only", new { anyOf = new[] { new { orgUnits = new[] { new { id = o.Ksa, subtree = false } } } } });
        var confidential = await LiveAsync("Board only", classification: "confidential");

        var rana = o.T["rana"].Client;
        var page1 = await OkAsync(rana.GetAsync("/api/v1/content/feed?limit=2"));
        var ids1 = page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal([pinned, third], ids1);
        var next = page1.GetProperty("next").GetString()!;
        var page2 = await OkAsync(rana.GetAsync($"/api/v1/content/feed?limit=2&cursor={next}"));
        Assert.Equal([first], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("next").ValueKind);

        // Rana is in Riyadh, below KSA: a KSA target without its subtree does not reach her, but reaches KSA's own staff.
        Assert.DoesNotContain(ksaOnly, await FeedAsync(rana));
        Assert.Contains(ksaOnly, await FeedAsync(o.T["ksa_staff"].Client));
        Assert.DoesNotContain(confidential, await FeedAsync(rana));
        Assert.Contains(confidential, await FeedAsync(pia));
        Assert.Empty(await FeedAsync(rana, "?type=page"));

        Assert.Equal(HttpStatusCode.BadRequest, (await rana.GetAsync("/api/v1/content/feed?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rana.GetAsync("/api/v1/content/feed?type=blog")).StatusCode);
    }

    [Fact]
    public async Task Routes_are_validated_and_reviews_of_another_tenant_are_not_found()
    {
        var a = await FreshAsync("kim");
        var b = await FreshAsync("kim");
        var admin = a.T["admin"].Client;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/content/sites/{a.Site}",
            new { routes = new Dictionary<string, object> { ["news"] = new { approval = true, approvers = new[] { Guid.NewGuid() } } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/content/sites/{a.Site}",
            new { routes = new Dictionary<string, object> { ["blog"] = new { approval = true } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/content/sites/{a.Site}",
            new { routes = new Dictionary<string, object> { ["news"] = new { approval = true, approvers = new[] { b.T["kim"].Id } } } })).StatusCode);

        var id = await CreateAsync(a.T["layla"].Client, "Tenant A review");
        await OkAsync(a.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/submit", new { }));
        var task = Assert.Single(await InboxAsync(a.T["kim"].Client)).GetProperty("taskId").GetGuid();

        Assert.Empty(await InboxAsync(b.T["kim"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(b.T["kim"].Client, task, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["admin"].Client.GetAsync($"{Items}/{id}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/submit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["layla"].Client.PostAsJsonAsync($"{Items}/{id}/withdraw", new { })).StatusCode);
        Assert.DoesNotContain(id, await FeedAsync(b.T["admin"].Client));
        Assert.Equal("in_review", Status(await OkAsync(a.T["layla"].Client.GetAsync($"{Items}/{id}"))));
    }
}
