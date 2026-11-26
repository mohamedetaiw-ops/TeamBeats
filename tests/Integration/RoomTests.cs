using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 R3 T016 (US9, FR-027 to FR-029): discussion rooms inside a community. Owners open rooms, members write,
/// open readers read, a closed community's rooms stay invisible to others, the live stream delivers new messages and
/// stops when access is lost, mentions reach only readers, the keyword filter holds a message, three reports hide it,
/// moderators remove it with a reason, muted members and archived rooms take no messages. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RoomTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en, ["ar"] = "غرفة " + en };

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
            : problem.TryGetProperty("detailKey", out var key) ? [key.GetString()!] : [];
    }

    private async Task<(TestTenant T, Guid Community, Guid Room)> SetupAsync(string kind = "open", string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("room-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "laila", "omar", "ahmed", "mona", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = Names("Town square"), kind }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        foreach (var name in new[] { "laila", "omar", "ahmed", "mona" })
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id }));
        }
        if (package == "workplace")
        {
            return (t, community, Guid.Empty);
        }
        var room = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities/{community}/rooms", new { names = Names("Coffee corner") }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        return (t, community, room);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid room, string body, params Guid[] mentions) =>
        client.PostAsJsonAsync($"{Api}/rooms/{room}/messages", new { body, mentions });

    private static async Task<Guid> SentAsync(HttpClient client, Guid room, string body, params Guid[] mentions) =>
        (await OkAsync(SendAsync(client, room, body, mentions), HttpStatusCode.Created)).GetProperty("message").GetProperty("id").GetGuid();

    private static async Task<JsonElement> PageAsync(HttpClient client, Guid room, string? after = null) =>
        await OkAsync(client.GetAsync(after is null ? $"{Api}/rooms/{room}/messages" : $"{Api}/rooms/{room}/messages?after={after}"));

    private static async Task<Guid[]> MessagesAsync(HttpClient client, Guid room) =>
        (await PageAsync(client, room)).GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray();

    private async Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        await world.Database.TenantScalarAsync<long>(t.Id, "select coalesce(sum(group_count), 0)::bigint from notifications.notification where user_id = @u and template_key = @k",
            ("u", t[user].Id), ("k", template));

    /// <summary>Reads server-sent events until one named <paramref name="name"/> arrives, and returns its data.</summary>
    private static async Task<JsonElement> NextEventAsync(StreamReader reader, string name, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string? current = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("The stream ended.");
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                current = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && current == name)
            {
                return JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone();
            }
        }
    }

    [Fact]
    public async Task Owners_open_rooms_members_write_and_readers_read()
    {
        var (t, community, room) = await SetupAsync();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementRoomCreated, room.ToString());

        // A member cannot open a room; names need an enabled language.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/communities/{community}/rooms", new { names = Names("Mine") })));
        Assert.Contains("validation.engagement.names", await ErrorKeysAsync(await t["admin"].Client.PostAsJsonAsync($"{Api}/communities/{community}/rooms",
            new { names = new Dictionary<string, string> { ["xx"] = "Nope" } })));

        var listed = Assert.Single((await OkAsync(t["laila"].Client.GetAsync($"{Api}/communities/{community}/rooms"))).EnumerateArray());
        Assert.Equal((room, true, false), (listed.GetProperty("id").GetGuid(), listed.GetProperty("rights").GetProperty("canWrite").GetBoolean(),
            listed.GetProperty("rights").GetProperty("canArchive").GetBoolean()));

        var first = await SentAsync(t["laila"].Client, room, "Morning, who is in today?");
        var second = await SentAsync(t["omar"].Client, room, "I am, by the window");
        Assert.Contains("validation.engagement.body_required", await ErrorKeysAsync(await SendAsync(t["omar"].Client, room, "   ")));
        Assert.Contains("validation.engagement.body_too_long", await ErrorKeysAsync(await SendAsync(t["omar"].Client, room, new string('a', 2001))));

        // An open reader who is not a member reads but does not write; the cursor gives only newer messages.
        var page = await PageAsync(t["kofi"].Client, room);
        Assert.Equal([first, second], page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray());
        Assert.False((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/rooms/{room}"))).GetProperty("rights").GetProperty("canWrite").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(SendAsync(t["kofi"].Client, room, "Hello")));
        var third = await SentAsync(t["ahmed"].Client, room, "On my way");
        var newer = await PageAsync(t["kofi"].Client, room, page.GetProperty("cursor").GetString());
        Assert.Equal([third], newer.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToArray());
        Assert.Contains("validation.engagement.cursor", await ErrorKeysAsync(await t["kofi"].Client.GetAsync($"{Api}/rooms/{room}/messages?after=nonsense")));

        // The author deletes their own message; nobody else can.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["omar"].Client.DeleteAsync($"{Api}/room-messages/{first}")));
        await OkAsync(t["laila"].Client.DeleteAsync($"{Api}/room-messages/{first}"), HttpStatusCode.NoContent);
        Assert.DoesNotContain(first, await MessagesAsync(t["omar"].Client, room));

        // An archived room stays readable and takes nothing new; only managers archive.
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.PostAsync($"{Api}/rooms/{room}:archive", null)));
        Assert.Equal("archived", (await OkAsync(t["admin"].Client.PostAsync($"{Api}/rooms/{room}:archive", null))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementRoomArchived, room.ToString());
        Assert.Contains("conflict.engagement.room_archived", await ErrorKeysAsync(await SendAsync(t["laila"].Client, room, "Still here?")));
        Assert.Contains(second, await MessagesAsync(t["kofi"].Client, room));

        // Another tenant reaches nothing.
        var other = await world.CreateTenantAsync("room-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["admin"].Client.GetAsync($"{Api}/rooms/{room}/messages")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(SendAsync(other["admin"].Client, room, "Hi")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other["admin"].Client.GetAsync($"{Api}/rooms/{room}/stream")));
    }

    [Fact]
    public async Task Closed_community_rooms_are_invisible_and_rooms_need_the_capability()
    {
        var (t, community, room) = await SetupAsync("closed");
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"{Api}/communities/{community}/rooms")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["kofi"].Client.GetAsync($"{Api}/rooms/{room}/messages")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(SendAsync(t["kofi"].Client, room, "Let me in")));
        await SentAsync(t["laila"].Client, room, "Members only");

        // Workplace has no rooms.
        var (w, wc, _) = await SetupAsync(package: "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(w["admin"].Client.PostAsJsonAsync($"{Api}/communities/{wc}/rooms", new { names = Names("Nope") })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(w["laila"].Client.GetAsync($"{Api}/communities/{wc}/rooms")));
    }

    [Fact]
    public async Task The_stream_delivers_new_messages_and_stops_when_access_is_lost()
    {
        var (t, community, room) = await SetupAsync("closed");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/rooms/{room}/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await t["mona"].Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await NextEventAsync(reader, "ready", TimeSpan.FromSeconds(10));

        // A message from someone else arrives within the poll time.
        var sent = await SentAsync(t["laila"].Client, room, "Standup moved to ten");
        var live = await NextEventAsync(reader, "messages", TimeSpan.FromSeconds(10));
        Assert.Contains(sent, live.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()));

        // Removed from the closed community, the person stops receiving at once.
        await OkAsync(t["admin"].Client.DeleteAsync($"{Api}/communities/{community}/members/{t["mona"].Id}"));
        await SentAsync(t["laila"].Client, room, "Not for Mona");
        await NextEventAsync(reader, "gone", TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Mentions_keyword_holds_reports_and_removal_follow_the_comment_rules()
    {
        var (t, community, room) = await SetupAsync("closed");
        var admin = t["admin"].Client;

        // Mentions reach only people who read the community; the writer hears who was not told.
        var saved = await OkAsync(SendAsync(t["laila"].Client, room, "Omar and Kofi, see the plan", t["omar"].Id, t["kofi"].Id), HttpStatusCode.Created);
        Assert.Equal([t["kofi"].Id], saved.GetProperty("notReachable").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(1, await NotificationsAsync(t, "omar", "engagement.room.mention"));
        Assert.Equal(0, await NotificationsAsync(t, "kofi", "engagement.room.mention"));

        // The keyword filter holds a message: only its author sees it, and a moderator approves it, telling its mentions then.
        var current = await admin.GetAsync($"{Api}/settings/keywords");
        var put = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/keywords") { Content = JsonContent.Create(new { keywords = new[] { "scam" } }) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(admin.SendAsync(put));
        var held = await OkAsync(SendAsync(t["omar"].Client, room, "This offer is a scam, Ahmed", t["ahmed"].Id), HttpStatusCode.Created);
        var heldId = held.GetProperty("message").GetProperty("id").GetGuid();
        Assert.Equal(("held", "keyword"), (held.GetProperty("message").GetProperty("status").GetString(), held.GetProperty("message").GetProperty("heldReason").GetString()));
        Assert.DoesNotContain(heldId, await MessagesAsync(t["mona"].Client, room));
        Assert.Contains(heldId, await MessagesAsync(t["omar"].Client, room));
        Assert.Equal(0, await NotificationsAsync(t, "ahmed", "engagement.room.mention"));
        var item = Assert.Single((await OkAsync(admin.GetAsync($"{Api}/moderation?communityId={community}"))).EnumerateArray());
        Assert.Equal(("message", heldId, room), (item.GetProperty("type").GetString(), item.GetProperty("id").GetGuid(), item.GetProperty("postId").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["mona"].Client.PostAsync($"{Api}/moderation/message/{heldId}:approve", null)));
        await OkAsync(admin.PostAsync($"{Api}/moderation/message/{heldId}:approve", null), HttpStatusCode.NoContent);
        Assert.Contains(heldId, await MessagesAsync(t["mona"].Client, room));
        Assert.Equal(1, await NotificationsAsync(t, "ahmed", "engagement.room.mention"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentApproved, heldId.ToString());

        // Three different people hide a message; the author cannot report their own.
        var rude = await SentAsync(t["laila"].Client, room, "Nobody asked you");
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/room-messages/{rude}:report", new { reason = "spam" })));
        Assert.Contains("validation.engagement.report_reason", await ErrorKeysAsync(await t["omar"].Client.PostAsJsonAsync($"{Api}/room-messages/{rude}:report", new { reason = "boring" })));
        foreach (var name in new[] { "omar", "ahmed", "mona" })
        {
            await OkAsync(t[name].Client.PostAsJsonAsync($"{Api}/room-messages/{rude}:report", new { reason = "harassment" }), HttpStatusCode.NoContent);
        }
        Assert.DoesNotContain(rude, await MessagesAsync(t["omar"].Client, room));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentHidden, rude.ToString());

        // A moderator removes it with a reason; the author reads the reason and is told.
        Assert.Contains("validation.engagement.removal_reason", await ErrorKeysAsync(await admin.PostAsJsonAsync($"{Api}/moderation/message/{rude}:remove", new { reason = " " })));
        await OkAsync(admin.PostAsJsonAsync($"{Api}/moderation/message/{rude}:remove", new { reason = "Be kind to colleagues" }), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementContentRemoved, rude.ToString());
        var own = (await PageAsync(t["laila"].Client, room)).GetProperty("items").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == rude);
        Assert.Equal(("removed", "Be kind to colleagues"), (own.GetProperty("status").GetString(), own.GetProperty("removedReason").GetString()));
        Assert.Equal(1, await NotificationsAsync(t, "laila", "engagement.content.removed"));
        Assert.Empty((await OkAsync(admin.GetAsync($"{Api}/moderation?communityId={community}"))).EnumerateArray());

        // A muted member reads but does not write.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mutes", new { userId = t["laila"].Id, until = DateTimeOffset.UtcNow.AddDays(1) }));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(SendAsync(t["laila"].Client, room, "Can I still talk?")));
        Assert.NotEmpty(await MessagesAsync(t["laila"].Client, room));
    }
}
