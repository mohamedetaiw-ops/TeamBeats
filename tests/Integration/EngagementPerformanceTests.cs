using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T015, non-functional requirements: a community feed's first page under 800 ms p95 with 10,000 posts in the
/// community, and the results of an anonymous survey with 10,000 responses in under 10 seconds once it closes. Posts
/// and responses are written straight into the tables, so the measurements cover the endpoints as they run for users.
/// Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EngagementPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Api = "/api/v1/engagement";
    private const int Posts = 10_000;
    private const int Respondents = 10_000;

    private static double P95(List<double> timings)
    {
        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task SeedAsync(TestTenant t, string sql, object args) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await Sql.ExecAsync(session, sql, args);
            await session.CommitAsync();
            return true;
        });

    [Fact]
    public async Task A_community_feed_first_page_stays_under_800_ms_p95_at_10000_posts()
    {
        var t = await world.CreateTenantAsync("epf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "reader", MembershipTypes.Employee);
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = new { en = "Running club", ar = "نادي الجري" }, kind = "open" }), HttpStatusCode.Created);
        var community = created.GetProperty("id").GetGuid();
        var channel = created.GetProperty("channels")[0].GetProperty("id").GetGuid();
        var reader = t["reader"].Client;
        await OkAsync(reader.PostAsync($"{Api}/communities/{community}:join", null));
        var body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "Who is in for the Friday run?" } } } } };
        await OkAsync(reader.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body }), HttpStatusCode.Created);

        // The posts copy the body of the one written through the API; a few are pinned, as moderators do.
        await SeedAsync(t,
            """
            insert into engagement.post (tenant_id, id, community_id, channel_id, author_id, body, text, pinned, published_at)
            select platform.current_tenant_id(), gen_random_uuid(), @community, @channel, @author,
                   (select body from engagement.post where community_id = @community limit 1), 'Post ' || i, i % 2500 = 0,
                   now() - make_interval(mins => i)
            from generate_series(1, @n) as i;
            analyze engagement.post;
            """, new { community, channel, author = t["admin"].Id, n = Posts });
        Assert.True(await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.post where community_id = @c", ("c", community)) > Posts);

        var url = $"{Api}/communities/{community}/posts";
        for (var i = 0; i < 3; i++)
        {
            (await reader.GetAsync(url)).EnsureSuccessStatusCode();
        }
        var timings = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var clock = Stopwatch.StartNew();
            var response = await reader.GetAsync(url);
            clock.Stop();
            response.EnsureSuccessStatusCode();
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        var page = await OkAsync(reader.GetAsync(url));
        Assert.NotEmpty(page.GetProperty("items").EnumerateArray());
        output.WriteLine($"community feed p95 {P95(timings):F1} ms");
        Assert.True(P95(timings) < 800, $"Community feed p95 {P95(timings):F1} ms is over 800 ms");
    }

    [Fact]
    public async Task An_anonymous_survey_of_10000_respondents_aggregates_in_under_10_seconds()
    {
        var t = await world.CreateTenantAsync("eps-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var groups = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            groups.Add(await world.CreateUnitAsync(t, $"Team{i}", holding, "department"));
        }
        var admin = t["admin"].Client;
        var draft = await OkAsync(admin.PostAsJsonAsync($"{Api}/surveys", new
        {
            orgUnitId = holding,
            title = "How is your week going?",
            intro = "A short pulse check.",
            anonymous = true,
            threshold = (int?)null,
            closesAt = DateTimeOffset.UtcNow.AddDays(7),
            questions = new object[]
            {
                new { kind = "single", prompt = "Do you have what you need to do your job?", required = true, options = new[] { "Yes", "Partly", "No" } },
                new { kind = "multiple", prompt = "What helps you most?", required = false, options = new[] { "My manager", "My team", "Tools", "Training" } },
                new { kind = "scale", prompt = "How would you rate your workload?", required = false, options = Array.Empty<string>() },
                new { kind = "text", prompt = "What would make next week better?", required = false, options = Array.Empty<string>() },
            },
        }), HttpStatusCode.Created);
        var id = draft.GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsync($"{Api}/surveys/{id}:publish", null));

        // Ten thousand anonymous responses spread over ten teams: every one answers the single choice, most pick one or
        // two helps and a rating, and one in ten writes a line.
        await SeedAsync(t,
            """
            insert into engagement.survey_response (tenant_id, id, survey_id, group_unit_id)
            select platform.current_tenant_id(), md5('r' || i)::uuid, @id, (@groups::uuid[])[1 + i % 10] from generate_series(1, @n) as i;
            with q as (select id, kind, position from engagement.survey_question where survey_id = @id),
                 o as (select question_id, id, position from engagement.survey_option)
            insert into engagement.survey_answer (tenant_id, id, response_id, question_id, option_id, value, text)
            select platform.current_tenant_id(), gen_random_uuid(), md5('r' || i)::uuid, q.id, o.id, null::int, null::text
            from generate_series(1, @n) as i join q on q.kind = 'single' join o on o.question_id = q.id and o.position = i % 3
            union all
            select platform.current_tenant_id(), gen_random_uuid(), md5('r' || i)::uuid, q.id, o.id, null, null
            from generate_series(1, @n) as i join q on q.kind = 'multiple' join o on o.question_id = q.id and (o.position = i % 4 or o.position = (i + 1) % 4)
            where i % 5 <> 0
            union all
            select platform.current_tenant_id(), gen_random_uuid(), md5('r' || i)::uuid, q.id, null::uuid, 1 + i % 5, null::text
            from generate_series(1, @n) as i join q on q.kind = 'scale' where i % 7 <> 0
            union all
            select platform.current_tenant_id(), gen_random_uuid(), md5('r' || i)::uuid, q.id, null::uuid, null::int, 'More quiet time ' || i
            from generate_series(1, @n) as i join q on q.kind = 'text' where i % 10 = 0;
            analyze engagement.survey_response;
            analyze engagement.survey_answer;
            """, new { id, groups = groups.ToArray(), n = Respondents });
        await OkAsync(admin.PostAsync($"{Api}/surveys/{id}:close", null));

        var clock = Stopwatch.StartNew();
        var results = await OkAsync(admin.GetAsync($"{Api}/surveys/{id}/results"));
        clock.Stop();
        Assert.True(results.GetProperty("visible").GetBoolean());
        Assert.Equal(Respondents, results.GetProperty("responses").GetInt32());
        var single = results.GetProperty("questions").EnumerateArray().Single(q => q.GetProperty("kind").GetString() == "single");
        Assert.Equal(Respondents, single.GetProperty("options").EnumerateArray().Sum(o => o.GetProperty("count").GetInt32()));
        output.WriteLine($"survey results for {Respondents} respondents in {clock.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Survey results took {clock.Elapsed.TotalSeconds:F1} s");
    }
}
