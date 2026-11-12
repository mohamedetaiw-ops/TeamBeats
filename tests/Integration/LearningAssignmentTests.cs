using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Learning;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 T006 and T007 (US2, FR-005 to FR-008, FR-012, FR-017): a learning administrator gives a published course to
/// the people of their unit with a due period and a recurrence; newcomers get it with their own date and leavers lose it;
/// two rules give one assignment with the earlier date; nobody outside the administrator's scope can be targeted;
/// reminders, overdue notices to the learner and their manager, renewal after the recurrence, and retraining after a new
/// version. Other tenants and the Workplace package reach no rule. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningAssignmentTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Africa, Guid Course);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return errorsOf(problem);
        static string[] errorsOf(JsonElement problem) => problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    private static Dictionary<string, string> Both(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static object Audience(Guid unit) => new { anyOf = new object[] { new { orgUnits = new object[] { new { id = unit } } } } };

    /// <summary>Holding over Gulf and Africa. Mona runs learning for Gulf; Laila and Omar work there and report to Huda; Kofi works in Africa.</summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("la-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "mona", "laila", "omar", "huda", "kofi", "sara" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "mona", "laila", "omar", "huda" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "kofi", "Africa");
        if (package == "workplace")
        {
            return new Org(t, gulf, africa, Guid.Empty);
        }
        await world.AssignAsync(t, "mona", BuiltInRoles.LearningAdministrator, "org_subtree", gulf);
        foreach (var name in new[] { "laila", "omar" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[name].Id}/manager", new { managerId = t["huda"].Id })).StatusCode);
        }
        var mona = t["mona"].Client;
        var view = await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            orgUnitId = gulf,
            category = "Safety",
            openEnrolment = false,
            definition = new
            {
                titles = Both("Fire safety", "السلامة من الحرائق"),
                lessons = new object[] { QuizLesson() },
            },
        }), HttpStatusCode.Created);
        var course = view.GetProperty("id").GetGuid();
        await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{course}:publish", new { revision = view.GetProperty("revision").GetInt32() }));
        return new Org(t, gulf, africa, course);
    }

    private static object QuizLesson() => new
    {
        key = "check", kind = "quiz", titles = Both("Check", "اختبار"),
        quiz = new
        {
            passMark = 100, attempts = 3,
            questions = new object[]
            {
                new
                {
                    key = "q1", kind = "true_false", prompts = Both("Use the lift in a fire.", "استخدم المصعد عند الحريق."),
                    options = new object[] { new { key = "t", labels = Both("True", "صح"), correct = false }, new { key = "f", labels = Both("False", "خطأ"), correct = true } },
                },
            },
        },
    };

    private static Task<HttpResponseMessage> RuleAsync(HttpClient client, Guid course, object body) => client.PostAsJsonAsync($"{Api}/manage/courses/{course}/rules", body);

    private Task<long> OpenAsync(Org o, string user) =>
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from learning.assignment where learner_id = @u and status = 'open'", ("u", o.T[user].Id));

    private Task<DateOnly> DueAsync(Org o, string user) =>
        world.Database.TenantScalarAsync<DateOnly>(o.T.Id, "select due_on from learning.assignment where learner_id = @u and status = 'open'", ("u", o.T[user].Id));

    private Task<long> NotificationsAsync(Org o, string user, string template) =>
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    private async Task DispatchUntilAsync(Func<Task<bool>> done)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 50 && !await done(); i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
        Assert.True(await done());
    }

    private Task<int> RunDueAsync(Org o, DateTimeOffset now) =>
        world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<AssignmentRules>().RunDueAsync(now, 1000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private static DateTimeOffset Noon(DateOnly day) => new(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

    [Fact]
    public async Task A_rule_gives_the_course_to_its_unit_newcomers_get_their_own_date_and_leavers_lose_it()
    {
        var o = await SetupAsync();
        var mona = o.T["mona"].Client;
        var rule = await OkAsync(RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, audience = Audience(o.Gulf), dueDays = 30, recurrenceMonths = 12 }), HttpStatusCode.Created);
        var ruleId = rule.GetProperty("id").GetGuid();
        Assert.Equal(4, rule.GetProperty("covered").GetInt32());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.LearningAssignmentCreated, ruleId.ToString());

        // Everyone in Gulf has the course, due in 30 days or the next working day after; Kofi in Africa does not.
        Assert.Equal(1, await OpenAsync(o, "laila"));
        Assert.Equal(0, await OpenAsync(o, "kofi"));
        var due = await DueAsync(o, "laila");
        var expected = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        Assert.InRange(due.DayNumber - expected.DayNumber, -1, 4);
        var mine = await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/me"));
        var assigned = Assert.Single(mine.GetProperty("assigned").EnumerateArray());
        Assert.Equal("rule", assigned.GetProperty("source").GetString());
        Assert.Equal(1, await NotificationsAsync(o, "laila", AssignmentRules.AssignedTemplate));
        Assert.Equal(4, await world.Database.OutboxCountAsync(nameof(CourseAssigned), o.T.Id));

        // Sara joins Gulf and gets the course with her own date.
        await world.AddMembershipAsync(o.T, "sara", "Gulf");
        await DispatchUntilAsync(async () => await OpenAsync(o, "sara") == 1);

        // Omar leaves Gulf: his open assignment is withdrawn; nothing else changes.
        var membership = (await OkAsync(o.T["admin"].Client.GetAsync($"/api/v1/org/units/{o.Gulf}/memberships"))).EnumerateArray()
            .First(m => m.GetProperty("userId").GetGuid() == o.T["omar"].Id).GetProperty("id").GetGuid();
        Assert.True((await o.T["admin"].Client.PostAsync($"/api/v1/org/memberships/{membership}:end", null)).IsSuccessStatusCode);
        await DispatchUntilAsync(async () => await OpenAsync(o, "omar") == 0);
        Assert.Equal(1L, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from learning.assignment where learner_id = @u and status = 'withdrawn'", ("u", o.T["omar"].Id)));
        Assert.Equal(1, await OpenAsync(o, "laila"));

        // Ending the rule withdraws what only it gave, and is audited.
        var ended = await OkAsync(mona.PostAsync($"{Api}/manage/courses/{o.Course}/rules/{ruleId}:end", null));
        Assert.Equal("ended", ended.GetProperty("status").GetString());
        Assert.Equal(0, await OpenAsync(o, "laila"));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.LearningAssignmentWithdrawn, ruleId.ToString());
    }

    [Fact]
    public async Task Two_rules_give_one_assignment_with_the_earlier_date_and_nobody_outside_the_scope_can_be_targeted()
    {
        var o = await SetupAsync();
        var mona = o.T["mona"].Client;
        await OkAsync(RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, audience = Audience(o.Gulf), dueDays = 60 }), HttpStatusCode.Created);
        var late = await DueAsync(o, "laila");
        await OkAsync(RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, people = new[] { o.T["laila"].Id }, dueDays = 10 }), HttpStatusCode.Created);
        Assert.Equal(1, await OpenAsync(o, "laila"));
        Assert.True(await DueAsync(o, "laila") < late);
        Assert.Equal(late, await DueAsync(o, "omar"));

        // Mona manages Gulf only: not Africa, not Kofi, not the whole tenant.
        Assert.Contains("validation.learning.rule_scope", await ErrorKeysAsync(await RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, audience = Audience(o.Africa) })));
        Assert.Contains("validation.learning.rule_scope", await ErrorKeysAsync(await RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, people = new[] { o.T["kofi"].Id } })));
        Assert.Equal(HttpStatusCode.Forbidden, (await RuleAsync(mona, o.Course, new { audience = Audience(o.Africa) })).StatusCode);
        Assert.Contains("validation.learning.rule_due", await ErrorKeysAsync(await RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, dueDays = 0, graceDays = 31 })));
        Assert.Equal(0, await OpenAsync(o, "kofi"));

        // A learner cannot see or make rules; another tenant cannot even find the course.
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["laila"].Client.GetAsync($"{Api}/manage/courses/{o.Course}/rules")).StatusCode);
        var other = await world.CreateTenantAsync("lao-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/manage/courses/{o.Course}/rules")).StatusCode);
    }

    [Fact]
    public async Task Reminders_come_before_the_due_date_and_overdue_learners_and_their_manager_hear_at_most_weekly()
    {
        var o = await SetupAsync();
        await OkAsync(RuleAsync(o.T["mona"].Client, o.Course, new { orgUnitId = o.Gulf, people = new[] { o.T["laila"].Id }, dueDays = 20, graceDays = 2 }),
            HttpStatusCode.Created);
        var due = await DueAsync(o, "laila");

        Assert.Equal(0, await RunDueAsync(o, Noon(due.AddDays(-8))));
        Assert.True(await RunDueAsync(o, Noon(due.AddDays(-7))) > 0);
        Assert.Equal(0, await RunDueAsync(o, Noon(due.AddDays(-7)).AddHours(2)));
        Assert.True(await RunDueAsync(o, Noon(due.AddDays(-1))) > 0);
        Assert.Equal(2, await NotificationsAsync(o, "laila", AssignmentRules.ReminderTemplate));

        // Inside the grace period nothing more; after it Laila and Huda, her manager, are told once a week.
        Assert.Equal(0, await RunDueAsync(o, Noon(due.AddDays(2))));
        Assert.True(await RunDueAsync(o, Noon(due.AddDays(3))) > 0);
        Assert.Equal(0, await RunDueAsync(o, Noon(due.AddDays(5))));
        Assert.Equal(1, await NotificationsAsync(o, "laila", AssignmentRules.OverdueTemplate));
        Assert.Equal(1, await NotificationsAsync(o, "huda", AssignmentRules.TeamOverdueTemplate));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(CourseOverdue), o.T.Id));
        Assert.True(await RunDueAsync(o, Noon(due.AddDays(10))) > 0);
        Assert.Equal(2, await NotificationsAsync(o, "laila", AssignmentRules.OverdueTemplate));
        Assert.Equal(1, await world.Database.OutboxCountAsync(nameof(CourseOverdue), o.T.Id));
        Assert.Equal(0, await NotificationsAsync(o, "omar", AssignmentRules.OverdueTemplate));
    }

    [Fact]
    public async Task A_recurring_completion_expires_into_a_new_assignment_and_a_retraining_version_assigns_again()
    {
        var o = await SetupAsync();
        var mona = o.T["mona"].Client;
        var laila = o.T["laila"].Client;
        await OkAsync(RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, people = new[] { o.T["laila"].Id, o.T["omar"].Id }, dueDays = 30, recurrenceMonths = 12 }),
            HttpStatusCode.Created);
        var passed = await OkAsync(laila.PostAsJsonAsync($"{Api}/courses/{o.Course}/lessons/check/attempts",
            new { answers = new Dictionary<string, string[]> { ["q1"] = ["f"] } }));
        Assert.Equal("completed", passed.GetProperty("course").GetProperty("me").GetProperty("state").GetString());
        var expires = await world.Database.TenantScalarAsync<DateOnly>(o.T.Id,
            "select expires_on from learning.assignment where learner_id = @u and status = 'completed'", ("u", o.T["laila"].Id));
        Assert.InRange(expires.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(12).DayNumber, -1, 4);
        var certificate = passed.GetProperty("course").GetProperty("me").GetProperty("certificateId").GetGuid();
        Assert.NotEqual(JsonValueKind.Null, (await OkAsync(laila.GetAsync($"{Api}/certificates/{certificate}"))).GetProperty("expiresAt").ValueKind);

        // Twelve months on, the completion expires and a new cycle is assigned; the certificate stays in history.
        Assert.True(await RunDueAsync(o, Noon(expires.AddDays(1))) > 0);
        Assert.Equal(1, await OpenAsync(o, "laila"));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from learning.assignment where learner_id = @u and status = 'expired'", ("u", o.T["laila"].Id)));
        Assert.Equal(2, await world.Database.TenantScalarAsync<int>(o.T.Id, "select cycle from learning.assignment where learner_id = @u and status = 'open'", ("u", o.T["laila"].Id)));

        // Omar completes; a version that requires retraining gives it to him again.
        await OkAsync(o.T["omar"].Client.PostAsJsonAsync($"{Api}/courses/{o.Course}/lessons/check/attempts",
            new { answers = new Dictionary<string, string[]> { ["q1"] = ["f"] } }));
        var detail = await OkAsync(mona.GetAsync($"{Api}/manage/courses/{o.Course}"));
        var revision = detail.GetProperty("revision").GetInt32();
        var edited = await OkAsync(mona.PutAsJsonAsync($"{Api}/manage/courses/{o.Course}", new
        {
            revision, orgUnitId = o.Gulf, category = "Safety", openEnrolment = false,
            definition = new { titles = Both("Fire safety 2027", "السلامة من الحرائق 2027"), lessons = new object[] { QuizLesson() } },
        }));
        await OkAsync(mona.PostAsJsonAsync($"{Api}/manage/courses/{o.Course}:publish", new { revision = edited.GetProperty("revision").GetInt32(), requiresRetraining = true }));
        await DispatchUntilAsync(async () => await OpenAsync(o, "omar") == 1);
        Assert.Equal(1, await NotificationsAsync(o, "omar", AssignmentRules.RetrainingTemplate));
    }

    [Fact]
    public async Task Workplace_has_no_mandatory_training_and_retiring_a_course_ends_its_rules()
    {
        var w = await SetupAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.T["admin"].Client.GetAsync($"{Api}/manage/courses/{Guid.NewGuid()}/rules")).StatusCode);

        var o = await SetupAsync();
        var mona = o.T["mona"].Client;
        var rule = await OkAsync(RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf, audience = Audience(o.Gulf) }), HttpStatusCode.Created);
        await OkAsync(mona.PostAsync($"{Api}/manage/courses/{o.Course}:retire", null));
        Assert.Equal("ended", await world.Database.TenantScalarAsync<string>(o.T.Id, "select status from learning.assignment_rule where id = @r", ("r", rule.GetProperty("id").GetGuid())));
        Assert.Equal(0, await OpenAsync(o, "laila"));
        await world.AddMembershipAsync(o.T, "sara", "Gulf");
        await world.DispatchTenantAsync(o.T.Id);
        Assert.Equal(0, await OpenAsync(o, "sara"));
        Assert.Equal(HttpStatusCode.Conflict, (await RuleAsync(mona, o.Course, new { orgUnitId = o.Gulf })).StatusCode);
    }
}
