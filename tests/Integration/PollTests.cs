using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T007 for polls (US3 scenario 1, FR-008, edge case "poll edited after votes"): one vote per person, changes
/// only where allowed, results after voting or after close, anonymous polls that count votes with nobody named and drop
/// the "has voted" marks at close, options that can only be added, and closing at the end time or by the author or a
/// moderator. Polls come with communities, so Workplace has them. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PollTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

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

    private async Task<(TestTenant T, Guid Community)> SetupAsync(string kind = "open")
    {
        var t = await world.CreateTenantAsync("pl-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        foreach (var name in new[] { "laila", "omar", "kofi", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = new Dictionary<string, string> { ["en"] = "Lunch club" }, kind }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var name in new[] { "laila", "omar", "kofi" })
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id }));
        }
        return (t, community);
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid community, object poll) => client.PostAsJsonAsync($"{Api}/communities/{community}/polls", poll);

    private static object Lunch(bool multiple = false, bool anonymous = false, bool allowChange = false, string results = "after_vote") => new
    {
        question = "Where shall we eat on Thursday?",
        options = new[] { "Falafel corner", "Sushi bar", "Pizza place" },
        multiple, anonymous, allowChange, results, closesAt = DateTimeOffset.UtcNow.AddDays(3),
    };

    private static Guid Option(JsonElement poll, string label) =>
        poll.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("label").GetString() == label).GetProperty("id").GetGuid();

    private static int? Count(JsonElement poll, string label)
    {
        var count = poll.GetProperty("options").EnumerateArray().Single(o => o.GetProperty("label").GetString() == label).GetProperty("count");
        return count.ValueKind == JsonValueKind.Null ? null : count.GetInt32();
    }

    private static Task<HttpResponseMessage> VoteAsync(HttpClient client, Guid poll, params Guid[] optionIds) =>
        client.PutAsJsonAsync($"{Api}/polls/{poll}/vote", new { optionIds });

    [Fact]
    public async Task Each_person_votes_once_and_sees_results_after_voting()
    {
        var (t, community) = await SetupAsync();
        var poll = await OkAsync(CreateAsync(t["laila"].Client, community, Lunch()), HttpStatusCode.Created);
        var id = poll.GetProperty("id").GetGuid();
        var falafel = Option(poll, "Falafel corner");
        var sushi = Option(poll, "Sushi bar");

        var before = await OkAsync(t["omar"].Client.GetAsync($"{Api}/polls/{id}"));
        Assert.False(before.GetProperty("resultsVisible").GetBoolean());
        Assert.Null(Count(before, "Falafel corner"));
        Assert.True(before.GetProperty("canVote").GetBoolean());

        Assert.Contains("validation.engagement.poll_choice", await ErrorKeysAsync(await VoteAsync(t["omar"].Client, id, falafel, sushi)));
        Assert.Contains("validation.engagement.poll_choice", await ErrorKeysAsync(await VoteAsync(t["omar"].Client, id, Guid.NewGuid())));
        var after = await OkAsync(VoteAsync(t["omar"].Client, id, falafel));
        Assert.True(after.GetProperty("resultsVisible").GetBoolean());
        Assert.Equal(1, Count(after, "Falafel corner"));
        Assert.Equal(falafel, Assert.Single(after.GetProperty("myChoices").EnumerateArray()).GetGuid());
        Assert.False(after.GetProperty("canVote").GetBoolean());
        // Readers see counts, not names; the author sees who voted for what in a named poll.
        Assert.Equal(JsonValueKind.Null, after.GetProperty("options")[0].GetProperty("voters").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await VoteAsync(t["omar"].Client, id, sushi)).StatusCode);
        await OkAsync(VoteAsync(t["kofi"].Client, id, sushi));
        var authorView = await OkAsync(t["laila"].Client.GetAsync($"{Api}/polls/{id}"));
        Assert.Equal(2, authorView.GetProperty("voters").GetInt32());
        Assert.Equal(t["omar"].Id, Assert.Single(authorView.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("id").GetGuid() == falafel).GetProperty("voters").EnumerateArray()).GetProperty("id").GetGuid());

        // Where the poll allows it, a vote changes until close.
        var changing = await OkAsync(CreateAsync(t["laila"].Client, community, Lunch(allowChange: true)), HttpStatusCode.Created);
        var changingId = changing.GetProperty("id").GetGuid();
        await OkAsync(VoteAsync(t["omar"].Client, changingId, Option(changing, "Pizza place")));
        var changed = await OkAsync(VoteAsync(t["omar"].Client, changingId, Option(changing, "Sushi bar")));
        Assert.Equal(0, Count(changed, "Pizza place"));
        Assert.Equal(1, Count(changed, "Sushi bar"));
        Assert.True(changed.GetProperty("canVote").GetBoolean());

        // Results after close stay hidden from voters until it closes; the author closes early.
        var later = await OkAsync(CreateAsync(t["laila"].Client, community, Lunch(multiple: true, results: "after_close")), HttpStatusCode.Created);
        var laterId = later.GetProperty("id").GetGuid();
        var voted = await OkAsync(VoteAsync(t["omar"].Client, laterId, Option(later, "Falafel corner"), Option(later, "Pizza place")));
        Assert.False(voted.GetProperty("resultsVisible").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.PostAsync($"{Api}/polls/{laterId}:close", null)).StatusCode);
        var closed = await OkAsync(t["laila"].Client.PostAsync($"{Api}/polls/{laterId}:close", null));
        Assert.True(closed.GetProperty("closed").GetBoolean());
        var shown = await OkAsync(t["omar"].Client.GetAsync($"{Api}/polls/{laterId}"));
        Assert.True(shown.GetProperty("resultsVisible").GetBoolean());
        Assert.Equal(1, Count(shown, "Pizza place"));
        Assert.Equal(HttpStatusCode.Conflict, (await VoteAsync(t["kofi"].Client, laterId, Option(later, "Sushi bar"))).StatusCode);
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(TeamBeats.Modules.Engagement.Contracts.PollClosed), t.Id));
    }

    [Fact]
    public async Task An_anonymous_poll_counts_votes_with_nobody_named_and_drops_the_marks_at_close()
    {
        var (t, community) = await SetupAsync();
        Assert.Contains("validation.engagement.poll_anonymous_change", await ErrorKeysAsync(await CreateAsync(t["laila"].Client, community, Lunch(anonymous: true, allowChange: true))));
        var poll = await OkAsync(CreateAsync(t["laila"].Client, community, Lunch(anonymous: true)), HttpStatusCode.Created);
        var id = poll.GetProperty("id").GetGuid();
        await OkAsync(VoteAsync(t["omar"].Client, id, Option(poll, "Sushi bar")));
        var mine = await OkAsync(VoteAsync(t["kofi"].Client, id, Option(poll, "Sushi bar")));
        Assert.True(mine.GetProperty("voted").GetBoolean());
        Assert.Empty(mine.GetProperty("myChoices").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await VoteAsync(t["kofi"].Client, id, Option(poll, "Pizza place"))).StatusCode);

        // The counts have no user column; the marks name people but not their choice.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from information_schema.columns where table_schema = 'engagement' and table_name = 'poll_tally' and column_name like '%user%'"));
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count from engagement.poll_tally where option_id = @o", ("o", Option(poll, "Sushi bar"))));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.poll_vote where poll_id = @p", ("p", id)));
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.poll_voted where poll_id = @p", ("p", id)));

        // Even the author sees counts but no voters.
        var author = await OkAsync(t["laila"].Client.GetAsync($"{Api}/polls/{id}"));
        Assert.Equal(2, Count(author, "Sushi bar"));
        Assert.All(author.GetProperty("options").EnumerateArray(), o => Assert.Equal(JsonValueKind.Null, o.GetProperty("voters").ValueKind));

        // A moderator closes it; the marks go, the counts stay.
        var closed = await OkAsync(t["admin"].Client.PostAsync($"{Api}/polls/{id}:close", null));
        Assert.True(closed.GetProperty("closed").GetBoolean());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.poll_voted where poll_id = @p", ("p", id)));
        Assert.Equal(2, Count(await OkAsync(t["mona"].Client.GetAsync($"{Api}/polls/{id}")), "Sushi bar"));
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsync($"{Api}/polls/{id}:close", null)).StatusCode);
    }

    [Fact]
    public async Task A_poll_closes_at_its_end_time_and_options_can_only_be_added()
    {
        var (t, community) = await SetupAsync();
        var poll = await OkAsync(CreateAsync(t["laila"].Client, community, Lunch(anonymous: true)), HttpStatusCode.Created);
        var id = poll.GetProperty("id").GetGuid();
        await OkAsync(VoteAsync(t["omar"].Client, id, Option(poll, "Falafel corner")));

        // Options are added after the first vote; the existing ones stay as they were.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PostAsJsonAsync($"{Api}/polls/{id}/options", new { options = new[] { "Burgers" } })).StatusCode);
        Assert.Contains("validation.engagement.poll_option_repeated", await ErrorKeysAsync(await t["laila"].Client.PostAsJsonAsync($"{Api}/polls/{id}/options",
            new { options = new[] { "sushi bar" } })));
        var added = await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/polls/{id}/options", new { options = new[] { "Burgers" } }));
        Assert.Equal(new[] { "Falafel corner", "Sushi bar", "Pizza place", "Burgers" }, added.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()!).ToArray());
        Assert.Equal(1, Count(added, "Falafel corner"));
        await OkAsync(VoteAsync(t["kofi"].Client, id, Option(added, "Burgers")));

        // Past its end time the poll is closed the next time anyone opens it, and the marks are deleted.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update engagement.poll set closes_at = now() - interval '1 minute', created_at = now() - interval '1 day' where id = @id", ("id", id));
        var list = await OkAsync(t["omar"].Client.GetAsync($"{Api}/communities/{community}/polls"));
        var listed = list.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
        Assert.True(listed.GetProperty("closed").GetBoolean());
        Assert.False(listed.GetProperty("canVote").GetBoolean());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.poll_voted where poll_id = @p", ("p", id)));
        Assert.Equal(HttpStatusCode.Conflict, (await VoteAsync(t["mona"].Client, id, Option(added, "Burgers"))).StatusCode);

        // Questions, options and end times are checked.
        var bad = await CreateAsync(t["laila"].Client, community, new { question = "", options = new[] { "Only one" }, results = "never", closesAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var keys = await ErrorKeysAsync(bad);
        Assert.Contains("validation.engagement.poll_question", keys);
        Assert.Contains("validation.engagement.poll_options", keys);
        Assert.Contains("validation.engagement.poll_results", keys);
        Assert.Contains("validation.engagement.poll_closes", keys);
    }

    [Fact]
    public async Task Polls_stay_inside_their_community_and_tenant()
    {
        var (t, community) = await SetupAsync("closed");
        var poll = await OkAsync(CreateAsync(t["laila"].Client, community, Lunch()), HttpStatusCode.Created);
        var id = poll.GetProperty("id").GetGuid();
        // Mona is not a member of the closed community: she cannot see, vote or start a poll.
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Api}/polls/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await VoteAsync(t["mona"].Client, id, Option(poll, "Sushi bar"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(t["mona"].Client, community, Lunch())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Api}/communities/{community}/polls")).StatusCode);

        var (other, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/polls/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await VoteAsync(other["laila"].Client, id, Option(poll, "Sushi bar"))).StatusCode);
    }
}
