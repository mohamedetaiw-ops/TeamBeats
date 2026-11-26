using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Learning;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 028 R3 T015 (US10, FR-026 to FR-030): a classroom lesson has sessions; learners with the course open take one
/// seat per lesson, wait when a session is full, and move up when someone gives a seat back; booked learners are
/// reminded once the day before; after the start the facilitator records who attended, which completes the course,
/// while an absent learner may book again; learners cannot complete the lesson or take attendance themselves; a
/// cancelled session tells everyone; Workplace has no classroom, and another tenant reaches nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LearningSessionTests(TenantWorld world)
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

    private static async Task<string?> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode status)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString()
            : problem.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString() : null;
    }

    private Task<long> NoticesAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private static object Session(DateTimeOffset starts, int capacity, Guid facilitator, string lessonKey = "practical") =>
        new { lessonKey, startsAt = starts, endsAt = starts.AddHours(2), place = "Training room 2", capacity, facilitatorId = facilitator };

    /// <summary>A published course of one classroom lesson, open for enrolment, with Laila, Omar and Sara enrolled.</summary>
    private async Task<(TestTenant T, Guid Course)> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ls-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "laila", "omar", "sara", "khaled" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var admin = t["admin"].Client;
        var created = await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses", new
        {
            openEnrolment = true,
            definition = new { titles = En("Fire warden"), lessons = new object[] { new { key = "practical", kind = "classroom", titles = En("Practical") } } },
        }), HttpStatusCode.Created);
        var course = created.GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}:publish", new { revision = created.GetProperty("revision").GetInt32(), requiresRetraining = false }));
        foreach (var name in new[] { "laila", "omar", "sara" })
        {
            await OkAsync(t[name].Client.PostAsync($"{Api}/courses/{course}:enrol", null));
        }
        return (t, course);
    }

    [Fact]
    public async Task Seats_waiting_lists_reminders_and_attendance_finish_the_lesson()
    {
        var (t, course) = await SetupAsync();
        var admin = t["admin"].Client;
        var khaled = t["khaled"].Id;
        var tomorrow = DateTimeOffset.UtcNow.AddHours(20);

        Assert.Equal("validation.learning.session_lesson",
            await ProblemKeyAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/sessions", Session(tomorrow, 2, khaled, "nope")), HttpStatusCode.BadRequest));
        Assert.Equal("validation.learning.session_start",
            await ProblemKeyAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/sessions", Session(DateTimeOffset.UtcNow.AddHours(-1), 2, khaled)), HttpStatusCode.BadRequest));
        Assert.Equal("validation.learning.session_facilitator",
            await ProblemKeyAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/sessions", Session(tomorrow, 2, Guid.NewGuid())), HttpStatusCode.BadRequest));
        var sid = (await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/sessions", Session(tomorrow, 2, khaled)), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var later = (await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/sessions", Session(tomorrow.AddDays(7), 10, khaled)), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.LearningSessionChanged);

        // Learners cannot complete a classroom lesson on their own.
        Assert.Equal("conflict.learning.classroom_needs_attendance",
            await ProblemKeyAsync(t["laila"].Client.PostAsync($"{Api}/courses/{course}/lessons/practical:complete", null), HttpStatusCode.Conflict));

        // Two seats: Laila and Omar book, Sara waits; one seat per lesson.
        Assert.Equal("booked", (await OkAsync(t["laila"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null))).GetProperty("mySeat").GetString());
        Assert.Equal("booked", (await OkAsync(t["omar"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null))).GetProperty("mySeat").GetString());
        var sara = await OkAsync(t["sara"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null));
        Assert.Equal("waiting", sara.GetProperty("mySeat").GetString());
        Assert.Equal(1, sara.GetProperty("myPosition").GetInt32());
        Assert.Equal("conflict.learning.seat_held", await ProblemKeyAsync(t["laila"].Client.PostAsync($"{Api}/sessions/{later}/seat", null), HttpStatusCode.Conflict));
        Assert.Equal("conflict.learning.not_enrolled", await ProblemKeyAsync(t["khaled"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null), HttpStatusCode.Conflict));
        var listed = await OkAsync(t["sara"].Client.GetAsync($"{Api}/courses/{course}/lessons/practical/sessions"));
        Assert.Equal(2, listed.GetArrayLength());

        // Omar gives his seat back: Sara gets it and is told.
        Assert.Null((await OkAsync(t["omar"].Client.DeleteAsync($"{Api}/sessions/{sid}/seat"))).GetProperty("mySeat").GetString());
        Assert.Equal("booked", (await OkAsync(t["sara"].Client.GetAsync($"{Api}/courses/{course}/lessons/practical/sessions")))[0].GetProperty("mySeat").GetString());
        Assert.Equal(1L, await NoticesAsync(t, "sara", Sessions.SeatTemplate));
        // Fewer seats than are booked is refused.
        Assert.Equal("validation.learning.session_capacity_booked",
            await ProblemKeyAsync(admin.PutAsJsonAsync($"{Api}/manage/sessions/{sid}", Session(tomorrow, 1, khaled)), HttpStatusCode.BadRequest));

        // The day before, each booked learner is reminded once.
        async Task<int> RemindAsync() => await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var sent = await sp.GetRequiredService<Sessions>().RemindDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return sent;
        });
        Assert.Equal(2, await RemindAsync());
        Assert.Equal(0, await RemindAsync());
        Assert.Equal(1L, await NoticesAsync(t, "laila", Sessions.ReminderTemplate));
        Assert.Equal(0L, await NoticesAsync(t, "omar", Sessions.ReminderTemplate));

        // Before the start nobody records attendance.
        Assert.Equal("conflict.learning.attendance_early", await ProblemKeyAsync(t["khaled"].Client.PutAsJsonAsync($"{Api}/sessions/{sid}/attendance",
            new { entries = new[] { new { learnerId = t["laila"].Id, attended = true } } }), HttpStatusCode.Conflict));

        // The session starts. Laila attended, Sara did not; a learner cannot take attendance, and Omar holds no seat.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update learning.session set starts_at = now() - interval '1 hour', ends_at = now() + interval '1 hour' where id = @id",
            ("id", sid));
        Assert.Equal(HttpStatusCode.NotFound, (await t["laila"].Client.PutAsJsonAsync($"{Api}/sessions/{sid}/attendance",
            new { entries = new[] { new { learnerId = t["laila"].Id, attended = true } } })).StatusCode);
        Assert.Equal("validation.learning.attendance_seat", await ProblemKeyAsync(t["khaled"].Client.PutAsJsonAsync($"{Api}/sessions/{sid}/attendance",
            new { entries = new[] { new { learnerId = t["omar"].Id, attended = true } } }), HttpStatusCode.BadRequest));
        var roster = await OkAsync(t["khaled"].Client.GetAsync($"{Api}/sessions/{sid}/roster"));
        Assert.Equal(2, roster.GetProperty("seats").GetArrayLength());
        Assert.True(roster.GetProperty("session").GetProperty("canTakeAttendance").GetBoolean());
        var recorded = await OkAsync(t["khaled"].Client.PutAsJsonAsync($"{Api}/sessions/{sid}/attendance", new
        {
            entries = new[] { new { learnerId = t["laila"].Id, attended = true }, new { learnerId = t["sara"].Id, attended = false } },
        }));
        Assert.Equal(["absent", "attended"], recorded.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("status").GetString()).Order());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.LearningAttendanceRecorded);

        // Attending was the last lesson: Laila's course is complete with a certificate.
        var lailaCourse = await OkAsync(t["laila"].Client.GetAsync($"{Api}/courses/{course}"));
        Assert.Equal("completed", lailaCourse.GetProperty("me").GetProperty("state").GetString());
        Assert.Equal("conflict.learning.not_enrolled", await ProblemKeyAsync(t["laila"].Client.PostAsync($"{Api}/sessions/{later}/seat", null), HttpStatusCode.Conflict));
        // Sara was absent: the lesson stays open and she books the next session.
        Assert.Equal("booked", (await OkAsync(t["sara"].Client.PostAsync($"{Api}/sessions/{later}/seat", null))).GetProperty("mySeat").GetString());
        Assert.Contains((await OkAsync(t["khaled"].Client.GetAsync($"{Api}/sessions/facilitating"))).EnumerateArray(), s => s.GetProperty("id").GetGuid() == sid);
    }

    [Fact]
    public async Task A_cancelled_session_tells_everyone_and_Workplace_and_other_tenants_reach_nothing()
    {
        var (t, course) = await SetupAsync();
        var admin = t["admin"].Client;
        var sid = (await OkAsync(admin.PostAsJsonAsync($"{Api}/manage/courses/{course}/sessions", Session(DateTimeOffset.UtcNow.AddDays(3), 1, t["khaled"].Id)),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["laila"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null));
        await OkAsync(t["omar"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null));

        // Only the course's administrators manage it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["khaled"].Client.PostAsync($"{Api}/manage/sessions/{sid}:cancel", null)).StatusCode);
        Assert.Equal("cancelled", (await OkAsync(admin.PostAsync($"{Api}/manage/sessions/{sid}:cancel", null))).GetProperty("status").GetString());
        Assert.Equal(1L, await NoticesAsync(t, "laila", Sessions.CancelledTemplate));
        Assert.Equal(1L, await NoticesAsync(t, "omar", Sessions.CancelledTemplate));
        Assert.Equal("conflict.learning.session_closed", await ProblemKeyAsync(t["sara"].Client.PostAsync($"{Api}/sessions/{sid}/seat", null), HttpStatusCode.Conflict));

        var other = await world.CreateTenantAsync("ls-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/sessions/{sid}/roster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/manage/courses/{course}/sessions")).StatusCode);

        // Workplace: no classroom, so no sessions and no course with a classroom lesson.
        var w = await world.CreateTenantAsync("ls-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await w["admin"].Client.GetAsync($"{Api}/sessions/facilitating")).StatusCode);
    }
}
