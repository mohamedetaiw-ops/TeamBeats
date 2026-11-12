using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 T008 (US4, FR-011) with spec 022 FR-015 and spec 025: an onboarding training step gives the joiner the
/// courses it names, due with the step, and is done once they complete them; a cancelled journey withdraws what only it
/// gave. A policy linked to a course gives it to everyone the policy applies to when a material version comes into
/// force, and attesting waits for the completion when the owner chose that. Only people who may give linked training
/// link it, only published courses can be named, and another tenant finds nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningObligationTests(TenantWorld world)
{
    private const string Learning = "/api/v1/learning";
    private const string Templates = "/api/v1/hr/journey-templates";
    private const string Journeys = "/api/v1/hr/journeys";
    private const string Policies = "/api/v1/policies";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("lo-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        await world.CreateUnitAsync(t, "Gulf", null, "organization");
        foreach (var name in new[] { "mona", "karim", "co", "legal", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "tenant", null);
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
    }

    /// <summary>A course with one true or false question, by the tenant administrator; published unless asked not to.</summary>
    private static async Task<Guid> CourseAsync(TestTenant t, string title, bool publish = true)
    {
        var view = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Learning}/manage/courses", new
        {
            definition = new
            {
                titles = Names(title, title + " ع"),
                lessons = new object[]
                {
                    new
                    {
                        key = "check", kind = "quiz", titles = Names("Check", "اختبار"),
                        quiz = new
                        {
                            passMark = 100, attempts = 3,
                            questions = new object[]
                            {
                                new
                                {
                                    key = "q1", kind = "true_false", prompts = Names("Share your password.", "شارك كلمة المرور."),
                                    options = new object[]
                                    {
                                        new { key = "t", labels = Names("True", "صح"), correct = false },
                                        new { key = "f", labels = Names("False", "خطأ"), correct = true },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        }), HttpStatusCode.Created);
        var id = view.GetProperty("id").GetGuid();
        if (publish)
        {
            await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Learning}/manage/courses/{id}:publish", new { revision = view.GetProperty("revision").GetInt32() }));
        }
        return id;
    }

    private static Task<JsonElement> PassAsync(TestTenant t, string user, Guid course) =>
        OkAsync(t[user].Client.PostAsJsonAsync($"{Learning}/courses/{course}/lessons/check/attempts", new { answers = new Dictionary<string, string[]> { ["q1"] = ["f"] } }));

    private Task<string?> AssignmentAsync(TestTenant t, string user, Guid course) =>
        world.Database.TenantScalarAsync<string?>(t.Id,
            "select status || ':' || source from learning.assignment where learner_id = @u and course_id = @c order by created_at desc limit 1", ("u", t[user].Id), ("c", course));

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private async Task DispatchAllAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await DispatchAsync();
        }
    }

    private Task HireAsync(TestTenant t, string user) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await sp.GetRequiredService<JourneyStarter>().HandleAsync(new EmployeeHired(t[user].Id, "R1", today, Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private static async Task<Guid> JourneyOfAsync(TestTenant t, string user) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/me/journeys"))).EnumerateArray().Single(j => j.GetProperty("kind").GetString() == "onboarding").GetProperty("id").GetGuid();

    private static async Task<JsonElement> StepAsync(TestTenant t, string user, Guid journey, string key) =>
        (await OkAsync(t[user].Client.GetAsync($"{Journeys}/{journey}"))).GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    [Fact]
    public async Task An_onboarding_training_step_gives_its_courses_and_is_done_when_they_are_completed()
    {
        var t = await FreshAsync();
        var security = await CourseAsync(t, "Information security");
        var draft = await CourseAsync(t, "Draft course", publish: false);
        object Template(string kind, string assignee, Guid[]? courses, string type = "training") => new
        {
            key = kind, kind, names = Names("Journey", "رحلة"), startOn = kind == "onboarding" ? "hire" : "termination",
            steps = new object[] { new { key = "training", names = Names("Training", "تدريب"), offsetDays = 0, assignee, type, courses, dueWorkingDays = 5 } },
        };

        // A training step names published courses, belongs to the joiner and to onboarding; no other step names courses.
        var admin = t["admin"].Client;
        Assert.Contains("validation.journey.courses", await ErrorKeysAsync(await admin.PostAsJsonAsync(Templates, Template("onboarding", "person", null))));
        Assert.Contains("validation.journey.courses", await ErrorKeysAsync(await admin.PostAsJsonAsync(Templates, Template("onboarding", "person", [draft]))));
        Assert.Contains("validation.journey.courses", await ErrorKeysAsync(await admin.PostAsJsonAsync(Templates, Template("onboarding", "person", [security], "task"))));
        Assert.Contains("validation.journey.step_type", await ErrorKeysAsync(await admin.PostAsJsonAsync(Templates, Template("onboarding", "manager", [security]))));
        Assert.Contains("validation.journey.step_type", await ErrorKeysAsync(await admin.PostAsJsonAsync(Templates, Template("offboarding", "person", [security]))));
        await OkAsync(admin.PostAsJsonAsync(Templates, Template("onboarding", "person", [security])), HttpStatusCode.Created);

        // Day one: Mona has the course from onboarding, due with the step; the step stays open until she completes it.
        await HireAsync(t, "mona");
        var journey = await JourneyOfAsync(t, "mona");
        Assert.Equal("open", (await StepAsync(t, "mona", journey, "training")).GetProperty("status").GetString());
        Assert.Equal("open:onboarding", await AssignmentAsync(t, "mona", security));
        var mine = await OkAsync(t["mona"].Client.GetAsync($"{Learning}/me"));
        Assert.Contains(mine.GetProperty("assigned").EnumerateArray(), c => c.GetProperty("courseId").GetGuid() == security && c.GetProperty("dueOn").ValueKind == JsonValueKind.String);
        await PassAsync(t, "mona", security);
        await DispatchAllAsync();
        var step = await StepAsync(t, "hr_gulf", journey, "training");
        Assert.Equal(("done", "done", t["mona"].Id), (step.GetProperty("status").GetString(), step.GetProperty("outcome").GetString(),
            step.GetProperty("completedBy").GetProperty("id").GetGuid()));

        // Karim's journey is cancelled: the course only onboarding gave him is withdrawn.
        await HireAsync(t, "karim");
        var karims = await JourneyOfAsync(t, "karim");
        Assert.Equal("open:onboarding", await AssignmentAsync(t, "karim", security));
        await OkAsync(t["hr_gulf"].Client.PostAsJsonAsync($"{Journeys}/{karims}/cancel", new { reason = "Offer withdrawn" }));
        Assert.Equal("withdrawn:onboarding", await AssignmentAsync(t, "karim", security));

        // A joiner who already completed the course does not need the step.
        await world.SeedUserAsync(t, "rana", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "rana", "Gulf");
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into learning.assignment (tenant_id, id, course_id, learner_id, source, status, completed_at, completed_version, created_at)
            values (@t, gen_random_uuid(), @c, @u, 'self', 'completed', now(), 1, now())
            """, ("t", t.Id), ("c", security), ("u", t["rana"].Id));
        await HireAsync(t, "rana");
        Assert.Equal("skipped", (await StepAsync(t, "hr_gulf", await JourneyOfAsync(t, "rana"), "training")).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_policy_linked_to_a_course_assigns_it_on_a_material_version_and_attesting_waits_for_completion()
    {
        var t = await FreshAsync();
        var course = await CourseAsync(t, "Code of conduct");
        var draft = await CourseAsync(t, "Unpublished", publish: false);
        var request = new { code = "COC", names = Names("Code of conduct", "مدونة السلوك"), route = new[] { new { approvers = new[] { t["legal"].Id } } } };
        var policy = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, request), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Compliance officers who may give linked training link a published course: not a steward who only manages
        // policies, nobody who cannot see the policy, and not a draft course.
        await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "policy_steward", names = new Dictionary<string, string> { ["en"] = "Policy steward" },
            permissions = new[] { Permissions.OrgUnitView, Permissions.PolicyAuthor, Permissions.PolicyManage }, delegable = true,
        }), HttpStatusCode.Created);
        await world.SeedUserAsync(t, "steward", MembershipTypes.Employee);
        await world.AssignAsync(t, "steward", "policy_steward", "tenant", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["steward"].Client.PutAsJsonAsync($"{Policies}/{policy}/training", new { courseId = course, beforeAttestation = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_gulf"].Client.PutAsJsonAsync($"{Policies}/{policy}/training", new { courseId = course, beforeAttestation = true })).StatusCode);
        Assert.Contains("validation.policies.course",
            await ErrorKeysAsync(await t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/training", new { courseId = draft, beforeAttestation = true })));
        var linked = await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/training", new { courseId = course, beforeAttestation = true }));
        Assert.Equal(course, linked.GetProperty("training").GetProperty("courseId").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyTrainingLinked, policy.ToString());
        var other = await world.CreateTenantAsync("loo-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PutAsJsonAsync($"{Policies}/{policy}/training", new { courseId = course })).StatusCode);

        // A material version comes into force: everyone it applies to has the course from the policy.
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/draft", new
        {
            titles = Names("Code of conduct", "مدونة السلوك"),
            clauses = new[]
            {
                new
                {
                    level = 1, titles = Names("Respect", "الاحترام"),
                    bodies = new Dictionary<string, object>
                    {
                        ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Treat everyone with respect." } } } } },
                        ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "عامل الجميع باحترام." } } } } },
                    },
                },
            },
            material = true,
        }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        await DispatchAllAsync();
        Assert.Equal("open:policy", await AssignmentAsync(t, "mona", course));
        Assert.Equal("open:policy", await AssignmentAsync(t, "karim", course));

        // Attesting waits until the course is completed.
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{policy}/campaigns", new { }), HttpStatusCode.Created);
        var mine = await OkAsync(t["mona"].Client.GetAsync($"{Policies}/{policy}/attestation"));
        Assert.Equal(course, mine.GetProperty("trainingFirst").GetGuid());
        Assert.Contains("conflict.policies.training_first", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { })));
        await PassAsync(t, "mona", course);
        Assert.Equal(JsonValueKind.Null, (await OkAsync(t["mona"].Client.GetAsync($"{Policies}/{policy}/attestation"))).GetProperty("trainingFirst").ValueKind);
        await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);

        // Unlinking lifts the wait for everyone else.
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{policy}/training", new { courseId = (Guid?)null }));
        await OkAsync(t["karim"].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);
    }
}
