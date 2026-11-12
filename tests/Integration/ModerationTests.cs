using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T005 (US8, FR-018 to FR-021): reports from three different people hide an item until a moderator decides;
/// a word on the tenant's keyword filter holds a post or comment for review and the author is told; removal records a
/// reason the author reads, is audited and takes the post out of search; a muted member reads but does not post or
/// comment until the mute ends. Moderators act only in their communities and tenants. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ModerationTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en, ["ar"] = "مجتمع " + en };

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> call) => (await call).StatusCode;

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    private async Task<(TestTenant T, Guid Community)> SetupAsync(string kind = "open")
    {
        var t = await world.CreateTenantAsync("mod-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        foreach (var name in new[] { "laila", "omar", "ahmed", "mona", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = Names("Town square"), kind }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        foreach (var name in new[] { "laila", "omar", "ahmed", "mona", "kofi" })
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id }));
        }
        return (t, community);
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, Guid community, string text, params Guid[] mentions) =>
        (await OkAsync(client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body(text), mentions }), HttpStatusCode.Created)).GetProperty("post");

    private static async Task<Guid[]> FeedAsync(HttpClient client, Guid community) =>
        (await OkAsync(client.GetAsync($"{Api}/communities/{community}/posts"))).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray();

    private static async Task<JsonElement[]> QueueAsync(HttpClient client, Guid? community = null) =>
        (await OkAsync(client.GetAsync(community is null ? $"{Api}/moderation" : $"{Api}/moderation?communityId={community}"))).EnumerateArray().ToArray();

    private static Task<HttpResponseMessage> ReportAsync(HttpClient client, string type, Guid id, string reason = "harassment", string? note = null) =>
        client.PostAsJsonAsync($"{Api}/{type}s/{id}:report", new { reason, note });

    private async Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        await world.Database.TenantScalarAsync<long>(t.Id, "select coalesce(sum(group_count), 0)::bigint from notifications.notification where user_id = @u and template_key = @k",
            ("u", t[user].Id), ("k", template));

    private static async Task<Guid[]> SearchAsync(HttpClient client, string q) =>
        (await OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}&kind=discussion"))).GetProperty("results").EnumerateArray()
        .Select(r => r.GetProperty("itemId").GetGuid()).ToArray();

    [Fact]
    public async Task Three_reports_from_different_people_hide_a_post_until_a_moderator_decides()
    {
        var (t, community) = await SetupAsync();
        var post = (await PostAsync(t["laila"].Client, community, "Parking rules are a joke")).GetProperty("id").GetGuid();
        Assert.True((await OkAsync(t["omar"].Client.GetAsync($"{Api}/posts/{post}"))).GetProperty("canReport").GetBoolean());

        // The author cannot report their own post; an unknown reason is refused; reporting twice counts once.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(ReportAsync(t["laila"].Client, "post", post)));
        Assert.Contains("validation.engagement.report_reason", await ErrorKeysAsync(await ReportAsync(t["omar"].Client, "post", post, "boring")));
        await OkAsync(ReportAsync(t["omar"].Client, "post", post, "harassment", "Targets the facilities team"), HttpStatusCode.NoContent);
        await OkAsync(ReportAsync(t["omar"].Client, "post", post), HttpStatusCode.NoContent);
        await OkAsync(ReportAsync(t["ahmed"].Client, "post", post, "inappropriate"), HttpStatusCode.NoContent);
        Assert.Contains(post, await FeedAsync(t["mona"].Client, community));

        // Scenario 1: it is in the moderators' queue with the reasons, and not in anyone else's.
        var item = Assert.Single(await QueueAsync(t["admin"].Client, community));
        Assert.Equal(("post", post, "published"), (item.GetProperty("type").GetString(), item.GetProperty("id").GetGuid(), item.GetProperty("status").GetString()));
        Assert.Equal(["harassment:1", "inappropriate:1"], item.GetProperty("reports").EnumerateArray().Select(r => $"{r.GetProperty("reason").GetString()}:{r.GetProperty("count").GetInt32()}").ToArray());
        Assert.Equal(["Targets the facilities team"], item.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToArray());
        Assert.DoesNotContain("reporter", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await QueueAsync(t["mona"].Client));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["mona"].Client.PostAsync($"{Api}/moderation/post/{post}:approve", null)));

        // The third different person hides it: gone from the feed for readers, still visible to its author as hidden.
        await OkAsync(ReportAsync(t["mona"].Client, "post", post, "spam"), HttpStatusCode.NoContent);
        Assert.DoesNotContain(post, await FeedAsync(t["kofi"].Client, community));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"{Api}/posts/{post}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(ReportAsync(t["kofi"].Client, "post", post)));
        var own = await OkAsync(t["laila"].Client.GetAsync($"{Api}/posts/{post}"));
        Assert.Equal(("hidden", "reports"), (own.GetProperty("status").GetString(), own.GetProperty("heldReason").GetString()));
        Assert.Contains(post, await FeedAsync(t["laila"].Client, community));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentHidden, post.ToString());
        Assert.Equal("hidden", Assert.Single(await QueueAsync(t["admin"].Client)).GetProperty("status").GetString());

        // A moderator keeps it: it is back for everyone and its reports are closed, so one new report does not hide it again.
        await OkAsync(t["admin"].Client.PostAsync($"{Api}/moderation/post/{post}:approve", null), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentApproved, post.ToString());
        Assert.Contains(post, await FeedAsync(t["kofi"].Client, community));
        Assert.Empty(await QueueAsync(t["admin"].Client));
        await OkAsync(ReportAsync(t["kofi"].Client, "post", post), HttpStatusCode.NoContent);
        Assert.Contains(post, await FeedAsync(t["mona"].Client, community));

        // Another tenant's moderator reaches nothing.
        var other = await world.CreateTenantAsync("mod-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Empty(await QueueAsync(other["admin"].Client));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["admin"].Client.PostAsJsonAsync($"{Api}/moderation/post/{post}:remove", new { reason = "Off topic" })));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(ReportAsync(other["admin"].Client, "post", post)));
    }

    [Fact]
    public async Task Reported_comments_hide_on_the_third_report_and_the_comment_count_follows()
    {
        var (t, community) = await SetupAsync();
        var post = (await PostAsync(t["admin"].Client, community, "Lunch and learn on Thursday")).GetProperty("id").GetGuid();
        var comment = (await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/posts/{post}/comments", new { body = "Who even goes to these" }), HttpStatusCode.Created))
            .GetProperty("comment").GetProperty("id").GetGuid();
        await OkAsync(t["kofi"].Client.PostAsJsonAsync($"{Api}/posts/{post}/comments", new { body = "I do", parentId = comment }), HttpStatusCode.Created);
        Assert.Equal(2, (await OkAsync(t["omar"].Client.GetAsync($"{Api}/posts/{post}"))).GetProperty("commentCount").GetInt32());

        foreach (var name in new[] { "omar", "ahmed", "mona" })
        {
            await OkAsync(ReportAsync(t[name].Client, "comment", comment), HttpStatusCode.NoContent);
        }
        // The comment and the reply under it leave the thread for readers; the author still sees hers as hidden.
        Assert.Empty((await OkAsync(t["omar"].Client.GetAsync($"{Api}/posts/{post}/comments"))).EnumerateArray());
        Assert.Equal(0, (await OkAsync(t["omar"].Client.GetAsync($"{Api}/posts/{post}"))).GetProperty("commentCount").GetInt32());
        Assert.Equal("hidden", Assert.Single((await OkAsync(t["laila"].Client.GetAsync($"{Api}/posts/{post}/comments"))).EnumerateArray()).GetProperty("status").GetString());
        var item = Assert.Single(await QueueAsync(t["admin"].Client));
        Assert.Equal(("comment", post), (item.GetProperty("type").GetString(), item.GetProperty("postId").GetGuid()));

        // Removing it keeps it gone; the author reads why on the comment and is told.
        Assert.Contains("validation.engagement.removal_reason", await ErrorKeysAsync(await t["admin"].Client.PostAsJsonAsync($"{Api}/moderation/comment/{comment}:remove", new { reason = " " })));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/moderation/comment/{comment}:remove", new { reason = "Please keep comments kind." }), HttpStatusCode.NoContent);
        var mine = Assert.Single((await OkAsync(t["laila"].Client.GetAsync($"{Api}/posts/{post}/comments"))).EnumerateArray());
        Assert.Equal(("removed", "Please keep comments kind."), (mine.GetProperty("status").GetString(), mine.GetProperty("removedReason").GetString()));
        Assert.Equal(1L, await NotificationsAsync(t, "laila", "engagement.content.removed"));
        Assert.Equal(0, (await OkAsync(t["omar"].Client.GetAsync($"{Api}/posts/{post}"))).GetProperty("commentCount").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentRemoved, comment.ToString());
        Assert.Empty(await QueueAsync(t["admin"].Client));
    }

    [Fact]
    public async Task A_filtered_word_holds_the_post_for_review_and_mentions_wait_until_it_is_published()
    {
        var (t, community) = await SetupAsync();
        var admin = t["admin"].Client;

        // The filter is versioned configuration that only engagement administrators read and change, with If-Match.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.GetAsync($"{Api}/settings/keywords")));
        var current = await admin.GetAsync($"{Api}/settings/keywords");
        var tag = current.Headers.ETag!.Tag;
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/keywords") { Content = JsonContent.Create(new { keywords = new[] { "x" } }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"k99\"");
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(admin.SendAsync(stale)));
        var bad = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/keywords") { Content = JsonContent.Create(new { keywords = new[] { "x" } }) };
        bad.Headers.TryAddWithoutValidation("If-Match", tag);
        Assert.Contains("validation.engagement.keyword", await ErrorKeysAsync(await admin.SendAsync(bad)));
        var put = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/keywords") { Content = JsonContent.Create(new { keywords = new[] { " Scam ", "insider tip", "scam" } }) };
        put.Headers.TryAddWithoutValidation("If-Match", tag);
        var saved = await OkAsync(admin.SendAsync(put));
        Assert.Equal(["insider tip", "scam"], saved.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementKeywordsChanged);

        // A whole word only: "scampi" is fine.
        var lunch = await PostAsync(t["laila"].Client, community, "Scampi for lunch today");
        Assert.Equal("published", lunch.GetProperty("status").GetString());

        // Scenario 2: the post is held and the author is told so; Omar's mention waits.
        var held = await PostAsync(t["laila"].Client, community, "Got an INSIDER TIP about the new office", t["omar"].Id);
        var heldId = held.GetProperty("id").GetGuid();
        Assert.Equal(("held", "keyword"), (held.GetProperty("status").GetString(), held.GetProperty("heldReason").GetString()));
        Assert.Contains(heldId, await FeedAsync(t["laila"].Client, community));
        Assert.DoesNotContain(heldId, await FeedAsync(t["omar"].Client, community));
        Assert.Equal(0L, await NotificationsAsync(t, "omar", "engagement.mention"));
        await world.DispatchTenantAsync(t.Id);
        Assert.Empty(await SearchAsync(t["omar"].Client, "office"));
        Assert.Equal("keyword", Assert.Single(await QueueAsync(admin)).GetProperty("heldReason").GetString());

        // A comment with the word is held too.
        var comment = (await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/posts/{lunch.GetProperty("id").GetGuid()}/comments", new { body = "Sounds like a scam" }), HttpStatusCode.Created))
            .GetProperty("comment");
        Assert.Equal("held", comment.GetProperty("status").GetString());
        Assert.Equal(0, (await OkAsync(t["mona"].Client.GetAsync($"{Api}/posts/{lunch.GetProperty("id").GetGuid()}"))).GetProperty("commentCount").GetInt32());

        // Once a moderator publishes it, Omar is told and the post is found.
        await OkAsync(admin.PostAsync($"{Api}/moderation/post/{heldId}:approve", null), HttpStatusCode.NoContent);
        Assert.Contains(heldId, await FeedAsync(t["omar"].Client, community));
        Assert.Equal(1L, await NotificationsAsync(t, "omar", "engagement.mention"));
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal([heldId], await SearchAsync(t["omar"].Client, "office"));
        await OkAsync(admin.PostAsync($"{Api}/moderation/comment/{comment.GetProperty("id").GetGuid()}:approve", null), HttpStatusCode.NoContent);
        Assert.Equal(1, (await OkAsync(t["mona"].Client.GetAsync($"{Api}/posts/{lunch.GetProperty("id").GetGuid()}"))).GetProperty("commentCount").GetInt32());

        // Editing a filtered word into a published post takes it back to the queue.
        var edited = await OkAsync(t["laila"].Client.PutAsJsonAsync($"{Api}/posts/{lunch.GetProperty("id").GetGuid()}", new { body = Body("Scampi lunch, no scam") }));
        Assert.Equal("held", edited.GetProperty("post").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Removal_records_a_reason_the_author_reads_is_audited_and_leaves_search()
    {
        var (t, community) = await SetupAsync();
        var post = (await PostAsync(t["laila"].Client, community, "Selling my car, message me for the zebra price")).GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal([post], await SearchAsync(t["omar"].Client, "zebra"));

        // A member who is not a moderator cannot remove; a community moderator can, and only in this community.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/moderation/post/{post}:remove", new { reason = "Off topic" })));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t["omar"].Id, role = "moderator" }));
        var elsewhere = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = Names("Elsewhere"), kind = "open" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var other = (await PostAsync(t["admin"].Client, elsewhere, "Unrelated news")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/moderation/post/{other}:remove", new { reason = "Off topic" })));

        // Scenario 3.
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/moderation/post/{post}:remove", new { reason = "Sales posts go in the marketplace community." }), HttpStatusCode.NoContent);
        var own = await OkAsync(t["laila"].Client.GetAsync($"{Api}/posts/{post}"));
        Assert.Equal(("removed", "Sales posts go in the marketplace community."), (own.GetProperty("status").GetString(), own.GetProperty("removedReason").GetString()));
        Assert.False(own.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["mona"].Client.GetAsync($"{Api}/posts/{post}")));
        Assert.DoesNotContain(post, await FeedAsync(t["mona"].Client, community));
        Assert.Equal(1L, await NotificationsAsync(t, "laila", "engagement.content.removed"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentRemoved, post.ToString());
        await world.DispatchTenantAsync(t.Id);
        Assert.Empty(await SearchAsync(t["mona"].Client, "zebra"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.unit where item_id = @i", ("i", post)));

        // The author cannot delete it to hide the decision, and a removed post takes no reactions or comments.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.DeleteAsync($"{Api}/posts/{post}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/posts/{post}/comments", new { body = "Why" })));
    }

    [Fact]
    public async Task A_muted_member_reads_but_cannot_post_or_comment_until_the_mute_ends()
    {
        var (t, community) = await SetupAsync();
        var admin = t["admin"].Client;
        var post = (await PostAsync(admin, community, "Office move schedule")).GetProperty("id").GetGuid();
        var until = DateTimeOffset.UtcNow.AddDays(2);

        // Only moderators mute; owners and oneself cannot be muted; the end must be within a year.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.PutAsJsonAsync($"{Api}/communities/{community}/mutes", new { userId = t["kofi"].Id, until })));
        Assert.Contains("validation.engagement.mute_self", await ErrorKeysAsync(await admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes",
            new { userId = t["admin"].Id, until })));
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t["omar"].Id, role = "owner" }));
        // The directory offers the queue to anyone who moderates a community; only administrators get the keyword filter.
        var omarRights = await OkAsync(t["omar"].Client.GetAsync($"{Api}/rights"));
        Assert.True(omarRights.GetProperty("canModerate").GetBoolean());
        Assert.False(omarRights.GetProperty("canManageKeywords").GetBoolean());
        Assert.False((await OkAsync(t["laila"].Client.GetAsync($"{Api}/rights"))).GetProperty("canModerate").GetBoolean());
        Assert.True((await OkAsync(admin.GetAsync($"{Api}/rights"))).GetProperty("canManageKeywords").GetBoolean());
        Assert.Contains("validation.engagement.mute_owner", await ErrorKeysAsync(await admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes",
            new { userId = t["omar"].Id, until })));
        Assert.Contains("validation.engagement.mute_until", await ErrorKeysAsync(await admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes",
            new { userId = t["kofi"].Id, until = DateTimeOffset.UtcNow.AddMinutes(-1) })));
        Assert.Contains("validation.engagement.mute_until", await ErrorKeysAsync(await admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes",
            new { userId = t["kofi"].Id, until = DateTimeOffset.UtcNow.AddDays(400) })));

        var mutes = await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes", new { userId = t["kofi"].Id, until, reason = "Cooling off" }));
        Assert.Equal(t["kofi"].Id, Assert.Single(mutes.EnumerateArray()).GetProperty("userId").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementMemberMuted, community.ToString());
        Assert.Equal(1L, await NotificationsAsync(t, "kofi", "engagement.member.muted"));

        // Scenario 4: Kofi reads and reacts, but neither posts nor comments, and his page says until when.
        var kofi = t["kofi"].Client;
        var rights = (await OkAsync(kofi.GetAsync($"{Api}/communities/{community}"))).GetProperty("rights");
        Assert.False(rights.GetProperty("canPost").GetBoolean());
        Assert.Equal(JsonValueKind.String, rights.GetProperty("mutedUntil").ValueKind);
        Assert.Contains(post, await FeedAsync(kofi, community));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(kofi.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Let me speak") })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(kofi.PostAsJsonAsync($"{Api}/posts/{post}/comments", new { body = "Me too" })));
        await OkAsync(kofi.PutAsJsonAsync($"{Api}/posts/{post}/reaction", new { kind = "like" }));

        // The mute is per community: in another community Kofi posts freely.
        var elsewhere = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Elsewhere"), kind = "open" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(kofi.PostAsync($"{Api}/communities/{elsewhere}:join", null));
        await PostAsync(kofi, elsewhere, "Hello here");

        // When the mute ends he posts again; a moderator can also end it early.
        await world.Database.TenantScalarAsync<long>(t.Id,
            "with u as (update engagement.mute set until = now() - interval '1 minute' where user_id = @u returning 1) select count(*) from u", ("u", t["kofi"].Id));
        await PostAsync(kofi, community, "Back again");
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes", new { userId = t["kofi"].Id, until }));
        Assert.Empty((await OkAsync(admin.DeleteAsync($"{Api}/communities/{community}/mutes/{t["kofi"].Id}"))).EnumerateArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementMemberUnmuted, community.ToString());
        await PostAsync(kofi, community, "Thanks");
    }
}
