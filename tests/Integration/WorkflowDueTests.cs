using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 024 T006: working-day due times on user tasks from the calendar of the instance's unit, a reminder job that
/// publishes <c>UserTaskDue</c> once even when delivered twice, recalculation after a holiday change, and jobs that stay
/// in their tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class WorkflowDueTests(TenantWorld world)
{
    private static readonly int[] SundayToThursday = [1, 2, 3, 4, 7];

    private sealed record Org(TestTenant T, Guid Gulf, JsonElement Calendar);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("wfd-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.SeedUserAsync(t, "requester", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "legal", MembershipTypes.Employee);
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/hr/calendars",
            new { name = new { en = "Gulf week", ar = "أسبوع الخليج" }, weekDays = SundayToThursday });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var calendar = await created.ReadAsync<JsonElement>();
        var assigned = await t["admin"].Client.PutAsJsonAsync($"/api/v1/hr/assignments/{gulf}", new { calendarId = calendar.GetProperty("id").GetGuid() });
        Assert.True(assigned.IsSuccessStatusCode);
        await DispatchAsync();
        return new Org(t, gulf, calendar);
    }

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private Task<T> InTenantAsync<T>(TestTenant t, string? user, Func<IServiceProvider, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            if (user is not null)
            {
                sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.User, t[user].Id);
            }
            var result = await action(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private static RouteSpec DueRoute(TestTenant t, int days) => new("Contract approval",
    [
        new RouteStep("legal", "Legal review", new CandidateRule(Users: [t["legal"].Id]), days),
        new RouteStep("final", "Final review", new CandidateRule(Users: [t["legal"].Id])),
    ]);

    private async Task<UserTaskInfo> StartAsync(Org o, string businessKey, int days = 2)
    {
        var t = o.T;
        await InTenantAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().PublishRouteAsync("service.contract", RouteSources.ServiceRoute, DueRoute(t, days)));
        var instance = await InTenantAsync(t, "requester", sp => sp.GetRequiredService<IProcessEngine>().StartAsync("service.contract", businessKey, o.Gulf, t["requester"].Id));
        return Assert.Single(await InTenantAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().InstanceTasksAsync(instance.Id)));
    }

    private Task<WorkingCalendarSnapshot> CalendarAsync(Org o) =>
        world.Factory.InScopeAsync(o.T.Id, sp => sp.GetRequiredService<IWorkingCalendar>().ForUnitAsync(o.Gulf));

    /// <summary>The working day a due time closes: the instant just before it, as a date in the calendar's zone.</summary>
    private static DateOnly DueDay(WorkingCalendarSnapshot calendar, DateTimeOffset dueAt) => calendar.Today(dueAt.AddTicks(-1));

    [Fact]
    public async Task A_due_step_is_due_at_the_end_of_the_nth_working_day_of_the_units_calendar()
    {
        var o = await FreshAsync();
        var task = await StartAsync(o, "REQ-1", days: 3);
        var calendar = await CalendarAsync(o);

        Assert.NotNull(task.DueAt);
        var day = DueDay(calendar, task.DueAt!.Value);
        Assert.Equal(calendar.AddWorkingDays(calendar.Today(task.CreatedAt), 3), day);
        Assert.NotEqual(DayOfWeek.Friday, day.DayOfWeek);
        Assert.NotEqual(DayOfWeek.Saturday, day.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(task.DueAt.Value, calendar.Zone).TimeOfDay);
        Assert.Equal(1, await world.Database.OutboxCountAsync("UserTaskCreated", o.T.Id));
    }

    [Fact]
    public async Task A_holiday_on_the_due_day_moves_the_due_time_to_the_next_working_day()
    {
        var o = await FreshAsync();
        var task = await StartAsync(o, "REQ-2");
        var before = await CalendarAsync(o);
        var dueDay = DueDay(before, task.DueAt!.Value);

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{o.Calendar.GetProperty("id").GetGuid()}/holidays")
        {
            Content = JsonContent.Create(new { year = dueDay.Year, holidays = new[] { new { date = dueDay.ToString("yyyy-MM-dd"), name = new { en = "Holiday" }, halfDay = false } } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"v{o.Calendar.GetProperty("version").GetInt32()}\"");
        Assert.Equal(HttpStatusCode.OK, (await o.T["admin"].Client.SendAsync(request)).StatusCode);
        await DispatchAsync();

        var after = await CalendarAsync(o);
        var moved = (await InTenantAsync(o.T, null, sp => sp.GetRequiredService<IProcessEngine>().GetTaskAsync(task.Id)))!;
        var movedDay = DueDay(after, moved.DueAt!.Value);
        Assert.True(movedDay > dueDay);
        Assert.Equal(after.NextWorkingDayOnOrAfter(dueDay.AddDays(1)), movedDay);

        // The reminder moved with it: it does not run at the old time, and runs at the new one.
        Assert.Equal(0, await InTenantAsync(o.T, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(task.DueAt!.Value.AddMinutes(1), 100)));
        Assert.Equal(1, await InTenantAsync(o.T, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(moved.DueAt!.Value.AddMinutes(1), 100)));
    }

    [Fact]
    public async Task The_reminder_publishes_UserTaskDue_once_even_when_the_job_is_delivered_twice()
    {
        var o = await FreshAsync();
        var task = await StartAsync(o, "REQ-3");
        var t = o.T;

        Assert.Equal(0, await InTenantAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(task.DueAt!.Value.AddMinutes(-1), 100)));
        Assert.Equal(1, await InTenantAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(task.DueAt!.Value.AddDays(30), 100)));
        var jobId = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<Guid>(
            "select id from workflow.engine_job where task_id = @taskId", new { taskId = task.Id }));
        Assert.False(await InTenantAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunAsync(jobId)));
        Assert.Equal(0, await InTenantAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(task.DueAt!.Value.AddDays(30), 100)));

        Assert.Equal(1, await world.Database.OutboxCountAsync("UserTaskDue", t.Id));
        var history = await InTenantAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().HistoryAsync(task.InstanceId));
        Assert.Single(history, h => h.Event == "task_due" && h.ElementId == "legal");
        // A step without a due time creates no job.
        await InTenantAsync(t, "legal", sp => sp.GetRequiredService<IProcessEngine>().CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null));
        var next = (await InTenantAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().InstanceTasksAsync(task.InstanceId)))[1];
        Assert.Null(next.DueAt);
    }

    [Fact]
    public async Task A_task_completed_or_withdrawn_before_its_due_time_sends_no_reminder()
    {
        var o = await FreshAsync();
        var t = o.T;
        var completed = await StartAsync(o, "REQ-4");
        var withdrawn = await StartAsync(o, "REQ-5");
        await InTenantAsync(t, "legal", sp => sp.GetRequiredService<IProcessEngine>().CompleteTaskAsync(completed.Id, TaskOutcomes.Reject, null));
        await InTenantAsync(t, "requester", sp => sp.GetRequiredService<IProcessEngine>().WithdrawAsync(withdrawn.InstanceId));

        Assert.Equal(0, await InTenantAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(DateTimeOffset.UtcNow.AddDays(400), 100)));
        Assert.Equal(0, await world.Database.OutboxCountAsync("UserTaskDue", t.Id));
    }

    [Fact]
    public async Task Jobs_run_only_in_their_own_tenant()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var task = await StartAsync(a, "REQ-6");

        Assert.Equal(0, await InTenantAsync(b.T, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(task.DueAt!.Value.AddDays(1), 100)));
        var jobId = await world.Factory.InScopeAsync(a.T.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<Guid>(
            "select id from workflow.engine_job where task_id = @taskId", new { taskId = task.Id }));
        Assert.False(await InTenantAsync(b.T, null, sp => sp.GetRequiredService<EngineJobs>().RunAsync(jobId)));
        Assert.Equal(0, await world.Database.OutboxCountAsync("UserTaskDue", a.T.Id));
        Assert.Equal(1, await InTenantAsync(a.T, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(task.DueAt!.Value.AddDays(1), 100)));
    }

    [Fact]
    public void Due_times_for_a_zone_end_at_local_midnight_after_the_due_day()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        var calendar = new WorkingCalendarSnapshot(null, 1, [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], [], zone);
        // Thursday 2026-11-05 10:00 in Dubai; one working day later is Sunday 2026-11-08, due at Monday 00:00 local.
        var due = DueTimes.For(calendar, new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.Zero), 1);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 20, 0, 0, TimeSpan.Zero), due);
    }
}
