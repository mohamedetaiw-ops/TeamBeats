using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 slice 1 (US1, US3, FR-001 to FR-004, FR-009, FR-010, FR-012, FR-018, FR-019): learning administrators build
/// courses from referenced pages, videos and quizzes in their scope and publish them as versions; learners in the
/// audience enrol, finish lessons, take quizzes within their attempts, resume where they stopped and get a certificate
/// that people who report on them can verify; retraining versions make earlier completions stop counting; paths keep
/// their courses in order; other tenants and units see nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningCourseTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Africa, Guid Page, Guid Video, Guid Pdf);

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

    private static async Task<string?> DetailKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : null;
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static Dictionary<string, string> Both(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    /// <summary>
    /// Holding over Gulf and Africa. Mona is the learning administrator for Gulf, Laila and Omar work in Gulf, Kofi in
    /// Africa, Khaled is the compliance officer for Gulf. A published page in both languages, and a video and a PDF in a
    /// tenant library, are the course material.
    /// </summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("lc-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "mona", "laila", "omar", "kofi", "khaled" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "mona", "laila", "omar", "khaled" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "kofi", "Africa");
        if (package == "workplace")
        {
            return new Org(t, gulf, africa, Guid.Empty, Guid.Empty, Guid.Empty);
        }
        await world.AssignAsync(t, "mona", BuiltInRoles.LearningAdministrator, "org_subtree", gulf);
        await world.AssignAsync(t, "khaled", BuiltInRoles.ComplianceOfficer, "org_subtree", gulf);

        var admin = t["admin"].Client;
        var page = (await OkAsync(admin.PostAsJsonAsync("/api/v1/content/items", new
        {
            type = "page",
            ownerId = t["admin"].Id,
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "Passwords", body = Body("Use a long passphrase.") },
                ["ar"] = new { title = "كلمات المرور", body = Body("استخدم عبارة مرور طويلة.") },
            },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/content/items/{page}/publish", new { }));
        var library = (await OkAsync(admin.PostAsJsonAsync("/api/v1/documents/libraries", new { names = Both("Training", "التدريب"), scope = "tenant" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        async Task<Guid> UploadAsync(string name, string content) =>
            (await OkAsync(admin.PostAsync($"/api/v1/documents/libraries/{library}/files?name={name}&publish=true", new ByteArrayContent(Encoding.UTF8.GetBytes(content))),
                HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var video = await UploadAsync("phishing.mp4", "a short video about phishing");
        var pdf = await UploadAsync("rules.pdf", "%PDF-1.4 the rules");
        return new Org(t, gulf, africa, page, video, pdf);
    }

    private static object Audience(Guid unit) => new { anyOf = new object[] { new { orgUnits = new object[] { new { id = unit } } } } };

    private static object Quiz(int passMark = 100, int attempts = 2) => new
    {
        passMark, attempts,
        questions = new object[]
        {
            new
            {
                key = "q1", kind = "single", prompts = Both("Which password is strongest?", "أي كلمة مرور أقوى؟"),
                options = new object[]
                {
                    new { key = "a", labels = Both("Summer2026", "صيف2026"), correct = false },
                    new { key = "b", labels = Both("A long passphrase", "عبارة مرور طويلة"), correct = true },
                },
            },
            new
            {
                key = "q2", kind = "true_false", prompts = Both("You may share your password with IT.", "يجوز مشاركة كلمة المرور مع فريق التقنية."),
                options = new object[]
                {
                    new { key = "t", labels = Both("True", "صح"), correct = false },
                    new { key = "f", labels = Both("False", "خطأ"), correct = true },
                },
            },
        },
    };

    private static object Definition(Org o, params object[] lessons) => new
    {
        titles = Both("Information security basics", "أساسيات أمن المعلومات"),
        summaries = Both("Passwords and phishing.", "كلمات المرور والتصيد."),
        minutes = 20,
        lessons,
    };

    private static object PageLesson(Org o, string key = "passwords") => new { key, kind = "page", titles = Both("Passwords", "كلمات المرور"), sources = new { en = o.Page, ar = o.Page } };

    private static object VideoLesson(Guid source) => new { key = "phishing", kind = "video", titles = Both("Phishing", "التصيد"), sources = new { en = source, ar = source } };

    private static object QuizLesson(int passMark = 100, int attempts = 2) => new { key = "check", kind = "quiz", titles = Both("Check", "اختبار"), quiz = Quiz(passMark, attempts) };

    private static async Task<(Guid Id, int Revision)> CreateAsync(HttpClient client, object definition, Guid? unit, object? audience = null, bool open = true)
    {
        var view = await OkAsync(client.PostAsJsonAsync($"{Api}/manage/courses", new { orgUnitId = unit, category = "Security", audience, openEnrolment = open, definition }),
            HttpStatusCode.Created);
        return (view.GetProperty("id").GetGuid(), view.GetProperty("revision").GetInt32());
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid id, int revision, bool retraining = false) =>
        client.PostAsJsonAsync($"{Api}/manage/courses/{id}:publish", new { revision, requiresRetraining = retraining });

    private static Task<HttpResponseMessage> AnswerAsync(HttpClient client, Guid id, bool right) =>
        client.PostAsJsonAsync($"{Api}/courses/{id}/lessons/check/attempts", new
        {
            answers = new Dictionary<string, string[]> { ["q1"] = [right ? "b" : "a"], ["q2"] = ["f"] },
        });

    private static async Task<Guid[]> CatalogueAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync($"{Api}/catalogue"))).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task A_course_of_a_page_a_video_and_a_quiz_is_taken_within_its_attempts_and_ends_with_a_certificate()
    {
        var o = await SetupAsync();
        var mona = o.T["mona"].Client;
        var laila = o.T["laila"].Client;
        var (id, revision) = await CreateAsync(mona, Definition(o, PageLesson(o), VideoLesson(o.Video), QuizLesson()), o.Gulf, Audience(o.Gulf));
        Assert.DoesNotContain(id, await CatalogueAsync(laila));
        Assert.Equal("conflict.learning.course_revision", await DetailKeyAsync(await PublishAsync(mona, id, revision + 1), HttpStatusCode.Conflict));
        var published = await OkAsync(PublishAsync(mona, id, revision));
        Assert.Equal("published", published.GetProperty("status").GetString());
        Assert.Equal(1, published.GetProperty("publishedVersion").GetInt32());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.LearningCoursePublished, id.ToString());

        // Laila in Gulf sees it; Kofi in Africa does not, not even by its ID.
        Assert.Contains(id, await CatalogueAsync(laila));
        Assert.DoesNotContain(id, await CatalogueAsync(o.T["kofi"].Client));
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["kofi"].Client.GetAsync($"{Api}/courses/{id}")).StatusCode);

        // The learner never sees which answers are right, and nothing is recorded before enrolling.
        var course = await OkAsync(laila.GetAsync($"{Api}/courses/{id}"));
        Assert.Equal(new[] { "ar", "en" }, course.GetProperty("locales").EnumerateArray().Select(l => l.GetString()!).ToArray());
        Assert.DoesNotContain("correct", course.GetProperty("lessons")[2].GetRawText(), StringComparison.Ordinal);
        Assert.Equal("none", course.GetProperty("me").GetProperty("state").GetString());
        Assert.Equal("conflict.learning.not_enrolled",
            await DetailKeyAsync(await laila.PostAsync($"{Api}/courses/{id}/lessons/passwords:complete", null), HttpStatusCode.Conflict));

        var enrolled = await OkAsync(laila.PostAsync($"{Api}/courses/{id}:enrol", null));
        Assert.Equal("open", enrolled.GetProperty("me").GetProperty("state").GetString());
        Assert.Equal("self", enrolled.GetProperty("me").GetProperty("source").GetString());
        var afterPage = await OkAsync(laila.PostAsync($"{Api}/courses/{id}/lessons/passwords:complete", null));
        Assert.Equal(33, afterPage.GetProperty("me").GetProperty("percent").GetInt32());
        Assert.Equal("conflict.learning.quiz_needs_attempt",
            await DetailKeyAsync(await laila.PostAsync($"{Api}/courses/{id}/lessons/check:complete", null), HttpStatusCode.Conflict));

        // Laila stops at the video and picks up there on another device.
        await OkAsync(laila.PutAsJsonAsync($"{Api}/courses/{id}/position", new { lessonKey = "phishing" }));
        Assert.Equal("phishing", (await OkAsync(laila.GetAsync($"{Api}/courses/{id}"))).GetProperty("me").GetProperty("lastLessonKey").GetString());
        await OkAsync(laila.PostAsync($"{Api}/courses/{id}/lessons/phishing:complete", null));

        // Two attempts, both failed; the third is refused until Mona grants one more.
        var first = await OkAsync(AnswerAsync(laila, id, right: false));
        Assert.Equal(50, first.GetProperty("score").GetInt32());
        Assert.False(first.GetProperty("passed").GetBoolean());
        Assert.Equal(1, first.GetProperty("course").GetProperty("lessons")[2].GetProperty("quiz").GetProperty("attemptsLeft").GetInt32());
        await OkAsync(AnswerAsync(laila, id, right: false));
        Assert.Equal("conflict.learning.attempts_used", await DetailKeyAsync(await AnswerAsync(laila, id, right: true), HttpStatusCode.Conflict));

        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync($"{Api}/manage/courses/{id}/learners")).StatusCode);
        var learners = await OkAsync(mona.GetAsync($"{Api}/manage/courses/{id}/learners"));
        var row = Assert.Single(learners.EnumerateArray());
        var standing = Assert.Single(row.GetProperty("quizzes").EnumerateArray());
        Assert.Equal(2, standing.GetProperty("used").GetInt32());
        Assert.Equal(2, standing.GetProperty("allowed").GetInt32());
        var assignment = row.GetProperty("assignmentId").GetGuid();
        Assert.Contains("validation.learning.grant_extra",
            await ErrorKeysAsync(await mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/learners/{assignment}/attempts", new { lessonKey = "check", extra = 11 })));
        await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{id}/learners/{assignment}/attempts", new { lessonKey = "check", extra = 1 }));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.LearningAttemptsGranted, assignment.ToString());

        var passed = await OkAsync(AnswerAsync(laila, id, right: true));
        Assert.True(passed.GetProperty("passed").GetBoolean());
        var me = passed.GetProperty("course").GetProperty("me");
        Assert.Equal("completed", me.GetProperty("state").GetString());
        Assert.Equal(100, me.GetProperty("percent").GetInt32());
        Assert.Equal(1, me.GetProperty("completedVersion").GetInt32());
        var certificateId = me.GetProperty("certificateId").GetGuid();
        Assert.Equal("conflict.learning.already_completed", await DetailKeyAsync(await AnswerAsync(laila, id, right: true), HttpStatusCode.Conflict));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(CourseCompleted), o.T.Id));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(CertificateIssued), o.T.Id));

        var mine = await OkAsync(laila.GetAsync($"{Api}/me"));
        var done = Assert.Single(mine.GetProperty("completed").EnumerateArray());
        Assert.Equal(certificateId, done.GetProperty("certificateId").GetGuid());
        Assert.Empty(mine.GetProperty("inProgress").EnumerateArray());

        // The certificate: Laila and Khaled, who reports on Gulf, find it by its ID; Omar and other tenants do not.
        var certificate = await OkAsync(laila.GetAsync($"{Api}/certificates/{certificateId}"));
        var code = certificate.GetProperty("code").GetString()!;
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$", code);
        var typed = code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var verified = await OkAsync(o.T["khaled"].Client.GetAsync($"{Api}/certificates/verify?code={typed}"));
        Assert.Equal(o.T["laila"].Id, verified.GetProperty("learner").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["omar"].Client.GetAsync($"{Api}/certificates/verify?code={code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["omar"].Client.GetAsync($"{Api}/certificates/{certificateId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["omar"].Client.GetAsync($"{Api}/certificates/{certificateId}/pdf")).StatusCode);

        // The PDF is printed on download, with the holder's name, the course and the ID; a print service that is down gives a conflict.
        var pdf = await laila.GetAsync($"{Api}/certificates/{certificateId}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var printed = await pdf.Content.ReadAsStringAsync();
        Assert.StartsWith("%PDF", printed, StringComparison.Ordinal);
        Assert.Contains(code, printed, StringComparison.Ordinal);
        Assert.Contains("Information security basics", printed, StringComparison.Ordinal);
        world.Factory.Gotenberg.HtmlDown = true;
        try
        {
            Assert.Equal("conflict.learning.certificate_unavailable",
                await DetailKeyAsync(await laila.GetAsync($"{Api}/certificates/{certificateId}/pdf"), HttpStatusCode.Conflict));
        }
        finally
        {
            world.Factory.Gotenberg.HtmlDown = false;
        }
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/certificates/verify?code={code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/courses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/manage/courses/{id}")).StatusCode);
    }

    [Fact]
    public async Task Builders_stay_in_their_scope_and_publishing_checks_every_source()
    {
        var o = await SetupAsync();
        var mona = o.T["mona"].Client;

        // Every lesson needs its text and its material in each language of the course.
        var missing = await mona.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            orgUnitId = o.Gulf,
            definition = new
            {
                titles = Both("Safety", "السلامة"),
                lessons = new object[] { new { kind = "page", titles = new { en = "Fire exits" }, sources = new { en = o.Page } } },
            },
        });
        var keys = await ErrorKeysAsync(missing);
        Assert.Contains("validation.learning.lesson_title", keys);
        Assert.Contains("validation.learning.lesson_source", keys);
        Assert.Contains("validation.learning.option_correct", await ErrorKeysAsync(await mona.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            orgUnitId = o.Gulf,
            definition = new
            {
                titles = new { en = "Quiz" },
                lessons = new object[]
                {
                    new
                    {
                        kind = "quiz", titles = new { en = "Quiz" },
                        quiz = new { questions = new object[] { new { kind = "single", prompts = new { en = "Pick one" }, options = new object[] { new { labels = new { en = "A" } }, new { labels = new { en = "B" } } } } } },
                    },
                },
            },
        })));

        // A video lesson needs a video, and a source must exist and be published.
        var (wrong, revision) = await CreateAsync(mona, Definition(o, PageLesson(o), VideoLesson(o.Pdf)), o.Gulf);
        Assert.Contains("validation.learning.source_not_video", await ErrorKeysAsync(await PublishAsync(mona, wrong, revision)));
        var (unknown, unknownRevision) = await CreateAsync(mona, Definition(o, new { kind = "page", titles = Both("Gone", "محذوف"), sources = new { en = Guid.NewGuid(), ar = Guid.NewGuid() } }),
            o.Gulf);
        Assert.Contains("validation.learning.source_unreadable", await ErrorKeysAsync(await PublishAsync(mona, unknown, unknownRevision)));
        var (empty, emptyRevision) = await CreateAsync(mona, Definition(o), o.Gulf);
        Assert.Contains("validation.learning.lesson_count", await ErrorKeysAsync(await PublishAsync(mona, empty, emptyRevision)));

        // Mona builds for Gulf only: not for Africa, not for the whole tenant, and she does not see a tenant-wide course.
        Assert.Equal("authz.denied.learning_scope", await DetailKeyAsync(
            await mona.PostAsJsonAsync($"{Api}/manage/courses", new { orgUnitId = o.Africa, definition = Definition(o, PageLesson(o)) }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.learning_scope", await DetailKeyAsync(
            await mona.PostAsJsonAsync($"{Api}/manage/courses", new { definition = Definition(o, PageLesson(o)) }), HttpStatusCode.Forbidden));
        var (tenantWide, _) = await CreateAsync(o.T["admin"].Client, Definition(o, PageLesson(o)), null);
        Assert.Equal(HttpStatusCode.NotFound, (await mona.GetAsync($"{Api}/manage/courses/{tenantWide}")).StatusCode);
        Assert.DoesNotContain(tenantWide, (await OkAsync(mona.GetAsync($"{Api}/manage/courses"))).EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["laila"].Client.PostAsJsonAsync($"{Api}/manage/courses", new { orgUnitId = o.Gulf, definition = Definition(o, PageLesson(o)) }))
            .StatusCode);

        var rights = await OkAsync(mona.GetAsync($"{Api}/rights"));
        Assert.True(rights.GetProperty("manage").GetBoolean());
        Assert.False((await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/rights"))).GetProperty("manage").GetBoolean());
    }

    [Fact]
    public async Task Edits_wait_for_the_next_version_and_retraining_makes_earlier_completions_stop_counting()
    {
        var o = await SetupAsync();
        var mona = o.T["mona"].Client;
        var laila = o.T["laila"].Client;
        var (id, revision) = await CreateAsync(mona, Definition(o, PageLesson(o)), o.Gulf, Audience(o.Gulf));
        await OkAsync(PublishAsync(mona, id, revision));
        await OkAsync(laila.PostAsync($"{Api}/courses/{id}:enrol", null));
        await OkAsync(laila.PostAsync($"{Api}/courses/{id}/lessons/passwords:complete", null));

        // Mona adds a lesson to the draft; learners keep version 1 until she publishes.
        var edited = await OkAsync(mona.PutAsJsonAsync($"{Api}/manage/courses/{id}", new
        {
            revision, orgUnitId = o.Gulf, category = "Security", audience = Audience(o.Gulf), openEnrolment = true,
            definition = Definition(o, PageLesson(o), VideoLesson(o.Video)),
        }));
        Assert.True(edited.GetProperty("draftChanged").GetBoolean());
        Assert.Single((await OkAsync(laila.GetAsync($"{Api}/courses/{id}"))).GetProperty("lessons").EnumerateArray());

        // Version 2 without retraining: Laila's completion of version 1 still counts.
        await OkAsync(PublishAsync(mona, id, edited.GetProperty("revision").GetInt32()));
        var v2 = await OkAsync(laila.GetAsync($"{Api}/courses/{id}"));
        Assert.Equal(2, v2.GetProperty("version").GetInt32());
        Assert.Equal("completed", v2.GetProperty("me").GetProperty("state").GetString());
        Assert.Equal(1, v2.GetProperty("me").GetProperty("completedVersion").GetInt32());

        // A change to the page shows in the builder until the course is published again.
        var admin = o.T["admin"].Client;
        await OkAsync(admin.PutAsJsonAsync($"/api/v1/content/items/{o.Page}", new
        {
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "Passwords", body = Body("Use a passphrase of four words.") },
                ["ar"] = new { title = "كلمات المرور", body = Body("استخدم عبارة مرور من أربع كلمات.") },
            },
        }));
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/content/items/{o.Page}/publish", new { }));
        var detail = await OkAsync(mona.GetAsync($"{Api}/manage/courses/{id}"));
        Assert.Equal(new[] { "en:passwords", "ar:passwords" }.Order(), detail.GetProperty("changedSources").EnumerateArray()
            .Select(c => $"{c.GetProperty("locale").GetString()}:{c.GetProperty("lessonKey").GetString()}").Order());

        // Version 3 requires retraining: the old completion no longer counts and Laila starts a new cycle from the beginning.
        await OkAsync(PublishAsync(mona, id, detail.GetProperty("revision").GetInt32(), retraining: true));
        Assert.Empty((await OkAsync(mona.GetAsync($"{Api}/manage/courses/{id}"))).GetProperty("changedSources").EnumerateArray());
        var v3 = await OkAsync(laila.GetAsync($"{Api}/courses/{id}"));
        Assert.Equal("retraining", v3.GetProperty("me").GetProperty("state").GetString());
        Assert.True(v3.GetProperty("canEnrol").GetBoolean());
        var again = await OkAsync(laila.PostAsync($"{Api}/courses/{id}:enrol", null));
        Assert.Equal("open", again.GetProperty("me").GetProperty("state").GetString());
        Assert.Equal(0, again.GetProperty("me").GetProperty("percent").GetInt32());
        Assert.Equal(2, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from learning.assignment where learner_id = @u", ("u", o.T["laila"].Id)));

        // Retiring withdraws the open assignment, tells Laila, and keeps her first completion.
        await OkAsync(mona.PostAsync($"{Api}/manage/courses/{id}:retire", null));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.LearningCourseRetired, id.ToString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from learning.assignment where status = 'withdrawn'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from learning.assignment where status = 'completed'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from notifications.notification where template_key = 'learning.course.withdrawn' and user_id = @u", ("u", o.T["laila"].Id)));
        Assert.Equal(HttpStatusCode.NotFound, (await laila.GetAsync($"{Api}/courses/{id}")).StatusCode);
        Assert.Single((await OkAsync(laila.GetAsync($"{Api}/me"))).GetProperty("completed").EnumerateArray());
    }

    [Fact]
    public async Task A_path_keeps_its_courses_in_order_and_opens_courses_that_are_closed_on_their_own()
    {
        var o = await SetupAsync();
        var admin = o.T["admin"].Client;
        var laila = o.T["laila"].Client;
        var (first, firstRevision) = await CreateAsync(admin, Definition(o, PageLesson(o)), null, open: false);
        var (second, secondRevision) = await CreateAsync(admin, Definition(o, PageLesson(o, "again")), null, open: false);
        var path = (await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/paths", new
        {
            titles = Both("New starter", "موظف جديد"),
            courses = new object[] { new { courseId = first }, new { courseId = second, requiresPrevious = true } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Contains("validation.learning.path_course_unpublished", await ErrorKeysAsync(await admin.PostAsync($"{Api}/manage/paths/{path}:publish", null)));
        await OkAsync(PublishAsync(admin, first, firstRevision));
        await OkAsync(PublishAsync(admin, second, secondRevision));
        await OkAsync(admin.PostAsync($"{Api}/manage/paths/{path}:publish", null));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.LearningPathPublished, path.ToString());

        var listed = Assert.Single((await OkAsync(laila.GetAsync($"{Api}/paths"))).EnumerateArray());
        Assert.Equal(2, listed.GetProperty("courses").GetInt32());
        var view = await OkAsync(laila.GetAsync($"{Api}/paths/{path}"));
        Assert.False(view.GetProperty("courses")[0].GetProperty("locked").GetBoolean());
        Assert.True(view.GetProperty("courses")[1].GetProperty("locked").GetBoolean());

        // Neither course is open on its own; the path opens them, in order.
        Assert.Equal("authz.denied.learning_enrolment_closed", await DetailKeyAsync(await laila.PostAsync($"{Api}/courses/{first}:enrol", null), HttpStatusCode.Forbidden));
        Assert.Equal("conflict.learning.path_locked", await DetailKeyAsync(await laila.PostAsync($"{Api}/paths/{path}/courses/{second}:start", null), HttpStatusCode.Conflict));
        await OkAsync(laila.PostAsync($"{Api}/paths/{path}/courses/{first}:start", null));
        await OkAsync(laila.PostAsync($"{Api}/courses/{first}/lessons/passwords:complete", null));
        Assert.False((await OkAsync(laila.GetAsync($"{Api}/paths/{path}"))).GetProperty("courses")[1].GetProperty("locked").GetBoolean());
        var started = await OkAsync(laila.PostAsync($"{Api}/paths/{path}/courses/{second}:start", null));
        Assert.Equal("open", started.GetProperty("me").GetProperty("state").GetString());

        // Mona, scoped to Gulf, cannot see or change the tenant-wide path; Kofi is in its audience too, other tenants are not.
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["mona"].Client.PutAsJsonAsync($"{Api}/manage/paths/{path}", new { rowVersion = 2, titles = Both("x", "x") })).StatusCode);
        Assert.Single((await OkAsync(o.T["kofi"].Client.GetAsync($"{Api}/paths"))).EnumerateArray());
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/paths/{path}")).StatusCode);
    }

    [Fact]
    public async Task Workplace_has_no_courses()
    {
        var o = await SetupAsync("workplace");
        var rights = await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/rights"));
        Assert.False(rights.GetProperty("take").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["laila"].Client.GetAsync($"{Api}/catalogue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["admin"].Client.PostAsJsonAsync($"{Api}/manage/courses", new { definition = new { titles = new { en = "x" } } })).StatusCode);
    }
}
