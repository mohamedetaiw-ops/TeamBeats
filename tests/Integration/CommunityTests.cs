using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T003 (US1, FR-001 to FR-004, FR-023): open, approval and closed communities; department communities whose
/// members follow the org unit; posts with mentions and hashtags; grouped comment notifications at most once an hour
/// per thread; and search that shows a post only to people who can read its community. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CommunityTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> call) => (await call).StatusCode;

    private sealed record Org(TestTenant T, Guid Holding, Guid Finance, Guid Audit, Guid Sales);

    private async Task<Org> OrgAsync(string package = "workplace")
    {
        var t = await world.CreateTenantAsync("eng-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var finance = await world.CreateUnitAsync(t, "Finance", holding, "department");
        var audit = await world.CreateUnitAsync(t, "Audit", finance, "department");
        var sales = await world.CreateUnitAsync(t, "Sales", holding, "department");
        foreach (var (name, unit) in new[] { ("laila", "Finance"), ("omar", "Sales"), ("ahmed", "Audit"), ("mona", "Sales"), ("kofi", "Sales") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        return new Org(t, holding, finance, audit, sales);
    }

    private static async Task<Guid> CommunityAsync(HttpClient admin, string en, string kind, Guid? orgUnitId = null) =>
        (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names(en, "مجتمع " + en), kind, orgUnitId }), HttpStatusCode.Created))
        .GetProperty("id").GetGuid();

    private static async Task<JsonElement> PostAsync(HttpClient client, Guid community, string text, params Guid[] mentions) =>
        await OkAsync(client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body(text), mentions }), HttpStatusCode.Created);

    private static async Task<Guid[]> FeedAsync(HttpClient client, Guid community) =>
        (await OkAsync(client.GetAsync($"{Api}/communities/{community}/posts"))).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray();

    private static async Task<Guid[]> ListedAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync($"{Api}/communities"))).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToArray();

    private async Task DispatchAsync(TestTenant t) => await world.DispatchTenantAsync(t.Id);

    /// <summary>How many times the person was told, counting notices folded into one group.</summary>
    private async Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        await world.Database.TenantScalarAsync<long>(t.Id, "select coalesce(sum(group_count), 0)::bigint from notifications.notification where user_id = @u and template_key = @k",
            ("u", t[user].Id), ("k", template));

    private static async Task<Guid[]> SearchAsync(HttpClient client, string q) =>
        (await OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}&kind=discussion"))).GetProperty("results").EnumerateArray()
        .Select(r => r.GetProperty("itemId").GetGuid()).ToArray();

    [Fact]
    public async Task Open_approval_and_closed_communities_decide_who_joins_reads_and_even_sees_them()
    {
        var o = await OrgAsync();
        var t = o.T;
        var admin = t["admin"].Client;
        var open = await CommunityAsync(admin, "Photography club", "open");
        var approval = await CommunityAsync(admin, "Runners", "approval");
        var closed = await CommunityAsync(admin, "Board support", "closed");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementCommunityCreated, open.ToString());

        // The creator owns each community and every community starts with its general channel.
        var created = await OkAsync(admin.GetAsync($"{Api}/communities/{open}"));
        Assert.Equal(["general"], created.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("key").GetString()!).ToArray());
        Assert.True(created.GetProperty("rights").GetProperty("canManage").GetBoolean());

        // Scenario 1: an open community is read and joined at once; an approval one sends a request to the owner.
        var laila = t["laila"].Client;
        Assert.True((await OkAsync(admin.GetAsync($"{Api}/rights"))).GetProperty("canCreate").GetBoolean());
        Assert.False((await OkAsync(laila.GetAsync($"{Api}/rights"))).GetProperty("canCreate").GetBoolean());
        Assert.Equal(new[] { open, approval }.Order().ToArray(), (await ListedAsync(laila)).Order().ToArray());
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(laila.GetAsync($"{Api}/communities/{closed}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(laila.GetAsync($"{Api}/communities/{closed}/posts")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(laila.PostAsync($"{Api}/communities/{closed}:join", null)));

        await PostAsync(admin, open, "Golden hour walk on Thursday");
        Assert.Single(await FeedAsync(laila, open));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(laila.PostAsJsonAsync($"{Api}/communities/{open}/posts", new { body = Body("Not yet a member") })));
        var joined = await OkAsync(laila.PostAsync($"{Api}/communities/{open}:join", null));
        Assert.True(joined.GetProperty("rights").GetProperty("canPost").GetBoolean());
        await PostAsync(laila, open, "Count me in");

        // An approval community lists its name but not its posts until the owner accepts.
        await PostAsync(admin, approval, "Saturday 10k route");
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(laila.GetAsync($"{Api}/communities/{approval}/posts")));
        var asked = await OkAsync(laila.PostAsync($"{Api}/communities/{approval}:join", null));
        Assert.Equal((false, true), (asked.GetProperty("rights").GetProperty("member").GetBoolean(), asked.GetProperty("rights").GetProperty("pending").GetBoolean()));
        var pending = (await OkAsync(admin.GetAsync($"{Api}/communities/{approval}/members"))).EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == t["laila"].Id);
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        // Other members do not see requests; a member cannot accept them.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(laila.PutAsJsonAsync($"{Api}/communities/{approval}/members", new { userId = t["laila"].Id })));
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{approval}/members", new { userId = t["laila"].Id }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementMemberChanged, approval.ToString());
        Assert.Single(await FeedAsync(laila, approval));

        // A closed community: the owner adds people; they then see and read it.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{closed}/members", new { userId = t["omar"].Id }));
        Assert.Contains(closed, await ListedAsync(t["omar"].Client));
        Assert.DoesNotContain(closed, await ListedAsync(laila));

        // The last owner cannot leave or be demoted; members leave freely.
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(admin.PostAsync($"{Api}/communities/{open}:leave", null)));
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(admin.PutAsJsonAsync($"{Api}/communities/{open}/members", new { userId = t["admin"].Id, role = "member" })));
        var left = await OkAsync(laila.PostAsync($"{Api}/communities/{open}:leave", null));
        Assert.False(left.GetProperty("rights").GetProperty("member").GetBoolean());

        // Only engagement administrators create communities; employees cannot.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(laila.PostAsJsonAsync($"{Api}/communities", new { names = Names("Mine", "لي"), kind = "open" })));

        // Settings change with If-Match and are audited; archiving stops new posts but keeps the feed.
        var current = await admin.GetAsync($"{Api}/communities/{open}");
        var stale = new HttpRequestMessage(HttpMethod.Put, $"{Api}/communities/{open}") { Content = JsonContent.Create(new { names = Names("Photo club", "نادي التصوير"), kind = "open" }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"v999\"");
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(admin.SendAsync(stale)));
        var change = new HttpRequestMessage(HttpMethod.Put, $"{Api}/communities/{open}") { Content = JsonContent.Create(new { names = Names("Photo club", "نادي التصوير"), kind = "open" }) };
        change.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        Assert.Equal("Photo club", (await OkAsync(admin.SendAsync(change))).GetProperty("names").GetProperty("en").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementCommunityChanged, open.ToString());
        await OkAsync(admin.PostAsync($"{Api}/communities/{open}:archive", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementCommunityArchived, open.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(admin.PostAsJsonAsync($"{Api}/communities/{open}/posts", new { body = Body("Too late") })));
        Assert.Equal(2, (await FeedAsync(admin, open)).Length);

        // Another tenant sees none of it.
        var other = await world.CreateTenantAsync("eng-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Empty(await ListedAsync(other["admin"].Client));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["admin"].Client.GetAsync($"{Api}/communities/{approval}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["admin"].Client.PostAsync($"{Api}/communities/{approval}:join", null)));
    }

    [Fact]
    public async Task A_department_community_follows_the_org_unit_and_its_moderators_act_in_their_scope()
    {
        var o = await OrgAsync();
        var t = o.T;
        var admin = t["admin"].Client;
        var finance = await CommunityAsync(admin, "Finance", "closed", o.Finance);
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Finance 2", "المالية 2"), kind = "open", orgUnitId = o.Finance })));

        // Scenario 2: Laila (Finance) and Ahmed (Audit, below Finance) are members; Omar (Sales) is not and cannot join.
        Assert.Contains(finance, await ListedAsync(t["laila"].Client));
        Assert.Contains(finance, await ListedAsync(t["ahmed"].Client));
        Assert.DoesNotContain(finance, await ListedAsync(t["omar"].Client));
        Assert.Equal(HttpStatusCode.Conflict, await StatusAsync(t["laila"].Client.PostAsync($"{Api}/communities/{finance}:join", null)));
        var members = (await OkAsync(t["laila"].Client.GetAsync($"{Api}/communities/{finance}/members"))).EnumerateArray()
            .Where(m => m.GetProperty("source").GetString() == "org_unit").Select(m => m.GetProperty("userId").GetGuid()).ToHashSet();
        Assert.Equal(new[] { t["laila"].Id, t["ahmed"].Id }.ToHashSet(), members);
        var post = (await PostAsync(t["laila"].Client, finance, "Month-end closing starts Monday")).GetProperty("post").GetProperty("id").GetGuid();

        // Omar moves to Finance: he is a member at once. Laila moves out: she no longer reads it.
        await world.AddMembershipAsync(t, "omar", "Finance");
        Assert.Contains(post, await FeedAsync(t["omar"].Client, finance));
        var lailaMembership = await OkAsync(admin.GetAsync($"/api/v1/org/units/{o.Finance}/memberships"));
        var membershipId = lailaMembership.EnumerateArray().First(m => m.GetProperty("userId").GetGuid() == t["laila"].Id).GetProperty("id").GetGuid();
        Assert.True((await admin.PostAsync($"/api/v1/org/memberships/{membershipId}:end", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["laila"].Client.GetAsync($"{Api}/communities/{finance}/posts")));

        // A moderator in the Finance scope pins; one in Sales cannot, and does not even see the closed community.
        await world.SeedUserAsync(t, "mod_fin", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "mod_sales", MembershipTypes.Employee);
        await world.AssignAsync(t, "mod_fin", BuiltInRoles.EngagementAdministrator, "org_subtree", o.Finance);
        await world.AssignAsync(t, "mod_sales", BuiltInRoles.EngagementAdministrator, "org_subtree", o.Sales);
        Assert.True((await OkAsync(t["mod_fin"].Client.PostAsync($"{Api}/posts/{post}:pin", null))).GetProperty("pinned").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementPostPinned, post.ToString());
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["mod_sales"].Client.PostAsync($"{Api}/posts/{post}:pin", null)));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["ahmed"].Client.PostAsync($"{Api}/posts/{post}:pin", null)));
        // An owner of the community can link units only with engagement.manage; owners made by hand manage the rest.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{finance}/members", new { userId = t["ahmed"].Id, role = "owner" }));
        var current = await t["ahmed"].Client.GetAsync($"{Api}/communities/{finance}");
        var relink = new HttpRequestMessage(HttpMethod.Put, $"{Api}/communities/{finance}") { Content = JsonContent.Create(new { names = Names("Finance", "المالية"), kind = "closed", orgUnitId = o.Sales }) };
        relink.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(t["ahmed"].Client.SendAsync(relink)));
    }

    [Fact]
    public async Task Mentions_hashtags_and_follows_tell_only_people_who_can_read_and_group_comment_notices()
    {
        var o = await OrgAsync();
        var t = o.T;
        var admin = t["admin"].Client;
        var club = await CommunityAsync(admin, "Budget circle", "approval");
        foreach (var name in new[] { "laila", "omar", "ahmed" })
        {
            await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{club}/members", new { userId = t[name].Id }));
        }
        var other = await world.CreateTenantAsync("eng-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.SeedUserAsync(other, "nadia", MembershipTypes.Employee);

        // Scenario 3: @Omar is told; Mona (not a member) is not, and Laila hears who could not be reached.
        var saved = await PostAsync(t["laila"].Client, club, "Draft #Budget for #Q3 is ready, see #budget notes", t["omar"].Id, t["mona"].Id);
        var post = saved.GetProperty("post");
        var postId = post.GetProperty("id").GetGuid();
        Assert.Equal(["budget", "q3"], post.GetProperty("hashtags").EnumerateArray().Select(h => h.GetString()!).ToArray());
        Assert.Equal([t["mona"].Id], saved.GetProperty("notReachable").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(1L, await NotificationsAsync(t, "omar", "engagement.mention"));
        Assert.Equal(0L, await NotificationsAsync(t, "mona", "engagement.mention"));
        // A person of another tenant cannot be mentioned at all.
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/communities/{club}/posts",
            new { body = Body("Hello"), mentions = new[] { other["nadia"].Id } })));

        // The hashtag page lists the post for members and nothing for Mona or the other tenant.
        Assert.Equal([postId], (await OkAsync(t["omar"].Client.GetAsync($"{Api}/hashtags/budget"))).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray());
        Assert.Empty((await OkAsync(t["mona"].Client.GetAsync($"{Api}/hashtags/budget"))).GetProperty("items").EnumerateArray());
        Assert.Empty((await OkAsync(other["nadia"].Client.GetAsync($"{Api}/hashtags/budget"))).GetProperty("items").EnumerateArray());

        // Scenario 4: Laila follows her post. Comments within the hour tell her once, grouped; Omar's own comments do not tell him.
        var first = await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/posts/{postId}/comments", new { body = "Looks good" }), HttpStatusCode.Created);
        var commentId = first.GetProperty("comment").GetProperty("id").GetGuid();
        await OkAsync(t["ahmed"].Client.PostAsJsonAsync($"{Api}/posts/{postId}/comments", new { body = "Agreed", parentId = commentId }), HttpStatusCode.Created);
        await OkAsync(t["ahmed"].Client.PostAsJsonAsync($"{Api}/posts/{postId}/comments", new { body = "One more thing" }), HttpStatusCode.Created);
        Assert.Equal(1L, await NotificationsAsync(t, "laila", "engagement.comment.new"));
        // Omar follows now that he commented, and heard of Ahmed's first comment once.
        Assert.Equal(1L, await NotificationsAsync(t, "omar", "engagement.comment.new"));
        // An hour later the next comment tells her again.
        await world.Database.TenantScalarAsync<long>(t.Id,
            "with u as (update engagement.post_follow set notified_at = notified_at - interval '61 minutes' where post_id = @p returning 1) select count(*) from u", ("p", postId));
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/posts/{postId}/comments", new { body = "Final numbers attached" }), HttpStatusCode.Created);
        Assert.Equal(2L, await NotificationsAsync(t, "laila", "engagement.comment.new"));

        // One reply level only.
        var reply = (await OkAsync(t["laila"].Client.GetAsync($"{Api}/posts/{postId}/comments"))).EnumerateArray().First(c => c.GetProperty("parentId").ValueKind == JsonValueKind.String);
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/posts/{postId}/comments",
            new { body = "Nested", parentId = reply.GetProperty("id").GetGuid() })));

        // Reactions: one per person, changeable, with counts; outsiders cannot react.
        var counts = await OkAsync(t["omar"].Client.PutAsJsonAsync($"{Api}/posts/{postId}/reaction", new { kind = "like" }));
        Assert.Equal((1, "like"), (counts.GetProperty("counts").GetProperty("like").GetInt32(), counts.GetProperty("mine").GetString()));
        counts = await OkAsync(t["omar"].Client.PutAsJsonAsync($"{Api}/posts/{postId}/reaction", new { kind = "celebrate" }));
        Assert.Equal((0, 1), (counts.GetProperty("counts").GetProperty("like").GetInt32(), counts.GetProperty("counts").GetProperty("celebrate").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["mona"].Client.PutAsJsonAsync($"{Api}/posts/{postId}/reaction", new { kind = "like" })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(t["omar"].Client.PutAsJsonAsync($"{Api}/posts/{postId}/reaction", new { kind = "angry" })));

        // Only the author edits; editing tells only people newly mentioned.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["omar"].Client.PutAsJsonAsync($"{Api}/posts/{postId}", new { body = Body("Hijacked") })));
        await OkAsync(t["laila"].Client.PutAsJsonAsync($"{Api}/posts/{postId}", new { body = Body("Draft #budget is ready"), mentions = new[] { t["omar"].Id, t["ahmed"].Id } }));
        Assert.Equal(1L, await NotificationsAsync(t, "omar", "engagement.mention"));
        Assert.Equal(1L, await NotificationsAsync(t, "ahmed", "engagement.mention"));
        Assert.Equal(["budget"], (await OkAsync(t["laila"].Client.GetAsync($"{Api}/posts/{postId}"))).GetProperty("hashtags").EnumerateArray().Select(h => h.GetString()!).ToArray());
    }

    [Fact]
    public async Task Posts_are_found_in_search_only_by_people_who_can_read_their_community()
    {
        var o = await OrgAsync();
        var t = o.T;
        var admin = t["admin"].Client;
        var open = await CommunityAsync(admin, "Garden", "open");
        var closed = await CommunityAsync(admin, "Merger room", "closed");
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{closed}/members", new { userId = t["omar"].Id }));
        var tomato = (await PostAsync(admin, open, "Tomato seedlings are free at reception")).GetProperty("post").GetProperty("id").GetGuid();
        var secret = (await PostAsync(admin, closed, "Tomato project milestone for the merger")).GetProperty("post").GetProperty("id").GetGuid();
        await DispatchAsync(t);

        // Scenario 5 and FR-001: the closed post is found only by its members.
        Assert.Equal([tomato], await SearchAsync(t["laila"].Client, "tomato"));
        Assert.Equal(new[] { tomato, secret }.Order().ToArray(), (await SearchAsync(t["omar"].Client, "tomato")).Order().ToArray());

        // A deleted post leaves the index.
        await OkAsync(admin.DeleteAsync($"{Api}/posts/{tomato}"), HttpStatusCode.NoContent);
        await DispatchAsync(t);
        Assert.Empty(await SearchAsync(t["laila"].Client, "tomato"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.unit where item_id = @i", ("i", tomato)));

        // Another tenant finds nothing.
        var other = await world.CreateTenantAsync("eng-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Empty(await SearchAsync(other["admin"].Client, "tomato"));
    }
}
