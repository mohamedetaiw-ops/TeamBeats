using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 slice 3 (T011, US6, FR-009): the team calendar shows the space's events, milestones, task due dates and the
/// members' approved leave without its type, and never pending leave or people outside the space. Members add events,
/// viewers read; free and busy comes through spec 018 as the viewer. Other tenants see nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpaceCalendarTests(TenantWorld world)
{
    private const string Api = "/api/v1/spaces";
    private const string Leave = "/api/v1/hr/leave";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    private static DateOnly Monday()
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        while (d.DayOfWeek != DayOfWeek.Monday)
        {
            d = d.AddDays(1);
        }
        return d;
    }

    /// <summary>Omar manages Laila and Kofi; all work in Gulf. Annual leave of 21 days is set up.</summary>
    private async Task<TestTenant> SetupAsync()
    {
        var t = await world.CreateTenantAsync("sc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "omar", "laila", "huda", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var person in new[] { "laila", "kofi" })
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[person].Id}/manager", new { managerId = t["omar"].Id }));
        }
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Leave}/types/annual", new { names = new { en = "Annual leave", ar = "إجازة سنوية" }, sensitive = false }));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Leave}/policies", new { typeKey = "annual", orgUnitId = (Guid?)null, bands = new[] { new { days = 21m } }, accrual = "yearly" }),
            HttpStatusCode.Created);
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var written = await sp.GetRequiredService<LeaveLedger>().RunAccrualAsync(default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return written;
        });
        return t;
    }

    private static async Task<Guid> RequestLeaveAsync(TestTenant t, string user, DateOnly start, DateOnly end) =>
        (await OkAsync(t[user].Client.PostAsJsonAsync($"{Leave}/requests", new { typeKey = "annual", start, end }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task ApproveAsync(HttpClient manager, Guid requestId)
    {
        var tasks = await OkAsync(manager.GetAsync("/api/v1/tasks"));
        var task = tasks.EnumerateArray().Single(i => i.GetProperty("source").GetString() == "leave"
            && i.GetProperty("link").GetString()!.Contains(requestId.ToString(), StringComparison.Ordinal));
        await OkAsync(manager.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
    }

    [Fact]
    public async Task The_team_calendar_shows_events_milestones_due_dates_and_approved_leave_without_its_type()
    {
        var t = await SetupAsync();
        var monday = Monday();
        var omar = t["omar"].Client;
        var space = (await OkAsync(omar.PostAsJsonAsync(Api, new
        {
            name = "Year-end close", description = "Closing the books.", visibility = "private", template = "project",
            members = new object[] { new { userId = t["laila"].Id, role = "member" }, new { userId = t["huda"].Id, role = "viewer" } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Laila's approved leave shows; Kofi's (not in the space) does not, and neither does a pending request.
        await ApproveAsync(omar, await RequestLeaveAsync(t, "laila", monday, monday.AddDays(1)));
        await ApproveAsync(omar, await RequestLeaveAsync(t, "kofi", monday, monday));
        await RequestLeaveAsync(t, "laila", monday.AddDays(3), monday.AddDays(3));

        var milestone = (await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/milestones", new { name = "Trial balance", dueOn = monday.AddDays(2) })))[0];
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Reconcile the bank", dueOn = monday.AddDays(4), assignees = new[] { t["laila"].Id } }),
            HttpStatusCode.Created);
        var startsAt = new DateTimeOffset(monday.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var created = await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/events", new
        {
            title = "Close kick-off", description = "Agenda in the library.", startsAt, endsAt = startsAt.AddHours(1), allDay = false,
        }), HttpStatusCode.Created);

        var calendar = await OkAsync(t["huda"].Client.GetAsync($"{Api}/{space}/calendar?from={monday:yyyy-MM-dd}&to={monday.AddDays(6):yyyy-MM-dd}"));
        Assert.False(calendar.GetProperty("canEdit").GetBoolean());
        Assert.Equal("Close kick-off", Assert.Single(calendar.GetProperty("events").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(milestone.GetProperty("id").GetGuid(), Assert.Single(calendar.GetProperty("milestones").EnumerateArray()).GetProperty("id").GetGuid());
        var due = Assert.Single(calendar.GetProperty("tasks").EnumerateArray());
        Assert.Equal("Reconcile the bank", due.GetProperty("title").GetString());
        Assert.Equal(t["laila"].Id, due.GetProperty("assignees")[0].GetProperty("userId").GetGuid());
        var away = Assert.Single(calendar.GetProperty("away").EnumerateArray());
        Assert.Equal(t["laila"].Id, away.GetProperty("person").GetProperty("userId").GetGuid());
        Assert.Equal(monday.AddDays(2).ToString("yyyy-MM-dd"), away.GetProperty("end").GetString());
        Assert.DoesNotContain("annual", calendar.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Annual leave", calendar.GetRawText(), StringComparison.Ordinal);

        // Members change events with the version; viewers cannot; times and ranges are checked.
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(t["laila"].Client.PutAsJsonAsync($"{Api}/{space}/events/{id}", new
        {
            title = "Close kick-off (moved)", description = "", startsAt = startsAt.AddDays(1), endsAt = startsAt.AddDays(1).AddHours(1), allDay = false, version = 1,
        }));
        Assert.Equal("conflict.spaces.event_changed", await ProblemKeyAsync(t["laila"].Client.PutAsJsonAsync($"{Api}/{space}/events/{id}", new
        {
            title = "Again", startsAt, endsAt = startsAt.AddHours(1), version = 1,
        }), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["huda"].Client.PostAsJsonAsync($"{Api}/{space}/events", new
        {
            title = "Mine", startsAt, endsAt = startsAt.AddHours(1),
        })).StatusCode);
        Assert.Equal("validation.spaces.event_time", await ProblemKeyAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/{space}/events", new
        {
            title = "Backwards", startsAt, endsAt = startsAt.AddHours(-1),
        }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.calendar_range",
            await ProblemKeyAsync(omar.GetAsync($"{Api}/{space}/calendar?from={monday:yyyy-MM-dd}&to={monday.AddDays(90):yyyy-MM-dd}"), HttpStatusCode.BadRequest));
        await OkAsync(t["laila"].Client.DeleteAsync($"{Api}/{space}/events/{id}"), HttpStatusCode.NoContent);
        Assert.Equal(3, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.SpacesEventChanged)));

        // Free and busy is read as the viewer through Outlook; while free and busy is not turned on the page is told so and shows nobody.
        var availability = await OkAsync(t["laila"].Client.GetAsync($"{Api}/{space}/calendar/availability?date={monday:yyyy-MM-dd}"));
        Assert.Equal("turned_off", availability.GetProperty("state").GetString());
        Assert.Empty(availability.GetProperty("members").EnumerateArray());

        // Kofi is not in the private space, and nobody in another tenant reaches it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["kofi"].Client.GetAsync($"{Api}/{space}/calendar")).StatusCode);
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["laila"].Client.GetAsync($"{Api}/{space}/calendar")).StatusCode);
    }
}
