using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 T012, non-functional requirements: a course assigned to 20,000 people within 5 minutes, and the course player's
/// first lesson under 2 seconds p95. The people are written straight into the membership table, so the measurement covers
/// the rule as an administrator creates it. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Api = "/api/v1/learning";
    private const int Learners = 20_000;

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

    private static Dictionary<string, string> Both(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    [Fact]
    public async Task A_rule_assigns_a_course_to_20000_people_within_5_minutes_and_the_player_opens_under_2_seconds_p95()
    {
        var t = await world.CreateTenantAsync("lpf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "learner", MembershipTypes.Employee);
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into tenancy.tenant_membership (tenant_id, user_id, membership_type, status)
            select @t, gen_random_uuid(), 'employee', 'active' from generate_series(2, @n)
            """, ("t", t.Id), ("n", Learners));
        var admin = t["admin"].Client;
        var view = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            category = "Compliance",
            openEnrolment = false,
            definition = new
            {
                titles = Both("Code of conduct", "مدونة السلوك"),
                lessons = new object[]
                {
                    new
                    {
                        key = "check", kind = "quiz", titles = Both("Check", "اختبار"),
                        quiz = new
                        {
                            passMark = 100, attempts = 3,
                            questions = new object[]
                            {
                                new
                                {
                                    key = "q1", kind = "true_false", prompts = Both("Gifts from suppliers must be declared.", "يجب الإفصاح عن هدايا الموردين."),
                                    options = new object[] { new { key = "t", labels = Both("True", "صح"), correct = true }, new { key = "f", labels = Both("False", "خطأ"), correct = false } },
                                },
                            },
                        },
                    },
                },
            },
        }), HttpStatusCode.Created);
        var course = view.GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}:publish", new { revision = view.GetProperty("revision").GetInt32() }));

        var clock = Stopwatch.StartNew();
        var rule = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/rules", new { dueDays = 30 }), HttpStatusCode.Created);
        clock.Stop();
        var assigned = await world.Database.CountInTenantAsync(t.Id, "select count(*) from learning.assignment where course_id = @c and status = 'open'", ("c", course));
        output.WriteLine($"rule for {assigned} people in {clock.Elapsed.TotalSeconds:F1} s");
        Assert.True(assigned >= Learners, $"Only {assigned} people got the course");
        Assert.True(rule.GetProperty("covered").GetInt32() >= Learners);
        Assert.True(clock.Elapsed < TimeSpan.FromMinutes(5), $"Assigning took {clock.Elapsed.TotalSeconds:F1} s, over 5 minutes");

        var learner = t["learner"].Client;
        var url = $"{Api}/courses/{course}";
        for (var i = 0; i < 3; i++)
        {
            (await learner.GetAsync(url)).EnsureSuccessStatusCode();
        }
        var timings = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await learner.GetAsync(url);
            watch.Stop();
            response.EnsureSuccessStatusCode();
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        var page = await OkAsync(learner.GetAsync(url));
        Assert.NotEmpty(page.GetProperty("lessons").EnumerateArray());
        output.WriteLine($"player p95 {P95(timings):F1} ms");
        Assert.True(P95(timings) < 2000, $"Player p95 {P95(timings):F1} ms is over 2 seconds");

        // The 20,000 assignment events are dispatched here, so tests that drain the shared outbox a few batches at a
        // time are not left behind them.
        await world.DispatchTenantAsync(t.Id, 400);
    }
}
