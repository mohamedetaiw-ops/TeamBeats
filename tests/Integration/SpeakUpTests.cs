using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T007 for Speak Up (US2, FR-005 to FR-007, FR-025): every Speak Up post passes the review queue; an anonymous
/// post is stored with nothing that points to its author, not in the post, the audit trail or the outbox; a moderator
/// publishes it (marked when edited), answers it or declines it, and the author reads the decision once through their
/// link; the anonymous limit counts by a keyed hash whose key changes daily and whose old keys are deleted. Only
/// engagement administrators allow anonymity, and the capability is off in Workplace. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpeakUpTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en, ["ar"] = "قناة " + en };

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    private async Task<(TestTenant T, Guid Community, Guid Channel)> SetupAsync(bool anonymous = true, string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("su-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "laila", "omar", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var admin = t["admin"].Client;
        var community = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Safety"), kind = "open" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        foreach (var name in new[] { "laila", "omar", "kofi" })
        {
            await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id }));
        }
        if (package == "workplace")
        {
            return (t, community, Guid.Empty);
        }
        var view = await OkAsync(admin.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "speak_up", names = Names("Speak up"), kind = "speak_up", allowAnonymous = anonymous }), HttpStatusCode.Created);
        var channel = view.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "speak_up");
        Assert.Equal("speak_up", channel.GetProperty("kind").GetString());
        Assert.Equal(anonymous, channel.GetProperty("allowAnonymous").GetBoolean());
        return (t, community, channel.GetProperty("id").GetGuid());
    }

    private static async Task<Guid[]> FeedAsync(HttpClient client, Guid community) =>
        (await OkAsync(client.GetAsync($"{Api}/communities/{community}/posts"))).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray();

    private static async Task<JsonElement[]> QueueAsync(HttpClient client) => (await OkAsync(client.GetAsync($"{Api}/moderation"))).EnumerateArray().ToArray();

    private static async Task<string> SubmitAsync(HttpClient client, Guid community, Guid channel, string text) =>
        (await OkAsync(client.PostAsJsonAsync($"{Api}/communities/{community}/anonymous-posts", new { channelId = channel, body = Body(text) })))
        .GetProperty("replyToken").GetString()!;

    private static Task<HttpResponseMessage> ReplyAsync(HttpClient client, string token) => client.PostAsJsonAsync($"{Api}/speak-up/reply", new { token });

    [Fact]
    public async Task Named_Speak_Up_posts_wait_for_a_moderator_and_edits_go_back_to_review()
    {
        var (t, community, channel) = await SetupAsync(anonymous: false);
        var laila = t["laila"].Client;
        var post = (await OkAsync(laila.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { channelId = channel, body = Body("The fire exit on floor 3 is blocked") }),
            HttpStatusCode.Created)).GetProperty("post");
        Assert.Equal("held", post.GetProperty("status").GetString());
        Assert.Equal("speak_up", post.GetProperty("heldReason").GetString());
        var id = post.GetProperty("id").GetGuid();
        Assert.DoesNotContain(id, await FeedAsync(t["omar"].Client, community));
        Assert.Contains(id, await FeedAsync(laila, community));

        var item = Assert.Single(await QueueAsync(t["admin"].Client));
        Assert.Equal("post", item.GetProperty("type").GetString());
        Assert.Equal(t["laila"].Id, item.GetProperty("author").GetProperty("id").GetGuid());
        await OkAsync(t["admin"].Client.PostAsync($"{Api}/moderation/post/{id}:approve", null), HttpStatusCode.NoContent);
        Assert.Contains(id, await FeedAsync(t["omar"].Client, community));

        // An edit in a Speak Up channel goes back to the queue.
        var edited = (await OkAsync(laila.PutAsJsonAsync($"{Api}/posts/{id}", new { body = Body("The fire exit on floor 3 is still blocked") }))).GetProperty("post");
        Assert.Equal("held", edited.GetProperty("status").GetString());
        Assert.DoesNotContain(id, await FeedAsync(t["omar"].Client, community));

        // Without anonymity allowed, an anonymous post is refused.
        var refused = await laila.PostAsJsonAsync($"{Api}/communities/{community}/anonymous-posts", new { channelId = channel, body = Body("Quietly") });
        Assert.Contains("validation.engagement.anonymous_channel", await ErrorKeysAsync(refused));
    }

    [Fact]
    public async Task An_anonymous_post_stores_nothing_that_points_to_its_author()
    {
        var (t, community, channel) = await SetupAsync();
        var omar = t["omar"];
        var auditBefore = await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where actor_id = @u", ("u", omar.Id));
        var outboxBefore = await world.Database.CountInTenantAsync(t.Id, "select count(*) from messaging.outbox where tenant_id = @t", ("t", t.Id));

        var token = await SubmitAsync(omar.Client, community, channel, "My manager shouts at the team");

        // The table has no user column and no time of day; the row holds nothing with Omar's id.
        var columns = await world.Database.CountInTenantAsync(t.Id,
            """
            select count(*) from information_schema.columns where table_schema = 'engagement' and table_name = 'anon_post'
              and (column_name like '%user%' or column_name like '%author%' or column_name like '%created%' or (data_type like 'timestamp%' and column_name <> 'decided_at'))
            """);
        Assert.Equal(0, columns);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_post where row_to_json(anon_post)::text like '%' || @u || '%'",
            ("u", omar.Id.ToString())));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_limit where hash like '%' || @u || '%'",
            ("u", omar.Id.ToString("N").ToUpperInvariant())));
        // No audit entry and no event come from posting.
        Assert.Equal(auditBefore, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where actor_id = @u", ("u", omar.Id)));
        Assert.Equal(outboxBefore, await world.Database.CountInTenantAsync(t.Id, "select count(*) from messaging.outbox where tenant_id = @t", ("t", t.Id)));

        // The queue shows it without an author; Omar's feed does not show it; the link says it waits.
        var item = Assert.Single(await QueueAsync(t["admin"].Client));
        Assert.Equal("anonymous", item.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("author").ValueKind);
        Assert.Equal("00:00:00", item.GetProperty("createdAt").GetDateTimeOffset().TimeOfDay.ToString());
        Assert.Empty(await FeedAsync(omar.Client, community));
        Assert.Equal("pending", (await OkAsync(ReplyAsync(omar.Client, token))).GetProperty("status").GetString());

        // Readers and the community's own members cannot act on it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.PostAsJsonAsync($"{Api}/moderation/anonymous/{item.GetProperty("id").GetGuid()}:publish", new { })).StatusCode);

        // Scenario 4: the moderator takes the name out before publishing; the post says so and has no author.
        var id = item.GetProperty("id").GetGuid();
        var published = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/moderation/anonymous/{id}:publish", new { body = Body("A manager shouts at the team") }));
        var postId = published.GetProperty("postId").GetGuid();
        var post = await OkAsync(t["kofi"].Client.GetAsync($"{Api}/posts/{postId}"));
        Assert.True(post.GetProperty("anonymous").GetBoolean());
        Assert.True(post.GetProperty("editedByModerator").GetBoolean());
        Assert.False(post.GetProperty("mine").GetBoolean());
        Assert.Equal(Guid.Empty, post.GetProperty("author").GetProperty("id").GetGuid());
        Assert.False((await OkAsync(omar.Client.GetAsync($"{Api}/posts/{postId}"))).GetProperty("mine").GetBoolean());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.post_follow where post_id = @p", ("p", postId)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_post where body is not null"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentApproved, id.ToString());

        // The link shows the decision once, then stops working.
        var reply = await OkAsync(ReplyAsync(omar.Client, token));
        Assert.Equal("published", reply.GetProperty("status").GetString());
        Assert.Equal(postId, reply.GetProperty("postId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await ReplyAsync(omar.Client, token)).StatusCode);

        // A reported anonymous post is removed with no author to tell.
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/moderation/post/{postId}:remove", new { reason = "Raised with HR directly" }), HttpStatusCode.NoContent);
        Assert.DoesNotContain(postId, await FeedAsync(t["kofi"].Client, community));
    }

    [Fact]
    public async Task A_moderator_answers_or_declines_and_the_author_reads_it_through_the_link()
    {
        var (t, community, channel) = await SetupAsync();
        var laila = t["laila"].Client;
        var admin = t["admin"].Client;
        var answerToken = await SubmitAsync(laila, community, channel, "Is the bonus policy changing?");
        var declineToken = await SubmitAsync(laila, community, channel, "Something unrelated to work");
        var items = await QueueAsync(admin);
        Assert.Equal(2, items.Length);
        var first = items.Single(i => i.GetProperty("text").GetString() == "Is the bonus policy changing?").GetProperty("id").GetGuid();
        var second = items.Single(i => i.GetProperty("text").GetString() == "Something unrelated to work").GetProperty("id").GetGuid();

        Assert.Contains("validation.engagement.anonymous_answer", await ErrorKeysAsync(await admin.PostAsJsonAsync($"{Api}/moderation/anonymous/{first}:answer", new { answer = " " })));
        await OkAsync(admin.PostAsJsonAsync($"{Api}/moderation/anonymous/{first}:answer", new { answer = "No change this year. HR will share any update." }), HttpStatusCode.NoContent);
        await OkAsync(admin.PostAsJsonAsync($"{Api}/moderation/anonymous/{second}:decline", new { reason = "Speak Up is for work concerns" }), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementSpeakUpAnswered, first.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementSpeakUpDeclined, second.ToString());
        Assert.Empty(await QueueAsync(admin));
        Assert.Empty(await FeedAsync(t["omar"].Client, community));
        // A decided item cannot be decided again.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Api}/moderation/anonymous/{first}:publish", new { })).StatusCode);

        var answered = await OkAsync(ReplyAsync(laila, answerToken));
        Assert.Equal("answered", answered.GetProperty("status").GetString());
        Assert.Equal("No change this year. HR will share any update.", answered.GetProperty("answer").GetString());
        var declined = await OkAsync(ReplyAsync(laila, declineToken));
        Assert.Equal("declined", declined.GetProperty("status").GetString());
        Assert.Equal("Speak Up is for work concerns", declined.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await ReplyAsync(laila, "not-a-token")).StatusCode);

        // Another tenant's moderator finds nothing.
        var (other, _, _) = await SetupAsync();
        var token = await SubmitAsync(laila, community, channel, "One more");
        var pending = Assert.Single(await QueueAsync(admin)).GetProperty("id").GetGuid();
        Assert.Empty(await QueueAsync(other["admin"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Api}/moderation/anonymous/{pending}:publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReplyAsync(other["laila"].Client, token)).StatusCode);
    }

    [Fact]
    public async Task The_anonymous_limit_counts_by_a_daily_keyed_hash_and_old_keys_are_deleted()
    {
        var (t, community, channel) = await SetupAsync();
        // Yesterday's key and counts are left over; the next post deletes them.
        await world.Database.OwnerExecInTenantAsync(t.Id,
            "insert into engagement.anon_key (tenant_id, day, key) values (@t, current_date - 1, '\\x00'::bytea)", ("t", t.Id));
        await world.Database.OwnerExecInTenantAsync(t.Id,
            "insert into engagement.anon_limit (tenant_id, day, hash, count) values (@t, current_date - 1, 'old', 3)", ("t", t.Id));

        for (var i = 0; i < 5; i++)
        {
            await SubmitAsync(t["laila"].Client, community, channel, $"Concern {i}");
        }
        var over = await t["laila"].Client.PostAsJsonAsync($"{Api}/communities/{community}/anonymous-posts", new { channelId = channel, body = Body("Concern 6") });
        Assert.Contains("validation.engagement.anonymous_limit", await ErrorKeysAsync(over));
        // Someone else still posts: the limit is per person.
        await SubmitAsync(t["omar"].Client, community, channel, "Mine");

        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_key"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_limit where day < current_date"));
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_limit"));
        // Six posts went in: five from Laila and one from Omar; the one over the limit was not kept.
        Assert.Equal(6, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.anon_post"));
    }

    [Fact]
    public async Task Only_engagement_administrators_allow_anonymity_and_Workplace_has_no_Speak_Up()
    {
        var (t, community, _) = await SetupAsync();
        var admin = t["admin"].Client;
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t["omar"].Id, role = "owner" }));
        var omar = t["omar"].Client;
        var rights = (await OkAsync(omar.GetAsync($"{Api}/communities/{community}"))).GetProperty("rights");
        Assert.True(rights.GetProperty("canAddSpeakUp").GetBoolean());
        Assert.False(rights.GetProperty("canAllowAnonymous").GetBoolean());
        Assert.True((await OkAsync(admin.GetAsync($"{Api}/communities/{community}"))).GetProperty("rights").GetProperty("canAllowAnonymous").GetBoolean());

        // The owner adds a Speak Up channel, but not one that allows anonymous posts.
        Assert.Equal(HttpStatusCode.Forbidden, (await omar.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "quiet", names = Names("Quiet"), kind = "speak_up", allowAnonymous = true })).StatusCode);
        await OkAsync(omar.PostAsJsonAsync($"{Api}/communities/{community}/channels", new { key = "open_door", names = Names("Open door"), kind = "speak_up" }), HttpStatusCode.Created);
        Assert.Contains("validation.engagement.anonymous_channel", await ErrorKeysAsync(await admin.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "chat", names = Names("Chat"), allowAnonymous = true })));
        Assert.Contains("validation.engagement.channel_kind", await ErrorKeysAsync(await admin.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "other", names = Names("Other"), kind = "secret" })));

        // Workplace has no Speak Up: no channel, no anonymous post, and the form does not offer it.
        var (w, wc, _) = await SetupAsync(package: "workplace");
        Assert.False((await OkAsync(w["admin"].Client.GetAsync($"{Api}/communities/{wc}"))).GetProperty("rights").GetProperty("canAddSpeakUp").GetBoolean());
        var denied = await w["admin"].Client.PostAsJsonAsync($"{Api}/communities/{wc}/channels", new { key = "speak_up", names = Names("Speak up"), kind = "speak_up" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("entitlement.not_in_package", await denied.Content.ReadAsStringAsync());
        var general = (await OkAsync(w["admin"].Client.GetAsync($"{Api}/communities/{wc}"))).GetProperty("channels")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await w["laila"].Client.PostAsJsonAsync($"{Api}/communities/{wc}/anonymous-posts",
            new { channelId = general, body = Body("Hello") })).StatusCode);
    }
}
