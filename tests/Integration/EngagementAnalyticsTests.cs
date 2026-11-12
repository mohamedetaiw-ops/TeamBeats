using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 slice 12 (FR-024): engagement counts reach analytics. Named actions (post, comment, react, vote, recognise,
/// join) are counted like other module events, through the outbox with the person's pseudonymous key and unit.
/// Anonymous actions (an anonymous Speak Up post, a vote in an anonymous poll, a response to an anonymous survey) are
/// counted with no person, unit, location, locale or time finer than the day, and never pass through the outbox. The web
/// collector refuses engagement events, and nothing crosses tenants. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EngagementAnalyticsTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en };

    private Task<long> CountAsync(TestTenant t, string sql, params (string, object)[] args) => world.Database.CountInTenantAsync(t.Id, sql, args);

    private async Task DrainAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    /// <summary>An open community with a Speak Up channel that allows anonymous posts; Laila and Omar work in Plant.</summary>
    private async Task<(TestTenant T, Guid Community, Guid Channel)> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ea-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var plant = await world.CreateUnitAsync(t, "Plant", null, "subsidiary");
        foreach (var name in new[] { "laila", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Plant");
        }
        await world.AssignAsync(t, "laila", "organization_administrator", "org_subtree", plant);
        var admin = t["admin"].Client;
        var community = (await OkAsync(admin.PostAsJsonAsync($"{Api}/communities", new { names = Names("Safety"), kind = "open" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(admin.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t["laila"].Id }));
        var view = await OkAsync(admin.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "speak_up", names = Names("Speak up"), kind = "speak_up", allowAnonymous = true }), HttpStatusCode.Created);
        var channel = view.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "speak_up").GetProperty("id").GetGuid();
        return (t, community, channel);
    }

    private static object Poll(bool anonymous) => new
    {
        question = anonymous ? "Is the canteen clean enough?" : "Where shall we eat on Thursday?",
        options = new[] { "Yes", "No" },
        multiple = false, anonymous, allowChange = false, results = "after_vote", closesAt = DateTimeOffset.UtcNow.AddDays(3),
    };

    private static Guid FirstOption(JsonElement poll) => poll.GetProperty("options").EnumerateArray().First().GetProperty("id").GetGuid();

    [Fact]
    public async Task Named_actions_are_counted_with_the_persons_key_through_the_outbox()
    {
        var (t, community, _) = await SetupAsync();
        var laila = t["laila"].Client;
        var omar = t["omar"].Client;

        await OkAsync(omar.PostAsync($"{Api}/communities/{community}:join", null));
        var post = (await OkAsync(laila.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Fire drill on Monday") }), HttpStatusCode.Created))
            .GetProperty("post").GetProperty("id").GetGuid();
        await OkAsync(omar.PostAsJsonAsync($"{Api}/posts/{post}/comments", new { body = "Thanks" }), HttpStatusCode.Created);
        await OkAsync(omar.PutAsJsonAsync($"{Api}/posts/{post}/reaction", new { kind = "like" }));
        var poll = await OkAsync(laila.PostAsJsonAsync($"{Api}/communities/{community}/polls", Poll(anonymous: false)), HttpStatusCode.Created);
        await OkAsync(omar.PutAsJsonAsync($"{Api}/polls/{poll.GetProperty("id").GetGuid()}/vote", new { optionIds = new[] { FirstOption(poll) } }));

        Assert.True(await world.Database.OutboxCountAsync(nameof(AnalyticsEventEmitted), t.Id) >= 5);
        await DrainAsync();

        foreach (var action in new[] { "join", "post", "comment", "react", "vote" })
        {
            Assert.Equal(1, await CountAsync(t,
                "select count(*) from analytics.event where name = 'engagement.action' and fields->>'action' = @a and channel = 'server' and user_key is not null and org_unit_id = @u",
                ("a", action), ("u", t.Unit("Plant"))));
        }
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where fields->>'action' = 'post' and fields->>'communityId' = @c", ("c", community.ToString())));
        Assert.Equal(0, await CountAsync(t, "select count(*) from analytics.event where name = 'engagement.anonymous_action'"));
    }

    [Fact]
    public async Task Anonymous_actions_are_counted_with_no_person_and_only_the_day()
    {
        var (t, community, channel) = await SetupAsync();
        var laila = t["laila"].Client;
        var omar = t["omar"].Client;
        await OkAsync(omar.PostAsync($"{Api}/communities/{community}:join", null));
        await DrainAsync();
        var outboxBefore = await world.Database.OutboxCountAsync(nameof(AnalyticsEventEmitted), t.Id);

        await OkAsync(omar.PostAsJsonAsync($"{Api}/communities/{community}/anonymous-posts", new { channelId = channel, body = Body("The fire exit is blocked") }));
        var poll = await OkAsync(laila.PostAsJsonAsync($"{Api}/communities/{community}/polls", Poll(anonymous: true)), HttpStatusCode.Created);
        await OkAsync(omar.PutAsJsonAsync($"{Api}/polls/{poll.GetProperty("id").GetGuid()}/vote", new { optionIds = new[] { FirstOption(poll) } }));

        var draft = await OkAsync(laila.PostAsJsonAsync($"{Api}/surveys", new
        {
            orgUnitId = t.Unit("Plant"), title = "How is your week going?", intro = "A short pulse check.", anonymous = true, threshold = (int?)null,
            closesAt = DateTimeOffset.UtcNow.AddDays(7),
            questions = new object[] { new { kind = "scale", prompt = "How would you rate your workload?", required = true, options = Array.Empty<string>() } },
        }), HttpStatusCode.Created);
        var survey = await OkAsync(laila.PostAsync($"{Api}/surveys/{draft.GetProperty("id").GetGuid()}:publish", null));
        var question = survey.GetProperty("questions").EnumerateArray().Single().GetProperty("id").GetGuid();
        await OkAsync(omar.PostAsJsonAsync($"{Api}/surveys/{survey.GetProperty("id").GetGuid()}/responses",
            new { answers = new[] { new { questionId = question, value = 4 } } }));

        // Written at once, never queued: no outbox message carries the request's correlation or exact time.
        Assert.Equal(outboxBefore, await world.Database.OutboxCountAsync(nameof(AnalyticsEventEmitted), t.Id));
        foreach (var action in new[] { "speak_up_post", "poll_vote", "survey_response" })
        {
            Assert.Equal(1, await CountAsync(t,
                "select count(*) from analytics.event where name = 'engagement.anonymous_action' and fields->>'action' = @a", ("a", action)));
        }
        Assert.Equal(3, await CountAsync(t,
            "select count(*) from analytics.event where name = 'engagement.anonymous_action' and user_key is null and org_unit_id is null and location is null and locale is null"
            + " and occurred_at = date_trunc('day', occurred_at at time zone 'UTC') at time zone 'UTC' and received_at = date_trunc('day', received_at at time zone 'UTC') at time zone 'UTC'"
            + " and fields - 'action' = '{}'::jsonb"));

        // Omar's named join is the only engagement row with his unit; nothing named appears for the anonymous actions.
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where name like 'engagement.%' and user_key is not null"));
    }

    [Fact]
    public async Task The_web_cannot_send_engagement_events_and_other_tenants_see_none()
    {
        var (t, community, channel) = await SetupAsync();
        var (other, _, _) = await SetupAsync();
        await OkAsync(t["omar"].Client.PostAsync($"{Api}/communities/{community}:join", null));
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/communities/{community}/anonymous-posts", new { channelId = channel, body = Body("Quietly") }));
        await DrainAsync();

        await OkAsync(t["laila"].Client.PostAsJsonAsync("/api/v1/analytics/events", new
        {
            events = new object[]
            {
                new { name = AnalyticsEvents.EngagementAction, fields = new { action = "post" } },
                new { name = AnalyticsEvents.EngagementAnonymousAction, fields = new { action = "speak_up_post" } },
            },
        }), HttpStatusCode.Accepted);

        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where name = 'engagement.action'"));
        Assert.Equal(1, await CountAsync(t, "select count(*) from analytics.event where name = 'engagement.anonymous_action'"));
        Assert.Equal(0, await CountAsync(t, "select count(*) from analytics.event where name like 'engagement.%' and channel <> 'server'"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.Id, "select count(*) from analytics.event where name like 'engagement.%'"));
    }
}
