using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 R3 T013 (US8, FR-021 to FR-023): a learning administrator asks for quiz questions drafted from the text of
/// the course's own page lessons; the questions arrive in every language of the course marked as AI drafts, a broken
/// question is dropped, publishing waits until a person keeps each one, a gateway outage changes nothing, a course
/// with no lesson text is refused before any model call, and people outside the course or the tenant reach nothing.
/// Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningQuizDraftTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";
    private const string Marker = "Use a long passphrase of four words.";

    private static Dictionary<string, string> Both(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("detailKey", out var key))
        {
            return key.GetString();
        }
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString())).FirstOrDefault()
            : null;
    }

    private async Task<(TestTenant T, Guid Gulf, Guid Page)> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("qd-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var gulf = await world.CreateUnitAsync(t, "Gulf", null, "organization");
        foreach (var name in new[] { "mona", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        if (package == "workplace")
        {
            return (t, gulf, Guid.Empty);
        }
        await world.AssignAsync(t, "mona", BuiltInRoles.LearningAdministrator, "org_subtree", gulf);
        var admin = t["admin"].Client;
        var page = (await OkAsync(admin.PostAsJsonAsync("/api/v1/content/items", new
        {
            type = "page",
            ownerId = t["admin"].Id,
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "Passwords", body = Body(Marker + " [fake:badquestion]") },
                ["ar"] = new { title = "كلمات المرور", body = Body("استخدم عبارة مرور طويلة.") },
            },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/content/items/{page}/publish", new { }));
        return (t, gulf, page);
    }

    private static object Definition(params object[] lessons) => new { titles = Both("Information security", "أمن المعلومات"), lessons };

    [Fact]
    public async Task Drafted_questions_come_marked_in_both_languages_and_wait_for_a_person_before_publishing()
    {
        var (t, gulf, page) = await SetupAsync();
        var mona = t["mona"].Client;
        var created = await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            orgUnitId = gulf,
            definition = Definition(new { key = "passwords", kind = "page", titles = Both("Passwords", "كلمات المرور"), sources = new { en = page, ar = page } }),
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var revision = created.GetProperty("revision").GetInt32();

        Assert.Equal("validation.learning.quiz_draft_count",
            await ProblemKeyAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, count = 21 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.learning.quiz_draft_lesson",
            await ProblemKeyAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, lessonKey = "passwords" }), HttpStatusCode.BadRequest));
        Assert.Equal("conflict.learning.course_revision",
            await ProblemKeyAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision = revision + 5 }), HttpStatusCode.Conflict));

        var drafted = await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, count = 4 }));
        Assert.Equal("drafted", drafted.GetProperty("outcome").GetString());
        Assert.Equal(4, drafted.GetProperty("added").GetInt32());
        // The lesson text went to the model as data; the broken question was dropped.
        Assert.Contains(world.Factory.OpenRouter.UserPrompts, p => p.Contains(Marker, StringComparison.Ordinal));
        var course = drafted.GetProperty("course");
        var quiz = course.GetProperty("draft").GetProperty("lessons")[1];
        Assert.Equal("quiz", quiz.GetProperty("kind").GetString());
        Assert.Equal("Check what you learned", quiz.GetProperty("titles").GetProperty("en").GetString());
        var questions = quiz.GetProperty("quiz").GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(["Fictional question 1", "Fictional question 2", "Fictional question 3", "Fictional question 4"],
            questions.Select(q => q.GetProperty("prompts").GetProperty("en").GetString()));
        Assert.All(questions, q => Assert.True(q.GetProperty("aiDraft").GetBoolean()));
        Assert.All(questions, q => Assert.StartsWith("سؤال تجريبي", q.GetProperty("prompts").GetProperty("ar").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.LearningQuizDrafted);

        // More questions go into the named quiz lesson.
        revision = course.GetProperty("revision").GetInt32();
        var quizKey = quiz.GetProperty("key").GetString();
        course = (await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, lessonKey = quizKey, count = 2 }))).GetProperty("course");
        Assert.Equal(6, course.GetProperty("draft").GetProperty("lessons")[1].GetProperty("quiz").GetProperty("questions").GetArrayLength());

        // Publishing waits until each AI draft is kept, edited or removed.
        revision = course.GetProperty("revision").GetInt32();
        Assert.Equal("validation.learning.quiz_unreviewed", await ProblemKeyAsync(
            mona.PostAsJsonAsync($"{Api}/manage/courses/{id}:publish", new { revision, requiresRetraining = false }), HttpStatusCode.BadRequest));

        // An outage changes nothing and says why.
        world.Factory.OpenRouter.Unavailable = true;
        try
        {
            var down = await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, count = 2 }));
            Assert.Equal("unavailable", down.GetProperty("outcome").GetString());
            Assert.Equal(0, down.GetProperty("added").GetInt32());
            Assert.Equal(revision, down.GetProperty("course").GetProperty("revision").GetInt32());
        }
        finally
        {
            world.Factory.OpenRouter.Unavailable = false;
        }

        // Keeping every question takes the mark away, and the course publishes.
        var definition = JsonNode.Parse(course.GetProperty("draft").GetRawText())!;
        foreach (var question in definition["lessons"]![1]!["quiz"]!["questions"]!.AsArray())
        {
            question!["aiDraft"] = false;
        }
        var kept = await OkAsync(mona.PutAsJsonAsync($"{Api}/manage/courses/{id}", new { revision, orgUnitId = gulf, openEnrolment = false, definition }));
        await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}:publish", new { revision = kept.GetProperty("revision").GetInt32(), requiresRetraining = false }));

        // Someone who does not manage the course, and another tenant, reach nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, count = 2 })).StatusCode);
        var other = await world.CreateTenantAsync("qd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Api}/manage/courses/{id}/quiz-drafts", new { revision, count = 2 })).StatusCode);
    }

    [Fact]
    public async Task A_course_without_lesson_text_is_refused_before_any_model_call_and_Workplace_has_no_courses()
    {
        var (t, gulf, _) = await SetupAsync();
        var mona = t["mona"].Client;
        var created = await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            orgUnitId = gulf,
            definition = Definition(new
            {
                kind = "quiz", titles = Both("Quiz", "اختبار"),
                quiz = new
                {
                    questions = new object[]
                    {
                        new
                        {
                            kind = "true_false", prompts = Both("Fictional prompt", "سؤال"),
                            options = new object[] { new { labels = Both("True", "صح"), correct = true }, new { labels = Both("False", "خطأ"), correct = false } },
                        },
                    },
                },
            }),
        }), HttpStatusCode.Created);
        var before = world.Factory.OpenRouter.Requests;
        Assert.Equal("conflict.learning.quiz_draft_no_text", await ProblemKeyAsync(mona.PostAsJsonAsync(
            $"{Api}/manage/courses/{created.GetProperty("id").GetGuid()}/quiz-drafts", new { revision = created.GetProperty("revision").GetInt32() }), HttpStatusCode.Conflict));
        Assert.Equal(before, world.Factory.OpenRouter.Requests);

        var (w, _, _) = await SetupAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await w["admin"].Client.PostAsJsonAsync($"{Api}/manage/courses/{Guid.NewGuid()}/quiz-drafts", new { revision = 1 })).StatusCode);
    }
}
