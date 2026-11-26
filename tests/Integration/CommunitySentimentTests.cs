using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 R3 T020 (US11 scenarios 2 and 3, FR-032, FR-033): a community's sentiment by week from spec 021 sentiment.
/// Comments and room messages count; an anonymous Speak Up post counts only after an engagement administrator lets AI
/// read anonymous content, and then with nothing about its author. Weeks with fewer people than the minimum group size
/// come without counts. Only moderators and owners see it; another tenant gets not found. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CommunitySentimentTests(TenantWorld world)
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

    private Task ClassifyAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<SentimentClassifier>().RunAsync(200, CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return 0;
        });

    private static async Task TurnSentimentOnAsync(TestTenant t)
    {
        var policy = await OkAsync(t["admin"].Client.GetAsync("/api/v1/analytics/policy"));
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/analytics/policy", new { sentiment = true, version = policy.GetProperty("version").GetInt32() }));
    }

    private static async Task<JsonElement?> WeekAsync(TestTenant t, Guid community)
    {
        var view = await OkAsync(t["admin"].Client.GetAsync($"{Api}/communities/{community}/sentiment"));
        Assert.True(view.GetProperty("enabled").GetBoolean());
        Assert.Equal(5, view.GetProperty("minGroupSize").GetInt32());
        var weeks = view.GetProperty("weeks").EnumerateArray().ToList();
        return weeks.Count == 0 ? null : Assert.Single(weeks);
    }

    [Fact]
    public async Task Moderators_see_their_community_sentiment_by_week_above_the_group_size()
    {
        var t = await world.CreateTenantAsync("cs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var people = new[] { "p1", "p2", "p3", "p4", "p5" };
        foreach (var name in people)
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var admin = t["admin"].Client;
        var community = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Night shift"), kind = "open" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        foreach (var name in people)
        {
            await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id }));
        }

        // Sentiment is off for the tenant until its administrator turns it on (spec 021).
        Assert.False((await OkAsync(admin.GetAsync($"{Api}/communities/{community}/sentiment"))).GetProperty("enabled").GetBoolean());
        await TurnSentimentOnAsync(t);

        var post = (await OkAsync(t["p1"].Client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("New rota") }), HttpStatusCode.Created))
            .GetProperty("post").GetProperty("id").GetGuid();
        foreach (var name in new[] { "p1", "p2", "p3" })
        {
            await OkAsync(t[name].Client.PostAsJsonAsync($"{Api}/posts/{post}/comments", new { body = "[fake:positive] Much better" }), HttpStatusCode.Created);
        }
        var room = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities/{community}/rooms", new { names = Names("Handover") }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["p4"].Client.PostAsJsonAsync($"{Api}/rooms/{room}/messages", new { body = "[fake:negative] The lift is broken again" }), HttpStatusCode.Created);
        await ClassifyAsync(t);

        // Four people are fewer than the minimum group size of five: the week shows no counts.
        var week = await WeekAsync(t, community);
        Assert.Equal(JsonValueKind.Null, week!.Value.GetProperty("texts").ValueKind);

        await OkAsync(t["p5"].Client.PostAsJsonAsync($"{Api}/rooms/{room}/messages", new { body = "[fake:negative] Still waiting on parts" }), HttpStatusCode.Created);
        await ClassifyAsync(t);
        week = await WeekAsync(t, community);
        Assert.Equal(5, week!.Value.GetProperty("texts").GetInt32());
        Assert.Equal(3, week.Value.GetProperty("positive").GetInt32());
        Assert.Equal(2, week.Value.GetProperty("negative").GetInt32());

        // A member who does not moderate is refused; another tenant does not find the community.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["p2"].Client.GetAsync($"{Api}/communities/{community}/sentiment")).StatusCode);
        var other = await world.CreateTenantAsync("cs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/communities/{community}/sentiment")).StatusCode);

        // An anonymous Speak Up post waits until AI may read anonymous content, and then counts without its author.
        var channel = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "speak_up", names = Names("Speak up"), kind = "speak_up", allowAnonymous = true }), HttpStatusCode.Created))
            .GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "speak_up").GetProperty("id").GetGuid();
        await OkAsync(t["p2"].Client.PostAsJsonAsync($"{Api}/communities/{community}/anonymous-posts", new { channelId = channel, body = Body("[fake:negative] Night breaks are too short") }));
        var item = (await OkAsync(admin.GetAsync($"{Api}/moderation"))).EnumerateArray().Single(i => i.GetProperty("type").GetString() == "anonymous");
        await OkAsync(admin.PostAsJsonAsync($"{Api}/moderation/anonymous/{item.GetProperty("id").GetGuid()}:publish", new { }));
        await ClassifyAsync(t);
        Assert.Equal(5, (await WeekAsync(t, community))!.Value.GetProperty("texts").GetInt32());

        var current = await admin.GetAsync($"{Api}/settings/ai");
        var put = new HttpRequestMessage(HttpMethod.Put, $"{Api}/settings/ai") { Content = JsonContent.Create(new { anonymousContent = true }) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        await OkAsync(admin.SendAsync(put));
        await ClassifyAsync(t);
        week = await WeekAsync(t, community);
        Assert.Equal(6, week!.Value.GetProperty("texts").GetInt32());
        Assert.Equal(3, week.Value.GetProperty("negative").GetInt32());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from analytics.sentiment where source = 'speak_up' and contributor is null and org_unit_id is null and location is null"));
    }
}
