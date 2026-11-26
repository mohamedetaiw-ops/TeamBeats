using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.DevFakes;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 R3 T018 (US10, FR-030): one-way mirroring of a community's new posts to a Slack or Teams channel. Only
/// engagement administrators set a mirror, only on an open, internal community without a unit or space. Each new post
/// goes once as a notice with the author and the community, and its text only when the mirror allows an excerpt;
/// anonymous posts never go. A refused channel is kept as the mirror's last failure. Slack and the Teams bot are fakes
/// of the test factory. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class MirrorTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";
    private const string TeamsChannel = "19:a1b2c3d4e5@thread.tacv2";

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en, ["ar"] = "مجتمع " + en };

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call)
    {
        var problem = JsonDocument.Parse(await (await call).Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : problem.GetProperty("detailKey").GetString()!;
    }

    /// <summary>A Slack workspace with one channel the app is in and one it is not, keeping what was posted.</summary>
    private sealed class FakeSlack
    {
        public List<(string Channel, string Text)> Posted { get; } = [];

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            var method = s.Uri.AbsolutePath.Split('/')[^1];
            var body = s.Body is { Length: > 0 } b ? JsonDocument.Parse(b).RootElement : default;
            return method switch
            {
                "auth.test" => Ok(new { ok = true, team = "Acme" }),
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

    private async Task<(TestTenant T, Guid Community)> TenantAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("mir-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "laila", MembershipTypes.Employee);
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = Names("Town square"), kind = "open" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t["laila"].Id }));
        return (t, community);
    }

    private async Task<FakeSlack> SlackAsync(TestTenant t)
    {
        var host = $"slack-{Guid.NewGuid():N}"[..20] + ".rest.test";
        var slack = new FakeSlack();
        world.Factory.Rest.On(host, slack.Respond);
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "slack-bot"), "fictional-bot-token");
        var created = await OkAsync(admin.PostAsJsonAsync("/api/v1/integrations/pack-connections", new
        {
            kind = "slack",
            displayName = "Acme Slack",
            settings = new { baseUrl = $"https://{host}/api", botTokenRef = SecretReferences.For(t.Id, "slack-bot") },
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsync($"/api/v1/integrations/pack-connections/{id}:test", null));
        await OkAsync(admin.PostAsync($"/api/v1/integrations/pack-connections/{id}:enable", null));
        return slack;
    }

    /// <summary>A Microsoft 365 connection with Teams granted, as the Teams quick action tests set it up.</summary>
    private async Task<Guid> TeamsAsync(TestTenant t)
    {
        var directory = Guid.NewGuid();
        var admin = t["admin"].Client;
        var connection = (await OkAsync(admin.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "teams" },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        world.Factory.Graph.Consent(directory, ["TeamsActivity.Send"]);
        var start = await admin.GetAsync($"/api/v1/integrations/connections/{connection}/features/teams/consent");
        var query = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query);
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(query["state"])}");
        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_consent=", StringComparison.Ordinal)));
        await client.GetAsync(relay.Headers.Location!.PathAndQuery);
        return directory;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static async Task<Guid> PostAsync(HttpClient client, Guid community, string text) =>
        (await OkAsync(client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body(text) }), HttpStatusCode.Created)).GetProperty("post").GetProperty("id").GetGuid();

    [Fact]
    public async Task Administrators_mirror_an_open_community_to_Slack_once_per_post_with_the_excerpt_only_when_allowed()
    {
        var (t, community) = await TenantAsync();
        var slack = await SlackAsync(t);
        var admin = t["admin"].Client;

        var view = await OkAsync(admin.GetAsync($"{Api}/communities/{community}/mirrors"));
        Assert.True(view.GetProperty("eligible").GetBoolean());
        Assert.True(view.GetProperty("slackAvailable").GetBoolean());
        Assert.Equal(["general", "random"], view.GetProperty("slackChannels").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync($"{Api}/communities/{community}/mirrors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/slack", new { channelId = "C0000000001" })).StatusCode);
        Assert.Equal("validation.slack.channel", await ProblemKeyAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/slack", new { channelId = "general" })));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/discord", new { channelId = "C0000000001" })).StatusCode);

        var set = await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/slack", new { channelId = "C0000000001", excerpt = false }));
        Assert.Equal(("slack", "C0000000001", false), (set.GetProperty("mirrors")[0].GetProperty("target").GetString(), set.GetProperty("mirrors")[0].GetProperty("channelId").GetString(),
            set.GetProperty("mirrors")[0].GetProperty("excerpt").GetBoolean()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementMirrorSet, community.ToString());

        // Without the excerpt only the author, the community and the link leave TeamBeats.
        var first = await PostAsync(t["laila"].Client, community, "Quarterly figures are confidential until Monday");
        await DispatchAsync();
        var (channel, text) = Assert.Single(slack.Posted);
        Assert.Equal("C0000000001", channel);
        Assert.Contains("posted in Town square", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly figures", text, StringComparison.Ordinal);
        Assert.Contains($"/communities/{community}/posts/{first}", text, StringComparison.Ordinal);

        // A post goes once, even when its event is handled again.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<TeamBeats.Modules.Engagement.Mirrors>().HandleAsync(new TeamBeats.Modules.Engagement.Contracts.PostPublished(first, community, Guid.Empty), CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Single(slack.Posted);

        // With the excerpt, the first 200 characters go too.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/slack", new { channelId = "C0000000001", excerpt = true }));
        await PostAsync(t["laila"].Client, community, "Lunch and learn on Thursday " + new string('x', 300));
        await DispatchAsync();
        Assert.Equal(2, slack.Posted.Count);
        Assert.Contains("Lunch and learn on Thursday", slack.Posted[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 250), slack.Posted[1].Text, StringComparison.Ordinal);

        // A channel the app is not in is kept as the last failure, and later posts are still tried.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/slack", new { channelId = "C0000000002" }));
        await PostAsync(t["laila"].Client, community, "Anyone for padel?");
        await DispatchAsync();
        var failed = (await OkAsync(admin.GetAsync($"{Api}/communities/{community}/mirrors"))).GetProperty("mirrors")[0];
        Assert.Equal("channel_unavailable", failed.GetProperty("lastFailure").GetString());

        // Removing the mirror stops it, and another tenant reaches nothing.
        var other = await world.CreateTenantAsync("mir-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/communities/{community}/mirrors")).StatusCode);
        Assert.Empty((await OkAsync(admin.DeleteAsync($"{Api}/communities/{community}/mirrors/slack"))).GetProperty("mirrors").EnumerateArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.EngagementMirrorRemoved, community.ToString());
        await PostAsync(t["laila"].Client, community, "Quiet now");
        await DispatchAsync();
        Assert.Equal(2, slack.Posted.Count);
    }

    [Fact]
    public async Task Only_open_internal_communities_can_be_mirrored_and_Workplace_has_no_mirroring()
    {
        var (t, _) = await TenantAsync();
        var admin = t["admin"].Client;
        var closed = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Board"), kind = "closed" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.False((await OkAsync(admin.GetAsync($"{Api}/communities/{closed}/mirrors"))).GetProperty("eligible").GetBoolean());
        Assert.Equal("conflict.engagement.mirror_not_allowed", await ProblemKeyAsync(admin.PutAsJsonAsync($"{Api}/communities/{closed}/mirrors/slack", new { channelId = "C0000000001" })));
        var unit = await world.CreateUnitAsync(t, "Finance", null, "department");
        var department = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Finance"), kind = "open", orgUnitId = unit }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        Assert.Equal("conflict.engagement.mirror_not_allowed", await ProblemKeyAsync(admin.PutAsJsonAsync($"{Api}/communities/{department}/mirrors/teams", new { channelId = TeamsChannel })));

        var (w, wc) = await TenantAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await w["admin"].Client.GetAsync($"{Api}/communities/{wc}/mirrors")).StatusCode);
    }

    [Fact]
    public async Task Teams_mirrors_start_a_thread_in_a_channel_where_the_bot_is_installed()
    {
        var (t, community) = await TenantAsync();
        var directory = await TeamsAsync(t);
        var admin = t["admin"].Client;
        Assert.True((await OkAsync(admin.GetAsync($"{Api}/communities/{community}/mirrors"))).GetProperty("teamsAvailable").GetBoolean());
        Assert.Equal("validation.engagement.teams_channel", await ProblemKeyAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/teams", new { channelId = "general" })));

        // Without the bot in the channel's team, the refusal is the last failure.
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/mirrors/teams", new { channelId = TeamsChannel, excerpt = true }));
        await PostAsync(t["laila"].Client, community, "Fire drill at eleven");
        await DispatchAsync();
        Assert.DoesNotContain(world.Factory.Graph.ChannelPosts, p => p.ChannelId == TeamsChannel && p.Directory == directory.ToString("D"));
        Assert.Equal("channel_unavailable", (await OkAsync(admin.GetAsync($"{Api}/communities/{community}/mirrors"))).GetProperty("mirrors")[0].GetProperty("lastFailure").GetString());

        world.Factory.Graph.InstallBotInChannel(directory, TeamsChannel);
        var post = await PostAsync(t["laila"].Client, community, "Fire drill moved to noon");
        await DispatchAsync();
        var card = Assert.Single(world.Factory.Graph.ChannelPosts, p => p.ChannelId == TeamsChannel && p.Directory == directory.ToString("D")).Card.GetRawText();
        Assert.Contains("posted in Town square", card, StringComparison.Ordinal);
        Assert.Contains("Fire drill moved to noon", card, StringComparison.Ordinal);
        Assert.Contains(post.ToString(), card, StringComparison.Ordinal);
        var mirror = (await OkAsync(admin.GetAsync($"{Api}/communities/{community}/mirrors"))).GetProperty("mirrors")[0];
        Assert.Equal(JsonValueKind.String, mirror.GetProperty("lastSentAt").ValueKind);
    }
}
