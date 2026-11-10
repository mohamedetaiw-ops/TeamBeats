using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T018 (US8) and spec 015 US8 scenario 3: the Slack pack. People link their Slack account by work email,
/// categories with Slack on arrive as direct messages, a person who left the workspace falls back to email, and
/// communicators share a published item to a channel. Slack is a fake host of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SlackTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/pack-connections";
    private const string Preferences = "/api/v1/me/notification-preferences";

    private FakeRestApi Rest => world.Factory.Rest;

    private static string Host() => $"slack-{Guid.NewGuid():N}"[..20] + ".rest.test";

    /// <summary>A small Slack workspace: members by email, channels, and the messages posted.</summary>
    private sealed class FakeSlack
    {
        public Dictionary<string, string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Gone { get; } = [];
        public List<(string Channel, string Text)> Posted { get; } = [];

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            var method = s.Uri.AbsolutePath.Split('/')[^1];
            var body = s.Body is { Length: > 0 } b ? JsonDocument.Parse(b).RootElement : default;
            return method switch
            {
                "auth.test" => Ok(new { ok = true, team = "Acme" }),
                "users.lookupByEmail" => Members.TryGetValue(Uri.UnescapeDataString(s.Uri.Query.Split("email=")[1]), out var id)
                    ? Ok(new { ok = true, user = new { id, team_id = "T0000000001", deleted = false } })
                    : Ok(new { ok = false, error = "users_not_found" }),
                "conversations.open" => Gone.Contains(body.GetProperty("users").GetString()!)
                    ? Ok(new { ok = false, error = "user_not_found" })
                    : Ok(new { ok = true, channel = new { id = "D" + body.GetProperty("users").GetString()![1..] } }),
                "conversations.list" => Ok(new
                {
                    ok = true,
                    channels = new object[] { new { id = "C0000000001", name = "general", is_member = true }, new { id = "C0000000002", name = "random", is_member = false } },
                }),
                "chat.postMessage" when body.GetProperty("channel").GetString() == "C0000000002" => Ok(new { ok = false, error = "not_in_channel" }),
                "chat.postMessage" => Post(body),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Post(JsonElement body)
        {
            lock (Posted)
            {
                Posted.Add((body.GetProperty("channel").GetString()!, body.GetProperty("text").GetString()!));
            }
            return Ok(new { ok = true, ts = "1700000000.000100" });
        }

        private static HttpResponseMessage Ok(object body) => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));
    }

    private sealed record Setup(TestTenant T, Guid Connection, FakeSlack Slack, string Host);

    private async Task<Setup> SetupAsync()
    {
        var t = await world.CreateTenantAsync("sl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "sara", MembershipTypes.Employee);
        var host = Host();
        var slack = new FakeSlack();
        Rest.On(host, slack.Respond);
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "slack-bot"), "fictional-bot-token");
        var created = await admin.PostAsJsonAsync(Packs, new
        {
            kind = "slack",
            displayName = "Acme Slack",
            settings = new { baseUrl = $"https://{host}/api", botTokenRef = SecretReferences.For(t.Id, "slack-bot") },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var test = await (await admin.PostAsync($"{Packs}/{id}:test", null)).ReadAsync<JsonElement>();
        Assert.Equal("ok", test.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{id}:enable", null)).StatusCode);
        return new Setup(t, id, slack, host);
    }

    private Task SendAsync(TestTenant t, string category, Guid user) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([user], category, "system.sample", "/p/home",
                new Dictionary<string, string> { ["label"] = "HR" }));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> DeliverSlackAsync(TestTenant t, DateTimeOffset? at = null) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var count = await sp.GetRequiredService<SlackDelivery>().DeliverDueAsync(at ?? DateTimeOffset.UtcNow, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return count;
        });

    private Task<long> DeliveriesAsync(TestTenant t, string channel, string status) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.delivery where channel = @c and status = @s", ("c", channel), ("s", status));

    private static async Task<JsonElement> LinkAsync(TestUser user)
    {
        var response = await user.Client.PostAsync($"{Preferences}/slack:link", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> SlackOnAsync(TestUser user, string category) =>
        user.Client.PutAsJsonAsync(Preferences, new { categories = new[] { new { category, email = false, digest = false, slack = true } } });

    [Fact]
    public async Task A_linked_person_gets_categories_with_Slack_on_as_direct_messages()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];
        s.Slack.Members[sara.Email] = "U0000000042";

        var before = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.True(before.GetProperty("slack").GetProperty("available").GetBoolean());
        Assert.False(before.GetProperty("slack").GetProperty("linked").GetBoolean());

        var linked = await LinkAsync(sara);
        Assert.True(linked.GetProperty("slack").GetProperty("linked").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SlackOnAsync(sara, "announcement")).StatusCode);
        var prefs = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.True(prefs.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("category").GetString() == "announcement").GetProperty("slack").GetBoolean());

        await SendAsync(s.T, "announcement", sara.Id);
        // A category with Slack off gets no direct message.
        await SendAsync(s.T, "comment", sara.Id);
        Assert.Equal(1, await DeliverSlackAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "slack", "sent"));

        var (channel, text) = Assert.Single(s.Slack.Posted);
        Assert.Equal("D0000000042", channel);
        Assert.Contains($"<https://{s.T.Key}.{ApiFactory.BaseDomain}/p/home>", text, StringComparison.Ordinal);
        var dm = Rest.SentTo(s.Host).Single(x => x.Uri.AbsolutePath.EndsWith("chat.postMessage", StringComparison.Ordinal));
        Assert.Equal("Bearer fictional-bot-token", dm.Headers["Authorization"]);
        Assert.Equal(0, await DeliverSlackAsync(s.T));

        // The calls are in the connection's run history, without content.
        var runs = await (await s.T["admin"].Client.GetAsync($"/api/v1/integrations/connections/{s.Connection}/runs")).ReadAsync<JsonElement>();
        Assert.Contains(runs.GetProperty("items").EnumerateArray(), r => r.GetProperty("action").GetString() == "send_direct_message");
        Assert.DoesNotContain("p/home", runs.GetRawText());

        // Unlinking stops direct messages.
        Assert.Equal(HttpStatusCode.OK, (await sara.Client.DeleteAsync($"{Preferences}/slack")).StatusCode);
        await SendAsync(s.T, "announcement", sara.Id);
        Assert.Equal(0, await DeliverSlackAsync(s.T));
        Assert.Single(s.Slack.Posted);
    }

    [Fact]
    public async Task A_person_who_left_the_workspace_is_unlinked_and_the_message_falls_back_to_email()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];
        s.Slack.Members[sara.Email] = "U0000000043";
        await LinkAsync(sara);
        Assert.Equal(HttpStatusCode.OK, (await SlackOnAsync(sara, "announcement")).StatusCode);
        await SendAsync(s.T, "announcement", sara.Id);
        Assert.Equal(0, await DeliveriesAsync(s.T, "email", "pending"));

        s.Slack.Gone.Add("U0000000043");
        Assert.Equal(1, await DeliverSlackAsync(s.T));
        Assert.Equal(1, await DeliveriesAsync(s.T, "slack", "dropped"));
        Assert.Equal(1, await DeliveriesAsync(s.T, "email", "pending"));
        Assert.Empty(s.Slack.Posted);
        var prefs = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.False(prefs.GetProperty("slack").GetProperty("linked").GetBoolean());
    }

    [Fact]
    public async Task Linking_needs_a_workspace_member_and_Slack_stays_inside_its_tenant()
    {
        var s = await SetupAsync();
        var sara = s.T["sara"];
        var refused = await sara.Client.PostAsync($"{Preferences}/slack:link", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.slack.not_in_workspace", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Another tenant has no Slack connection: nothing to link, and nothing of this tenant's Slack is visible.
        var other = await world.CreateTenantAsync("sl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var view = await (await other["admin"].Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.False(view.GetProperty("slack").GetProperty("available").GetBoolean());
        var none = await other["admin"].Client.PostAsync($"{Preferences}/slack:link", null);
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Packs}/{s.Connection}")).StatusCode);

        // A disabled connection is not available.
        Assert.Equal(HttpStatusCode.OK, (await s.T["admin"].Client.PostAsync($"{Packs}/{s.Connection}:disable", null)).StatusCode);
        var disabled = await (await sara.Client.GetAsync(Preferences)).ReadAsync<JsonElement>();
        Assert.False(disabled.GetProperty("slack").GetProperty("available").GetBoolean());

        // Slack connections never capture payloads: they carry notification text.
        var capture = await s.T["admin"].Client.PutAsJsonAsync($"/api/v1/integrations/connections/{s.Connection}/capture", new { hours = 1, publicOrInternal = true });
        Assert.Contains("validation.connector.capture_kind", await capture.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_communicator_shares_a_published_item_everyone_may_read_to_a_channel()
    {
        var s = await SetupAsync();
        var admin = s.T["admin"].Client;
        var sara = s.T["sara"];

        var channels = await (await admin.GetAsync("/api/v1/content/items/slack-channels")).ReadAsync<JsonElement>();
        Assert.Equal(["general", "random"], channels.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.Client.GetAsync("/api/v1/content/items/slack-channels")).StatusCode);

        var item = await CreateNewsAsync(admin, "Office opening <!channel>", "internal");
        var id = item.GetProperty("id").GetGuid();
        var early = await admin.PostAsJsonAsync($"/api/v1/content/items/{id}/share-slack", new { channelId = "C0000000001" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("conflict.content.share_not_published", await early.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { })).StatusCode);
        var shared = await admin.PostAsJsonAsync($"/api/v1/content/items/{id}/share-slack", new { channelId = "C0000000001", note = "Please read" });
        Assert.Equal(HttpStatusCode.NoContent, shared.StatusCode);
        var (channel, text) = Assert.Single(s.Slack.Posted);
        Assert.Equal("C0000000001", channel);
        Assert.Contains("Please read", text, StringComparison.Ordinal);
        Assert.Contains("Office opening &lt;!channel&gt;", text, StringComparison.Ordinal);
        Assert.Contains($"<https://{s.T.Key}.{ApiFactory.BaseDomain}/content/read/{id}>", text, StringComparison.Ordinal);
        Assert.Equal(1, await world.Database.AuditCountAsync(s.T.Id, AuditActions.ContentShared, id.ToString()));

        // A channel the app is not in, an employee, and another tenant are refused.
        var notIn = await admin.PostAsJsonAsync($"/api/v1/content/items/{id}/share-slack", new { channelId = "C0000000002" });
        Assert.Contains("conflict.slack.channel_unavailable", await notIn.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await sara.Client.PostAsJsonAsync($"/api/v1/content/items/{id}/share-slack", new { channelId = "C0000000001" })).StatusCode);
        var other = await world.CreateTenantAsync("sl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"/api/v1/content/items/{id}/share-slack", new { channelId = "C0000000001" })).StatusCode);

        // Confidential content never goes to a channel.
        var secret = await CreateNewsAsync(admin, "Board minutes", "confidential");
        var secretId = secret.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/content/items/{secretId}/publish", new { })).StatusCode);
        var restricted = await admin.PostAsJsonAsync($"/api/v1/content/items/{secretId}/share-slack", new { channelId = "C0000000001" });
        Assert.Contains("conflict.content.share_restricted", await restricted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(s.Slack.Posted);
    }

    private static async Task<JsonElement> CreateNewsAsync(HttpClient client, string title, string classification)
    {
        var body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } };
        var response = await client.PostAsJsonAsync("/api/v1/content/items", new
        {
            type = "news",
            classification,
            variants = new Dictionary<string, object> { ["en"] = new { title, body } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }
}
