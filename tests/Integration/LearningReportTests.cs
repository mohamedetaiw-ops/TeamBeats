using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 T009 (US5, FR-013, FR-014): a compliance officer sees assigned, completed, overdue and expired counts per
/// unit in their scope, a parent unit counting the units below it, drills down to people and exports the evidence with
/// version, score, time and certificate ID, audited. A Gulf officer sees nothing of Africa. A line manager sees their
/// direct reports only and gives them catalogue courses. Learners and other tenants reach no report. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningReportTests(TenantWorld world)
{
    private const string Api = "/api/v1/learning";

    private sealed record Org(TestTenant T, Guid Holding, Guid Gulf, Guid Africa, Guid Course);

    private static Dictionary<string, string> Both(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

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
        return problem.GetProperty("errors").EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray();
    }

    /// <summary>
    /// Holding over Gulf and Africa. Khaled reports on Gulf, Hanan on the whole tenant. Laila and Omar work in Gulf and
    /// report to Huda; Kofi works in Africa. A rule gives the course to the three of them.
    /// </summary>
    private async Task<Org> SetupAsync()
    {
        var t = await world.CreateTenantAsync("lr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "khaled", "hanan", "laila", "omar", "huda", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "laila", "omar", "huda" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "kofi", "Africa");
        await world.AssignAsync(t, "khaled", BuiltInRoles.ComplianceOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hanan", BuiltInRoles.ComplianceOfficer, "tenant", null);
        foreach (var name in new[] { "laila", "omar" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[name].Id}/manager", new { managerId = t["huda"].Id })).StatusCode);
        }
        var admin = t["admin"].Client;
        var view = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            definition = new
            {
                titles = Both("Anti-bribery", "مكافحة الرشوة"),
                lessons = new object[]
                {
                    new
                    {
                        key = "check", kind = "quiz", titles = Both("Check", "اختبار"),
                        quiz = new
                        {
                            passMark = 50, attempts = 3,
                            questions = new object[]
                            {
                                new
                                {
                                    key = "q1", kind = "true_false", prompts = Both("Gifts above the limit are declared.", "تُعلن الهدايا فوق الحد."),
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
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/rules", new { people = new[] { t["laila"].Id, t["omar"].Id, t["kofi"].Id }, dueDays = 30 }),
            HttpStatusCode.Created);
        return new Org(t, holding, gulf, africa, course);
    }

    private static (int Assigned, int Completed, int Overdue, int Expired) Counts(JsonElement c) =>
        (c.GetProperty("assigned").GetInt32(), c.GetProperty("completed").GetInt32(), c.GetProperty("overdue").GetInt32(), c.GetProperty("expired").GetInt32());

    private static JsonElement Unit(JsonElement report, Guid unit) =>
        report.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("orgUnitId").GetGuid() == unit);

    [Fact]
    public async Task A_compliance_officer_sees_counts_per_unit_in_scope_drills_down_and_exports_audited_evidence()
    {
        var o = await SetupAsync();
        var t = o.T;
        await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/courses/{o.Course}/lessons/check/attempts", new { answers = new Dictionary<string, string[]> { ["q1"] = ["t"] } }));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update learning.assignment set due_on = current_date - 10 where learner_id = @u", ("u", t["omar"].Id));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update learning.assignment set status = 'expired', completed_at = now() - interval '400 days', completed_version = 1 where learner_id = @u",
            ("u", t["kofi"].Id));

        Assert.Contains((await OkAsync(t["khaled"].Client.GetAsync($"{Api}/reports/courses"))).EnumerateArray(), c => c.GetProperty("id").GetGuid() == o.Course);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync($"{Api}/reports/courses")).StatusCode);

        // Hanan covers the tenant: Holding counts both subsidiaries.
        var whole = await OkAsync(t["hanan"].Client.GetAsync($"{Api}/reports/courses/{o.Course}"));
        Assert.True(whole.GetProperty("wholeTenant").GetBoolean());
        Assert.Equal((3, 1, 1, 1), Counts(whole.GetProperty("total")));
        Assert.Equal((3, 1, 1, 1), Counts(Unit(whole, o.Holding).GetProperty("counts")));
        Assert.Equal((2, 1, 1, 0), Counts(Unit(whole, o.Gulf).GetProperty("counts")));
        Assert.Equal((1, 0, 0, 1), Counts(Unit(whole, o.Africa).GetProperty("counts")));

        // Khaled covers Gulf only: no Africa, no Holding, and no drill down outside it.
        var gulf = await OkAsync(t["khaled"].Client.GetAsync($"{Api}/reports/courses/{o.Course}"));
        Assert.Equal((2, 1, 1, 0), Counts(gulf.GetProperty("total")));
        Assert.Equal([o.Gulf], gulf.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("orgUnitId").GetGuid()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["khaled"].Client.GetAsync($"{Api}/reports/courses/{o.Course}/people?unit={o.Africa}")).StatusCode);
        var people = await OkAsync(t["khaled"].Client.GetAsync($"{Api}/reports/courses/{o.Course}/people?unit={o.Gulf}"));
        Assert.Equal(new[] { "overdue", "completed" }, people.EnumerateArray().Select(p => p.GetProperty("state").GetString()!).ToArray());
        var laila = people.EnumerateArray().Single(p => p.GetProperty("person").GetProperty("id").GetGuid() == t["laila"].Id);
        Assert.Equal((1, 100), (laila.GetProperty("version").GetInt32(), laila.GetProperty("score").GetInt32()));
        var code = laila.GetProperty("certificateCode").GetString()!;

        // The evidence lists each person with version, score, time and certificate ID, and is audited.
        var export = await t["khaled"].Client.GetAsync($"{Api}/reports/courses/{o.Course}/evidence?unit={o.Gulf}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await export.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("person,status,source,due_on,completed_at,version,score,certificate_id", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains(lines, l => l.Contains(code, StringComparison.Ordinal) && l.Contains(",completed,rule,", StringComparison.Ordinal) && l.Contains(",1,100,", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(t["kofi"].Id.ToString(), StringComparison.Ordinal));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.LearningEvidenceExported, o.Course.ToString());

        // Learners have no report; another tenant cannot find the course.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync($"{Api}/reports/courses/{o.Course}")).StatusCode);
        var other = await world.CreateTenantAsync("lro-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/reports/courses/{o.Course}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/reports/courses/{o.Course}/evidence")).StatusCode);
    }

    [Fact]
    public async Task A_manager_sees_only_direct_reports_and_gives_them_catalogue_courses()
    {
        var o = await SetupAsync();
        var t = o.T;
        var team = await OkAsync(t["huda"].Client.GetAsync($"{Api}/team"));
        Assert.Equal(new[] { t["laila"].Id, t["omar"].Id }.Order(), team.EnumerateArray().Select(m => m.GetProperty("person").GetProperty("id").GetGuid()).Order());
        Assert.All(team.EnumerateArray(), m => Assert.Equal("assigned", m.GetProperty("courses")[0].GetProperty("state").GetString()));
        Assert.True((await OkAsync(t["huda"].Client.GetAsync($"{Api}/rights"))).GetProperty("team").GetBoolean());
        Assert.Empty((await OkAsync(t["kofi"].Client.GetAsync($"{Api}/team"))).EnumerateArray());

        // A second course, which Huda gives Laila; not Kofi, who does not report to her, and not a draft.
        var admin = t["admin"].Client;
        var draft = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            openEnrolment = true,
            definition = new { titles = Both("Ethics", "الأخلاقيات"), lessons = new object[] { new { key = "check", kind = "quiz", titles = Both("Check", "اختبار"),
                quiz = new { passMark = 50, attempts = 1, questions = new object[] { new { key = "q1", kind = "true_false", prompts = Both("Be honest.", "كن أمينًا."),
                    options = new object[] { new { key = "t", labels = Both("True", "صح"), correct = true }, new { key = "f", labels = Both("False", "خطأ"), correct = false } } } } } } } },
        }), HttpStatusCode.Created);
        var ethics = draft.GetProperty("id").GetGuid();
        Assert.Contains("validation.learning.team_course",
            await ErrorKeysAsync(await t["huda"].Client.PostAsJsonAsync($"{Api}/team/{t["laila"].Id}/assignments", new { courseId = ethics })));
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{ethics}:publish", new { revision = draft.GetProperty("revision").GetInt32() }));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["huda"].Client.PostAsJsonAsync($"{Api}/team/{t["kofi"].Id}/assignments", new { courseId = ethics })).StatusCode);
        Assert.Contains("validation.learning.team_due", await ErrorKeysAsync(await t["huda"].Client.PostAsJsonAsync($"{Api}/team/{t["laila"].Id}/assignments",
            new { courseId = ethics, dueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1) })));
        team = await OkAsync(t["huda"].Client.PostAsJsonAsync($"{Api}/team/{t["laila"].Id}/assignments", new { courseId = ethics }));
        var lailas = team.EnumerateArray().Single(m => m.GetProperty("person").GetProperty("id").GetGuid() == t["laila"].Id).GetProperty("courses");
        Assert.Contains(lailas.EnumerateArray(), c => c.GetProperty("courseId").GetGuid() == ethics && c.GetProperty("source").GetString() == "manager");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.LearningAssignmentCreated, ethics.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.PostAsJsonAsync($"{Api}/team/{t["omar"].Id}/assignments", new { courseId = ethics })).StatusCode);
    }
}
