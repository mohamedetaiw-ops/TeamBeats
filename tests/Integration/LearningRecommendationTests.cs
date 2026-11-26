using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 R3 T014 (US9, FR-024, FR-025): courses are tagged with skills from the spec 010 list; My learning
/// recommends open, targeted courses tagged with skills the learner declared below expert, more matching skills
/// first, never ones they have started, and never again once dismissed. With skills off nothing is recommended and
/// tags stay as they were; unknown skills are dropped; another tenant reaches nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningRecommendationTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";

    private static Dictionary<string, string> En(string text) => new() { ["en"] = text };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> SetSkillsAsync(HttpClient admin, object body)
    {
        var current = await admin.GetAsync("/api/v1/people/skills");
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/people/skills") { Content = JsonContent.Create(body) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        return await OkAsync(admin.SendAsync(put));
    }

    private static Guid SkillId(JsonElement list, string en) =>
        list.GetProperty("skills").EnumerateArray().Single(s => s.GetProperty("names").GetProperty("en").GetString() == en).GetProperty("id").GetGuid();

    private static object Lesson() => new
    {
        kind = "quiz", titles = En("Check"),
        quiz = new { questions = new object[] { new { kind = "true_false", prompts = En("Fictional"), options = new object[] { new { labels = En("True"), correct = true }, new { labels = En("False"), correct = false } } } } },
    };

    private static async Task<Guid> PublishedAsync(HttpClient admin, string title, Guid[] skills, bool open = true)
    {
        var created = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            openEnrolment = open, skills, definition = new { titles = En(title), lessons = new[] { Lesson() } },
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{id}:publish", new { revision = created.GetProperty("revision").GetInt32(), requiresRetraining = false }));
        return id;
    }

    private static async Task<string[]> RecommendedAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync($"{Api}/recommendations"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("titles").GetProperty("en").GetString()!).ToArray();

    [Fact]
    public async Task Courses_for_skills_below_expert_are_recommended_until_started_or_dismissed()
    {
        var t = await world.CreateTenantAsync("lrc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "laila", Modules.Tenancy.Contracts.MembershipTypes.Employee);
        var admin = t["admin"].Client;
        var list = await SetSkillsAsync(admin, new { skills = new[] { new { names = En("Data analysis") }, new { names = En("Excel") }, new { names = En("Arabic writing") } } });
        var (data, excel, writing) = (SkillId(list, "Data analysis"), SkillId(list, "Excel"), SkillId(list, "Arabic writing"));
        var laila = t["laila"].Client;
        await OkAsync(laila.PutAsJsonAsync("/api/v1/me/skills", new
        {
            skills = new object[]
            {
                new { skillId = data, proficiency = "basic" }, new { skillId = excel, proficiency = "good" }, new { skillId = writing, proficiency = "expert" },
            },
        }));

        // Tags: unknown skills are dropped.
        var both = await PublishedAsync(admin, "Data with Excel", [data, excel, Guid.NewGuid()]);
        var detail = await OkAsync(admin.GetAsync($"{Api}/manage/courses/{both}"));
        Assert.Equal(2, detail.GetProperty("skills").GetArrayLength());
        await PublishedAsync(admin, "Excel tips", [excel]);
        await PublishedAsync(admin, "Data basics", [data]);
        await PublishedAsync(admin, "Writing well", [writing]);
        await PublishedAsync(admin, "Closed data course", [data], open: false);
        await PublishedAsync(admin, "Untagged", []);

        // Two matching skills first, then a basic skill before a good one; expert skills and closed courses are left out.
        var recommended = await OkAsync(laila.GetAsync($"{Api}/recommendations"));
        Assert.True(recommended.GetProperty("available").GetBoolean());
        Assert.Equal(["Data with Excel", "Data basics", "Excel tips"], await RecommendedAsync(laila));
        Assert.Equal(2, recommended.GetProperty("items")[0].GetProperty("skills").GetArrayLength());

        // Started courses and dismissed ones go.
        await OkAsync(laila.PostAsync($"{Api}/courses/{both}:enrol", null));
        var basics = recommended.GetProperty("items")[1].GetProperty("id").GetGuid();
        await OkAsync(laila.PostAsync($"{Api}/recommendations/{basics}:dismiss", null), HttpStatusCode.NoContent);
        await OkAsync(laila.PostAsync($"{Api}/recommendations/{basics}:dismiss", null), HttpStatusCode.NoContent);
        Assert.Equal(["Excel tips"], await RecommendedAsync(laila));
        Assert.Equal(HttpStatusCode.NotFound, (await laila.PostAsync($"{Api}/recommendations/{Guid.NewGuid()}:dismiss", null)).StatusCode);

        // Skills off: nothing is recommended, and saving a course keeps its tags.
        await SetSkillsAsync(admin, new { enabled = false });
        var off = await OkAsync(laila.GetAsync($"{Api}/recommendations"));
        Assert.False(off.GetProperty("available").GetBoolean());
        Assert.Empty(off.GetProperty("items").EnumerateArray());
        var kept = await OkAsync(admin.PutAsJsonAsync($"{Api}/manage/courses/{both}", new
        {
            revision = detail.GetProperty("revision").GetInt32() + 0, openEnrolment = true, skills = Array.Empty<Guid>(),
            definition = new { titles = En("Data with Excel"), lessons = new[] { Lesson() } },
        }));
        Assert.Equal(2, kept.GetProperty("skills").GetArrayLength());

        // Another tenant cannot dismiss this tenant's course.
        var other = await world.CreateTenantAsync("lrc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Api}/recommendations/{both}:dismiss", null)).StatusCode);
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync($"{Api}/recommendations"))).GetProperty("items").EnumerateArray());
    }
}
