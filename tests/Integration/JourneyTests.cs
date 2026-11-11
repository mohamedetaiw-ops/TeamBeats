using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T018 (US4 scenarios 1, 2, 6 and 7, US5 scenario 1, FR-013, FR-014, edge cases): HR officers define journey
/// templates with steps relative to an anchor date; HR, directory and service events start one journey per template,
/// person and event, and a later event moves the anchor and reschedules what is not open yet. Steps run on the process
/// engine, go to the person, the buddy, the line manager, named people or a permission at the unit, show in the inbox
/// and complete the journey. A no-show cancels onboarding and starts offboarding. HR officers stay in their subtree,
/// other tenants see nothing, and a plan without <c>hr.journeys</c> refuses.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class JourneyTests(TenantWorld world)
{
    private const string Templates = "/api/v1/hr/journey-templates";
    private const string Journeys = "/api/v1/hr/journeys";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<(TestTenant T, Guid Gulf, Guid Egypt)> FreshAsync(string plan = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("jr-" + Guid.NewGuid().ToString("N")[..8], plan);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "karim", "rana", "kofi", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var name in new[] { "nadia", "hr_egypt" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Egypt");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        foreach (var name in new[] { "mona", "rana" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[name].Id}/manager", new { managerId = t["kofi"].Id })).StatusCode);
        }
        return (t, gulf, egypt);
    }

    private Task<DateOnly> TodayAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById((await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync()).TimeZone);
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        });

    private static object OnboardingSteps(Guid karim) => new object[]
    {
        new { key = "welcome", names = Names("Read the welcome pack", "اقرأ دليل الترحيب"), offsetDays = -10, assignee = "person" },
        new { key = "laptop", names = Names("Prepare the laptop", "جهّز الحاسوب"), offsetDays = -7, assignee = "users", users = new[] { karim }, dueWorkingDays = 3 },
        new { key = "buddy_lunch", names = Names("Take the new joiner to lunch", "اصطحب الموظف الجديد للغداء"), offsetDays = 0, assignee = "buddy" },
        new { key = "checkin_30", names = Names("30 day check-in", "متابعة بعد 30 يوماً"), offsetDays = 30, assignee = "manager" },
    };

    private static async Task<JsonElement> CreateTemplateAsync(TestTenant t, object body, string user = "admin")
    {
        var response = await t[user].Client.PostAsJsonAsync(Templates, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<JsonElement>();
    }

    private static object Onboarding(TestTenant t, string key = "onboarding", string startOn = "hire", Guid? unit = null) => new
    {
        key, kind = "onboarding", names = Names("Onboarding", "التعيين"), orgUnitId = unit, startOn, steps = OnboardingSteps(t["karim"].Id),
    };

    private static object Offboarding(string key = "offboarding", string startOn = "termination", Guid? serviceId = null, string? anchorField = null) => new
    {
        key, kind = "offboarding", names = Names("Offboarding", "إنهاء الخدمة"), startOn, serviceId, anchorField,
        steps = new object[]
        {
            new { key = "handover", names = Names("Knowledge hand-over", "تسليم المعرفة"), offsetDays = -5, assignee = "manager" },
            new { key = "access", names = Names("Remove access", "إزالة الصلاحيات"), offsetDays = 0, assignee = "permission", permission = Permissions.HrManage },
        },
    };

    private static async Task<string[]> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private Task HandleAsync<TEvent>(TestTenant t, TEvent message) where TEvent : TeamBeats.Primitives.IIntegrationEvent =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<JourneyStarter>().HandleAsync((dynamic)message, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task<int> TickAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var opened = await sp.GetRequiredService<JourneyService>().OpenDueAsync(null, default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return opened;
        });

    private static async Task<JsonElement[]> MineAsync(TestTenant t, string user) =>
        (await (await t[user].Client.GetAsync("/api/v1/me/journeys")).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    private static async Task<JsonElement> JourneyAsync(TestTenant t, string user, Guid id)
    {
        var response = await t[user].Client.GetAsync($"{Journeys}/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<JsonElement>();
    }

    private static JsonElement Step(JsonElement journey, string key) => journey.GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().Where(i => i.GetProperty("source").GetString() == "journey").ToArray();

    private static async Task<HttpResponseMessage> CompleteAsync(TestTenant t, string user, Guid journey, string step)
    {
        var view = await JourneyAsync(t, user, journey);
        var task = Step(view, step).GetProperty("myTaskId");
        Assert.Equal(JsonValueKind.String, task.ValueKind);
        return await t[user].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetGuid()}/decision", new { outcome = "approve" });
    }

    [Fact]
    public async Task Templates_are_validated_versioned_audited_and_kept_to_the_officers_units()
    {
        var (t, gulf, egypt) = await FreshAsync();

        var invalid = await t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            key = "Bad Key", kind = "onboarding", names = Names("Onboarding", "التعيين"), startOn = "termination",
            steps = new object[]
            {
                new { key = "a", names = Names("A", "أ"), offsetDays = 400, assignee = "users" },
                new { key = "queue", names = Names("Queue", "قائمة"), offsetDays = 0, assignee = "permission", permission = "not.a_permission" },
                new { key = "queue", names = Names("Again", "مجدداً"), offsetDays = 0, assignee = "person", dueWorkingDays = 0 },
            },
        });
        Assert.Equal(
            ["validation.journey.due", "validation.journey.key", "validation.journey.offset", "validation.journey.permission", "validation.journey.start_kind",
                "validation.journey.step_key", "validation.journey.users"],
            (await ErrorsAsync(invalid)).Distinct().Order(StringComparer.Ordinal).ToArray());

        var created = await CreateTemplateAsync(t, Onboarding(t));
        Assert.Equal((1, "\"v1\"", 4), (created.GetProperty("version").GetInt32(), created.GetProperty("eTag").GetString(), created.GetProperty("steps").GetArrayLength()));
        var id = created.GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrJourneyTemplateChanged, "onboarding");

        // One active template per start and unit, so one event starts one journey.
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync(Templates, Onboarding(t, "onboarding_two"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync(Templates, Onboarding(t))).StatusCode);

        // Each version publishes its own routes, so open journeys keep theirs; a stale If-Match is refused.
        using (var stale = new HttpRequestMessage(HttpMethod.Put, $"{Templates}/{id}") { Content = JsonContent.Create(Onboarding(t)) })
        {
            stale.Headers.TryAddWithoutValidation("If-Match", "\"v9\"");
            Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.SendAsync(stale)).StatusCode);
        }
        using (var update = new HttpRequestMessage(HttpMethod.Put, $"{Templates}/{id}") { Content = JsonContent.Create(Onboarding(t)) })
        {
            update.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
            var updated = await t["admin"].Client.SendAsync(update);
            Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
            Assert.Equal(2, (await updated.ReadAsync<JsonElement>()).GetProperty("version").GetInt32());
        }
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from workflow.process_definition where key in ('hr.journey.onboarding.1.welcome', 'hr.journey.onboarding.2.welcome')"));

        // An officer of one unit manages that unit's templates only; a tenant-wide one needs tenant-wide hr.manage.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_gulf"].Client.PostAsJsonAsync(Templates, Offboarding())).StatusCode);
        var gulfOnly = await CreateTemplateAsync(t, Onboarding(t, "gulf_onboarding", unit: gulf), "hr_gulf");
        var gulfId = gulfOnly.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Templates}/{gulfId}")).StatusCode);
        var egyptList = await (await t["hr_egypt"].Client.GetAsync(Templates)).ReadAsync<JsonElement>();
        Assert.DoesNotContain(egyptList.EnumerateArray(), x => x.GetProperty("id").GetGuid() == gulfId);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync(Templates)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_egypt"].Client.PostAsJsonAsync(Templates, Onboarding(t, "egypt_wrong", unit: gulf))).StatusCode);
        _ = egypt;

        // Another tenant sees nothing.
        var other = await world.CreateTenantAsync("jr-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Templates}/{id}")).StatusCode);
        Assert.Empty((await (await other["admin"].Client.GetAsync(Templates)).ReadAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task A_hire_starts_one_onboarding_journey_whose_steps_run_in_the_inbox_and_move_with_the_start_date()
    {
        var (t, _, _) = await FreshAsync();
        await CreateTemplateAsync(t, Onboarding(t));
        var today = await TodayAsync(t);
        var change = Guid.NewGuid();

        // Scenario 1: the hire starts one journey; a second delivery of the same event changes nothing.
        await HandleAsync(t, new EmployeeHired(t["mona"].Id, "R1", today.AddDays(5), Guid.NewGuid(), change));
        await HandleAsync(t, new EmployeeHired(t["mona"].Id, "R1", today.AddDays(5), Guid.NewGuid(), change));
        var mine = Assert.Single(await MineAsync(t, "mona"));
        var id = mine.GetProperty("id").GetGuid();
        Assert.Equal(("onboarding", "active", 4, 2), (mine.GetProperty("kind").GetString(), mine.GetProperty("status").GetString(), mine.GetProperty("steps").GetInt32(),
            mine.GetProperty("open").GetInt32()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrJourneyStarted, id.ToString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync(nameof(JourneyStarted), t.Id));

        // Steps whose date has come are open, each with its assignee: the person, and the named people.
        var view = await JourneyAsync(t, "mona", id);
        Assert.Equal(("open", "open", "scheduled", "scheduled"), (Step(view, "welcome").GetProperty("status").GetString(), Step(view, "laptop").GetProperty("status").GetString(),
            Step(view, "buddy_lunch").GetProperty("status").GetString(), Step(view, "checkin_30").GetProperty("status").GetString()));
        Assert.Equal(today.AddDays(5).ToString("yyyy-MM-dd"), Step(view, "buddy_lunch").GetProperty("scheduledOn").GetString());
        Assert.Single(await InboxAsync(t["mona"].Client));
        var karimsTask = Assert.Single(await InboxAsync(t["karim"].Client));
        Assert.Contains($"/hr/journeys/{id}", karimsTask.GetProperty("link").GetString(), StringComparison.Ordinal);
        Assert.True(Step(await JourneyAsync(t, "karim", id), "laptop").TryGetProperty("dueAt", out var due) && due.ValueKind == JsonValueKind.String);

        // Nobody completes someone else's step; the person cannot complete a step about them that is not theirs.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t["mona"].Client.PostAsJsonAsync($"/api/v1/tasks/{karimsTask.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" })).StatusCode);
        Assert.True((await CompleteAsync(t, "mona", id, "welcome")).IsSuccessStatusCode);
        Assert.Equal(("done", "done"), (Step(await JourneyAsync(t, "mona", id), "welcome").GetProperty("status").GetString(),
            Step(await JourneyAsync(t, "mona", id), "welcome").GetProperty("outcome").GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrJourneyStepCompleted, id.ToString());

        // Scenario 2: the start date moves with a later event; steps not open yet are rescheduled, open ones stay.
        await HandleAsync(t, new EmployeeHired(t["mona"].Id, "R1", today.AddDays(20), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Single(await MineAsync(t, "mona"));
        view = await JourneyAsync(t, "hr_gulf", id);
        Assert.Equal(today.AddDays(20).ToString("yyyy-MM-dd"), view.GetProperty("journey").GetProperty("anchorDate").GetString());
        Assert.Equal(today.AddDays(20).ToString("yyyy-MM-dd"), Step(view, "buddy_lunch").GetProperty("scheduledOn").GetString());
        Assert.Equal("open", Step(view, "laptop").GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrJourneyRescheduled, id.ToString());

        // HR moves the date into the past: every step is due, the worker finds nothing more to open.
        var moved = await t["hr_gulf"].Client.PutAsJsonAsync($"{Journeys}/{id}/anchor", new { anchorDate = today.AddDays(-30) });
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        Assert.Equal(0, await TickAsync(t));
        view = await moved.ReadAsync<JsonElement>();
        Assert.All(view.GetProperty("steps").EnumerateArray(), s => Assert.NotEqual("scheduled", s.GetProperty("status").GetString()));

        // No buddy: HR officers of the unit take the buddy step. The line manager takes the check-in.
        Assert.True((await CompleteAsync(t, "hr_gulf", id, "buddy_lunch")).IsSuccessStatusCode);
        Assert.True((await CompleteAsync(t, "kofi", id, "checkin_30")).IsSuccessStatusCode);
        Assert.True((await CompleteAsync(t, "karim", id, "laptop")).IsSuccessStatusCode);

        // The journey completes when every step is done.
        var done = await JourneyAsync(t, "hr_gulf", id);
        Assert.Equal(("completed", 4), (done.GetProperty("journey").GetProperty("status").GetString(), done.GetProperty("journey").GetProperty("done").GetInt32()));
        Assert.Equal(1L, await world.Database.OutboxCountAsync(nameof(JourneyCompleted), t.Id));
        Assert.Empty(await InboxAsync(t["karim"].Client));
    }

    [Fact]
    public async Task Journeys_are_seen_by_the_person_their_assignees_and_the_officers_of_their_unit_only()
    {
        var (t, _, _) = await FreshAsync();
        await CreateTemplateAsync(t, Onboarding(t));
        var today = await TodayAsync(t);
        await HandleAsync(t, new EmployeeHired(t["mona"].Id, "R1", today, Guid.NewGuid(), Guid.NewGuid()));
        var id = Assert.Single(await MineAsync(t, "mona")).GetProperty("id").GetGuid();

        Assert.False((await JourneyAsync(t, "karim", id)).GetProperty("canManage").GetBoolean());
        Assert.True((await JourneyAsync(t, "hr_gulf", id)).GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["rana"].Client.GetAsync($"{Journeys}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Journeys}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.PostAsJsonAsync($"{Journeys}/{id}/cancel", new { reason = "Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync(Journeys)).StatusCode);

        // The dashboard shows the officer's units, with progress and overdue steps (US4 scenarios 6 and 7).
        var gulf = (await (await t["hr_gulf"].Client.GetAsync(Journeys)).ReadAsync<JsonElement>()).EnumerateArray().ToArray();
        var row = Assert.Single(gulf);
        Assert.Equal((t["mona"].Id, 4), (row.GetProperty("person").GetProperty("id").GetGuid(), row.GetProperty("steps").GetInt32()));
        Assert.Empty((await (await t["hr_egypt"].Client.GetAsync(Journeys)).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with c as (update hr.journey_step set due_at = now() - interval '1 day' where key = 'laptop' returning 1) select count(*) from c"));
        var overdue = Assert.Single((await (await t["hr_gulf"].Client.GetAsync($"{Journeys}?overdue=true")).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(1, overdue.GetProperty("overdue").GetInt32());
        Assert.True(Step(await JourneyAsync(t, "karim", id), "laptop").GetProperty("overdue").GetBoolean());

        // Another tenant sees nothing.
        var other = await world.CreateTenantAsync("jr-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Journeys}/{id}")).StatusCode);
        Assert.Empty((await (await other["admin"].Client.GetAsync(Journeys)).ReadAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task A_no_show_cancels_onboarding_and_starts_offboarding_and_a_termination_starts_offboarding()
    {
        var (t, _, _) = await FreshAsync();
        await CreateTemplateAsync(t, Onboarding(t, "manual_onboarding", "manual"));
        await CreateTemplateAsync(t, Offboarding());
        var today = await TodayAsync(t);

        // HR starts a journey by hand with a buddy; a second one for the same person and template is refused.
        var start = new { templateKey = "manual_onboarding", userId = t["rana"].Id, anchorDate = today, buddyUserId = t["kofi"].Id };
        var started = await t["hr_gulf"].Client.PostAsJsonAsync(Journeys, start);
        Assert.True(started.StatusCode == HttpStatusCode.Created, await started.Content.ReadAsStringAsync());
        var id = (await started.ReadAsync<JsonElement>()).GetProperty("journey").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await t["hr_gulf"].Client.PostAsJsonAsync(Journeys, start)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.PostAsJsonAsync(Journeys, start)).StatusCode);
        Assert.Contains("validation.journey.buddy", await ErrorsAsync(await t["hr_gulf"].Client.PostAsJsonAsync(Journeys,
            new { templateKey = "manual_onboarding", userId = t["mona"].Id, anchorDate = today, buddyUserId = t["mona"].Id })));
        Assert.True((await CompleteAsync(t, "kofi", id, "buddy_lunch")).IsSuccessStatusCode);

        // Cancel needs a reason; a no-show withdraws open steps and starts offboarding today (edge cases).
        Assert.Contains("validation.journey.reason", await ErrorsAsync(await t["hr_gulf"].Client.PostAsJsonAsync($"{Journeys}/{id}/cancel", new { reason = " " })));
        var cancelled = await t["hr_gulf"].Client.PostAsJsonAsync($"{Journeys}/{id}/cancel", new { reason = "Did not show up on day one", noShow = true });
        Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        var view = await cancelled.ReadAsync<JsonElement>();
        Assert.Equal("cancelled", view.GetProperty("journey").GetProperty("status").GetString());
        Assert.Equal(("done", "cancelled"), (Step(view, "buddy_lunch").GetProperty("status").GetString(), Step(view, "welcome").GetProperty("status").GetString()));
        Assert.Empty(await InboxAsync(t["rana"].Client));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrJourneyCancelled, id.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await t["hr_gulf"].Client.PostAsJsonAsync($"{Journeys}/{id}/cancel", new { reason = "Again" })).StatusCode);
        var offboarding = (await MineAsync(t, "rana")).Single(j => j.GetProperty("kind").GetString() == "offboarding");
        Assert.Equal(today.ToString("yyyy-MM-dd"), offboarding.GetProperty("anchorDate").GetString());

        // US5 scenario 1: an HR termination starts offboarding on the last working day; the manager hands over first.
        await HandleAsync(t, new EmployeeTerminated(t["mona"].Id, "R1", today.AddDays(3), false, Guid.NewGuid(), Guid.NewGuid()));
        var leaving = Assert.Single(await MineAsync(t, "mona"));
        Assert.Equal(today.AddDays(3).ToString("yyyy-MM-dd"), leaving.GetProperty("anchorDate").GetString());
        var leavingView = await JourneyAsync(t, "kofi", leaving.GetProperty("id").GetGuid());
        Assert.Equal(("open", "scheduled"), (Step(leavingView, "handover").GetProperty("status").GetString(), Step(leavingView, "access").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task New_accounts_and_approved_resignations_start_journeys_and_a_plan_without_journeys_starts_nothing()
    {
        var (t, _, _) = await FreshAsync();
        await CreateTemplateAsync(t, Onboarding(t, "account_onboarding", "account"));
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/services/admin", new { kind = "form", names = Names("Resignation", "استقالة"), classification = "internal" });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var serviceId = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        var form = new { fields = new object[] { new { key = "last_day", type = "date", labels = Names("Last working day", "آخر يوم عمل") } } };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/services/admin/{serviceId}/draft", new { form, allowOnBehalf = false, maxAttachments = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/v1/services/admin/{serviceId}/publish", new { })).StatusCode);
        Assert.Contains("validation.journey.anchor_field", await ErrorsAsync(await admin.PostAsJsonAsync(Templates, Offboarding("resignation", "service", serviceId))));
        await CreateTemplateAsync(t, Offboarding("resignation", "service", serviceId, "last_day"));
        var today = await TodayAsync(t);

        // A new account with no HR hire date starts onboarding today, through the registered event consumer.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<TeamBeats.Messaging.IOutbox>().EnqueueAsync(new UserProvisioned(t["karim"].Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        await world.Factory.Services.GetRequiredService<TeamBeats.Messaging.OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        Assert.Equal(today.ToString("yyyy-MM-dd"), Assert.Single(await MineAsync(t, "karim")).GetProperty("anchorDate").GetString());

        // An approved resignation starts offboarding on the date in its form; a rejected one starts nothing.
        var submitted = await t["rana"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId, data = new { last_day = today.AddDays(14).ToString("yyyy-MM-dd") } });
        Assert.True(submitted.StatusCode == HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var requestId = (await submitted.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
        await HandleAsync(t, new RequestCompleted(requestId, serviceId, "rejected"));
        Assert.Empty(await MineAsync(t, "rana"));
        await HandleAsync(t, new RequestCompleted(requestId, serviceId, "approved"));
        var resignation = Assert.Single(await MineAsync(t, "rana"));
        Assert.Equal(("offboarding", today.AddDays(14).ToString("yyyy-MM-dd")), (resignation.GetProperty("kind").GetString(), resignation.GetProperty("anchorDate").GetString()));

        // The plan decides: without hr.journeys events start nothing and every endpoint refuses.
        var basic = await world.CreateTenantAsync("jr-b-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        await world.SeedUserAsync(basic, "omar", MembershipTypes.Employee);
        await HandleAsync(basic, new UserProvisioned(basic["omar"].Id, "directory"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(basic.Id, "select count(*) from hr.journey"));
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(Templates)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(Journeys)).StatusCode);
    }
}
