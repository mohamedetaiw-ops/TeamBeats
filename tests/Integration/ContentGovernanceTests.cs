using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 slice 4 (US3, US4, FR-008, FR-009): the owner's review task with confirm, edit and retire in the inbox
/// and on the item; overdue notices and the "under review" label; items handed to the backup owner when the owner
/// leaves; the health counts; history with compare and restore, hidden from readers. Tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentGovernanceTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("cg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.CreateUnitAsync(t, "Holding", null, "organization");
        foreach (var name in new[] { "pia", "ed", "olga", "bo", "mona", "reader" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["olga"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        return t;
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static object Variants(string title, string body) => new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } };

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

    /// <summary>A live page owned by <paramref name="owner"/> whose review is due in <paramref name="dueInDays"/>.</summary>
    private async Task<Guid> PageAsync(TestTenant t, string owner, int dueInDays, string? backup = null, string title = "Leave policy")
    {
        var response = await t["pia"].Client.PostAsJsonAsync(Items, new
        {
            type = "page", variants = Variants(title, "Staff get 30 days of leave."), ownerId = t[owner].Id, backupOwnerId = backup is null ? (Guid?)null : t[backup].Id,
            reviewDueOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(dueInDays)),
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["pia"].Client.PostAsJsonAsync($"{Items}/{id}/publish", new { })).StatusCode);
        return id;
    }

    private Task<int> RunScheduleAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) => (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().ToArray();

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    private static async Task<JsonElement> ItemAsync(TestTenant t, Guid id) => await OkAsync(t["pia"].Client.GetAsync($"{Items}/{id}"));

    [Fact]
    public async Task The_owner_gets_a_review_task_thirty_days_ahead_and_confirming_sets_the_next_date()
    {
        var t = await FreshAsync();
        var soon = await PageAsync(t, "olga", 10);
        var later = await PageAsync(t, "olga", 90, title: "Travel policy");
        Assert.Equal(1, await RunScheduleAsync(t, DateTimeOffset.UtcNow));
        Assert.Equal(0, await RunScheduleAsync(t, DateTimeOffset.UtcNow)); // Once per window.
        Assert.Equal(1, await world.Database.OutboxCountAsync("ContentReviewDue", t.Id));
        await DispatchAsync();

        var olga = t["olga"].Client;
        var task = Assert.Single(await InboxAsync(olga));
        Assert.Equal("content_owner_review", task.GetProperty("source").GetString());
        Assert.Equal("Leave policy", task.GetProperty("title").GetProperty("en").GetString());
        Assert.Contains(await NotificationLinksAsync(olga), l => l.StartsWith(ContentTaskSource.Link(soon), StringComparison.Ordinal));
        Assert.True((await ItemAsync(t, soon)).GetProperty("ownerReviewOpen").GetBoolean());
        Assert.False((await ItemAsync(t, later)).GetProperty("ownerReviewOpen").GetBoolean());

        // Only the owner acts on it.
        Assert.Equal(HttpStatusCode.NotFound, (await t["ed"].Client.PostAsJsonAsync($"{Items}/{soon}/owner-review", new { action = "confirm" })).StatusCode);
        var confirmed = await OkAsync(olga.PostAsJsonAsync($"{Items}/{soon}/owner-review", new { action = "confirm" }));
        Assert.Equal("published", confirmed.GetProperty("status").GetString());
        Assert.InRange(confirmed.GetProperty("reviewDueAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMonths(12).AddDays(-1), DateTimeOffset.UtcNow.AddMonths(12).AddDays(1));
        Assert.Empty(await InboxAsync(olga));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentReviewed, soon.ToString());
        Assert.Equal("conflict.content.no_review", await DetailKeyAsync(await olga.PostAsJsonAsync($"{Items}/{soon}/owner-review", new { action = "confirm" }), HttpStatusCode.Conflict));
        Assert.Contains("validation.content.review_action", await (await olga.PostAsJsonAsync($"{Items}/{later}/owner-review", new { action = "delete" })).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Edit_from_the_inbox_starts_a_draft_and_retire_goes_to_a_publisher()
    {
        var t = await FreshAsync();
        var edited = await PageAsync(t, "olga", 5);
        var retired = await PageAsync(t, "olga", 5, title: "Old canteen menu");
        await RunScheduleAsync(t, DateTimeOffset.UtcNow);
        await DispatchAsync();
        var olga = t["olga"].Client;
        var tasks = await InboxAsync(olga);
        Assert.Equal(2, tasks.Length);
        Guid TaskOf(Guid item) => tasks.Single(x => x.GetProperty("link").GetString()!.Contains(item.ToString(), StringComparison.Ordinal)).GetProperty("taskId").GetGuid();

        // Return in the inbox means edit: the live version stays and a draft copy waits.
        Assert.Equal(HttpStatusCode.OK, (await olga.PostAsJsonAsync($"/api/v1/tasks/{TaskOf(edited)}/decision", new { outcome = "return" })).StatusCode);
        var item = await ItemAsync(t, edited);
        Assert.Equal("published", item.GetProperty("status").GetString());
        Assert.Equal(2, item.GetProperty("draftVersion").GetInt32());
        var versions = (await OkAsync(t["pia"].Client.GetAsync($"{Items}/{edited}/versions"))).EnumerateArray().ToArray();
        Assert.Equal(1, versions[0].GetProperty("restoredFrom").GetInt32());

        // Olga is not a publisher: retiring asks the site's publishers, then archives.
        var result = await OkAsync(olga.PostAsJsonAsync($"{Items}/{retired}/owner-review", new { action = "retire" }));
        Assert.True(result.GetProperty("retirePending").GetBoolean());
        Assert.Equal("published", result.GetProperty("status").GetString());
        var retire = Assert.Single(await InboxAsync(t["pia"].Client), x => x.GetProperty("source").GetString() == "content_retire");
        Assert.DoesNotContain(await InboxAsync(olga), x => x.GetProperty("source").GetString() == "content_retire");
        Assert.Equal(HttpStatusCode.OK, (await t["pia"].Client.PostAsJsonAsync($"/api/v1/tasks/{retire.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" })).StatusCode);
        Assert.Equal("archived", (await ItemAsync(t, retired)).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentArchived, retired.ToString());
    }

    [Fact]
    public async Task An_overdue_item_shows_under_review_and_tells_the_owners_manager_and_the_administrators()
    {
        var t = await FreshAsync();
        var id = await PageAsync(t, "olga", 2);
        Assert.False((await OkAsync(t["reader"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("underReview").GetBoolean());
        await RunScheduleAsync(t, DateTimeOffset.UtcNow);
        await world.Database.OwnerExecInTenantAsync(t.Id, "update content.content_item set review_due_at = now() - interval '1 day' where id = @id", ("id", id));
        Assert.Equal(1, await RunScheduleAsync(t, DateTimeOffset.UtcNow));
        Assert.Equal(0, await RunScheduleAsync(t, DateTimeOffset.UtcNow));
        await DispatchAsync();
        Assert.Contains(ContentTaskSource.Link(id), await NotificationLinksAsync(t["mona"].Client));
        Assert.Contains(ContentTaskSource.Link(id), await NotificationLinksAsync(t["admin"].Client));
        Assert.True((await OkAsync(t["reader"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("underReview").GetBoolean());

        var health = Assert.Single((await OkAsync(t["admin"].Client.GetAsync("/api/v1/content/health"))).EnumerateArray());
        Assert.Equal(1, health.GetProperty("overdueForReview").GetInt32());
        var site = health.GetProperty("siteId").GetGuid();
        Assert.Equal([id], (await OkAsync(t["admin"].Client.GetAsync($"/api/v1/content/health/items?site={site}&kind=overdue"))).EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["pia"].Client.GetAsync("/api/v1/content/health")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.GetAsync($"/api/v1/content/health/items?site={site}&kind=stale")).StatusCode);
    }

    [Fact]
    public async Task When_an_owner_leaves_their_items_go_to_the_backup_owner_or_become_ownerless()
    {
        var t = await FreshAsync();
        var backed = await PageAsync(t, "olga", 5, backup: "bo");
        var alone = await PageAsync(t, "olga", 200, title: "Parking rules");
        await RunScheduleAsync(t, DateTimeOffset.UtcNow);
        await DispatchAsync();
        Assert.Single(await InboxAsync(t["olga"].Client));

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserDeprovisioned(t["olga"].Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        await DispatchAsync();

        var moved = await ItemAsync(t, backed);
        Assert.Equal(t["bo"].Id, moved.GetProperty("owner").GetProperty("id").GetGuid());
        Assert.False(moved.GetProperty("ownerReviewOpen").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await ItemAsync(t, alone)).GetProperty("owner").ValueKind);
        Assert.Empty(await InboxAsync(t["olga"].Client));
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.ContentOwnerReassigned)));
        Assert.Contains("/content/health", await NotificationLinksAsync(t["admin"].Client));
        Assert.Equal(1, (await OkAsync(t["admin"].Client.GetAsync("/api/v1/content/health"))).EnumerateArray().Single().GetProperty("withoutActiveOwner").GetInt32());

        // Bo's review opens at the next run.
        await RunScheduleAsync(t, DateTimeOffset.UtcNow);
        await DispatchAsync();
        Assert.Single(await InboxAsync(t["bo"].Client));
    }

    [Fact]
    public async Task History_compares_versions_and_restore_makes_a_new_draft_while_readers_see_no_history()
    {
        var t = await FreshAsync();
        var pia = t["pia"].Client;
        var id = await PageAsync(t, "olga", 200);
        Assert.Equal(HttpStatusCode.OK, (await pia.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Leave policy", "Staff get 25 days of leave."), changeNote = "Cut" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pia.PostAsJsonAsync($"{Items}/{id}/publish", new { })).StatusCode);

        var compare = await OkAsync(pia.GetAsync($"{Items}/{id}/compare?from=1&to=2"));
        var body = compare.GetProperty("body").EnumerateArray().Select(s => (s.GetProperty("op").GetString(), s.GetProperty("text").GetString())).ToArray();
        Assert.Contains(("removed", "30"), body);
        Assert.Contains(("added", "25"), body);
        Assert.Equal("Staff get 30 days of leave.", string.Concat(body.Where(s => s.Item1 != "added").Select(s => s.Item2)));
        Assert.Equal("equal", Assert.Single(compare.GetProperty("title").EnumerateArray()).GetProperty("op").GetString());
        var detail = await OkAsync(pia.GetAsync($"{Items}/{id}/versions/1"));
        Assert.Equal("Leave policy", detail.GetProperty("variants").GetProperty("en").GetProperty("title").GetString());

        var restored = await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/versions/1/restore", new { changeNote = "Back to 30" }));
        Assert.Equal(3, restored.GetProperty("draftVersion").GetInt32());
        Assert.Equal(2, restored.GetProperty("publishedVersion").GetInt32());
        Assert.Equal("Leave policy", restored.GetProperty("variants").GetProperty("en").GetProperty("title").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentVersionRestored, id.ToString());
        Assert.Equal("conflict.content.draft_exists", await DetailKeyAsync(await pia.PostAsJsonAsync($"{Items}/{id}/versions/2/restore", new { }), HttpStatusCode.Conflict));
        // History is never rewritten: three versions, the first two unchanged.
        Assert.Equal(3, (await OkAsync(pia.GetAsync($"{Items}/{id}/versions"))).GetArrayLength());

        var reader = t["reader"].Client;
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"{Items}/{id}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"{Items}/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"{Items}/{id}/versions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"{Items}/{id}/compare?from=1&to=2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.PostAsJsonAsync($"{Items}/{id}/versions/1/restore", new { })).StatusCode);
    }

    [Fact]
    public async Task Text_diff_marks_words_and_rejoins_to_the_original_texts()
    {
        var segments = TextDiff.Words("Fire exit on the left", "Fire exit on the right side");
        Assert.Equal("Fire exit on the left", string.Concat(segments.Where(s => s.Op != DiffSegment.Added).Select(s => s.Text)));
        Assert.Equal("Fire exit on the right side", string.Concat(segments.Where(s => s.Op != DiffSegment.Removed).Select(s => s.Text)));
        Assert.Contains(segments, s => s is { Op: DiffSegment.Removed, Text: "left" });
        Assert.Empty(TextDiff.Words(null, ""));
        var big = string.Join(' ', Enumerable.Range(0, 5000).Select(i => "w" + i));
        Assert.Equal(big + " end", string.Concat(TextDiff.Words(big, big + " end").Where(s => s.Op != DiffSegment.Removed).Select(s => s.Text)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Another_tenant_cannot_review_compare_restore_or_list_health()
    {
        var t = await FreshAsync();
        var id = await PageAsync(t, "olga", 5);
        await RunScheduleAsync(t, DateTimeOffset.UtcNow);
        var site = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/content/health"))).EnumerateArray().Single().GetProperty("siteId").GetGuid();

        var other = (await FreshAsync())["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Items}/{id}/owner-review", new { action = "confirm" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Items}/{id}/compare?from=1&to=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Items}/{id}/versions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Items}/{id}/versions/1/restore", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/content/health/items?site={site}&kind=overdue")).StatusCode);
    }
}
