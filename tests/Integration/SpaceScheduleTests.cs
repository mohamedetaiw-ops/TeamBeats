using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 R3 T013 and T014: start dates, "blocked by" links with the loop check, the timeline with its conflicts,
/// completing before a blocker, time logged on tasks with author and owner rules, owner totals and CSV, viewers,
/// archived spaces and the tenant boundary. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpaceScheduleTests(TenantWorld world)
{
    private const string Api = "/api/v1/spaces";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    /// <summary>Omar owns a private project space; Laila and Sara are members and Huda a viewer.</summary>
    private async Task<(TestTenant T, Guid Space)> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ss-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var unit = await world.CreateUnitAsync(t, "Gulf", null, "organization");
        foreach (var name in new[] { "omar", "laila", "huda", "sara", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        _ = unit;
        var body = new
        {
            name = "ERP rollout", description = "Moving to the new ERP.", visibility = "private", template = "project",
            members = new object[] { new { userId = t["laila"].Id, role = "member" }, new { userId = t["sara"].Id, role = "member" }, new { userId = t["huda"].Id, role = "viewer" } },
        };
        var space = (await OkAsync(t["omar"].Client.PostAsJsonAsync(Api, body), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return (t, space);
    }

    private static async Task<(Guid Id, int Version)> TaskAsync(HttpClient client, Guid space, string title)
    {
        var detail = await OkAsync(client.PostAsJsonAsync($"{Api}/{space}/tasks", new { title }), HttpStatusCode.Created);
        return (detail.GetProperty("task").GetProperty("id").GetGuid(), detail.GetProperty("task").GetProperty("version").GetInt32());
    }

    private static Task<JsonElement> ScheduleAsync(HttpClient client, Guid space, Guid task, DateOnly? start, DateOnly? due, int version, int? estimate = null) =>
        OkAsync(client.PutAsJsonAsync($"{Api}/{space}/tasks/{task}/schedule", new { startOn = start, dueOn = due, estimateMinutes = estimate, version }));

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Blockers_are_kept_without_loops_and_the_timeline_shows_conflicts()
    {
        var (t, space) = await SetupAsync();
        var omar = t["omar"].Client;
        var laila = t["laila"].Client;
        var design = await TaskAsync(omar, space, "Design data model");
        var migrate = await TaskAsync(omar, space, "Migrate customer data");
        var train = await TaskAsync(omar, space, "Train finance team");

        // Dates and an estimate together; a start after the due date is refused.
        Assert.Equal("validation.spaces.task_dates", await ProblemKeyAsync(await omar.PutAsJsonAsync($"{Api}/{space}/tasks/{design.Id}/schedule",
            new { startOn = Today.AddDays(5), dueOn = Today.AddDays(2), version = design.Version }), HttpStatusCode.BadRequest));
        var d = await ScheduleAsync(omar, space, design.Id, Today, Today.AddDays(10), design.Version, 600);
        Assert.Equal(600, d.GetProperty("estimateMinutes").GetInt32());
        var m = await ScheduleAsync(laila, space, migrate.Id, Today.AddDays(5), Today.AddDays(20), migrate.Version);
        await ScheduleAsync(laila, space, train.Id, Today.AddDays(21), Today.AddDays(25), train.Version);

        // Migrate waits for design, train waits for migrate; train blocking design would close a loop.
        var linked = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{migrate.Id}/blockers", new { taskId = design.Id }));
        Assert.Equal(design.Id, linked.GetProperty("blockedBy")[0].GetProperty("id").GetGuid());
        // Design is due on day 10 and migrate starts on day 5: a schedule conflict (US8 scenario 3).
        Assert.True(linked.GetProperty("conflict").GetBoolean());
        await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{train.Id}/blockers", new { taskId = migrate.Id }));
        Assert.Equal("conflict.spaces.task_loop", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{design.Id}/blockers", new { taskId = train.Id }),
            HttpStatusCode.Conflict));
        Assert.Equal("validation.spaces.task_blocker", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{design.Id}/blockers", new { taskId = design.Id }),
            HttpStatusCode.BadRequest));
        var blocks = await OkAsync(omar.GetAsync($"{Api}/{space}/tasks/{design.Id}/schedule"));
        Assert.Equal(migrate.Id, blocks.GetProperty("blocks")[0].GetProperty("id").GetGuid());

        // A task in another space cannot be a blocker.
        var otherSpace = (await OkAsync(omar.PostAsJsonAsync(Api, new { name = "Office move", description = "", visibility = "private", template = "project" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var elsewhere = await TaskAsync(omar, otherSpace, "Pack boxes");
        Assert.Equal("validation.spaces.task_blocker", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{design.Id}/blockers", new { taskId = elsewhere.Id }),
            HttpStatusCode.BadRequest));

        // The viewer reads the timeline but changes nothing.
        var huda = t["huda"].Client;
        var timeline = await OkAsync(huda.GetAsync($"{Api}/{space}/timeline"));
        Assert.False(timeline.GetProperty("canEdit").GetBoolean());
        Assert.Equal(Today.ToString("yyyy-MM-dd"), timeline.GetProperty("from").GetString());
        Assert.Equal(Today.AddDays(25).ToString("yyyy-MM-dd"), timeline.GetProperty("to").GetString());
        var tasks = timeline.GetProperty("tasks").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        Assert.Equal(3, tasks.Count);
        Assert.True(tasks[migrate.Id].GetProperty("conflict").GetBoolean());
        Assert.False(tasks[train.Id].GetProperty("conflict").GetBoolean());
        Assert.Equal(2, timeline.GetProperty("links").GetArrayLength());
        // The template's starter tasks have no dates and stay in the plan as undated.
        Assert.Equal(3, timeline.GetProperty("undated").GetArrayLength());
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.PostAsJsonAsync($"{Api}/{space}/tasks/{train.Id}/blockers", new { taskId = design.Id }),
            HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.PutAsJsonAsync($"{Api}/{space}/tasks/{train.Id}/schedule",
            new { startOn = Today, version = 1 }), HttpStatusCode.Forbidden));

        // Moving design earlier clears the conflict; completing migrate before design is allowed and noted (US8 scenario 4).
        await ScheduleAsync(omar, space, design.Id, Today, Today.AddDays(4), d.GetProperty("version").GetInt32(), 600);
        Assert.False((await OkAsync(omar.GetAsync($"{Api}/{space}/tasks/{migrate.Id}/schedule"))).GetProperty("conflict").GetBoolean());
        await OkAsync(laila.PostAsync($"{Api}/{space}/tasks/{migrate.Id}:complete", null));
        Assert.True((await OkAsync(laila.GetAsync($"{Api}/{space}/tasks/{migrate.Id}/schedule"))).GetProperty("finishedBeforeBlocker").GetBoolean());

        // The task form cannot set a due date before the start.
        var current = await OkAsync(omar.GetAsync($"{Api}/{space}/tasks/{train.Id}"));
        Assert.Equal("validation.spaces.task_dates", await ProblemKeyAsync(await omar.PutAsJsonAsync($"{Api}/{space}/tasks/{train.Id}",
            new { title = "Train finance team", dueOn = Today, version = current.GetProperty("task").GetProperty("version").GetInt32() }), HttpStatusCode.BadRequest));

        // Removing a link, and deleting a blocker removes its links.
        await OkAsync(laila.DeleteAsync($"{Api}/{space}/tasks/{train.Id}/blockers/{migrate.Id}"));
        await OkAsync(omar.DeleteAsync($"{Api}/{space}/tasks/{design.Id}"), HttpStatusCode.NoContent);
        Assert.Equal(0, (await OkAsync(omar.GetAsync($"{Api}/{space}/timeline"))).GetProperty("links").GetArrayLength());

        // Kofi is not in the private space, and another tenant never finds it.
        Assert.Equal("authz.denied.spaces_private", await ProblemKeyAsync(await t["kofi"].Client.GetAsync($"{Api}/{space}/timeline"), HttpStatusCode.Forbidden));
        var other = await world.CreateTenantAsync("sso-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/{space}/timeline")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Api}/{space}/tasks/{train.Id}/schedule")).StatusCode);
    }

    [Fact]
    public async Task Members_log_time_authors_change_it_and_owners_see_totals()
    {
        var (t, space) = await SetupAsync();
        var omar = t["omar"].Client;
        var laila = t["laila"].Client;
        var sara = t["sara"].Client;
        var task = await TaskAsync(omar, space, "Migrate customer data");
        var other = await TaskAsync(omar, space, "Clean vendor list");
        await ScheduleAsync(omar, space, task.Id, null, null, task.Version, 240);

        var logged = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time", new { workedOn = Today, minutes = 90, note = "Mapped the fields" }));
        Assert.Equal(90, logged.GetProperty("loggedMinutes").GetInt32());
        Assert.Equal(240, logged.GetProperty("estimateMinutes").GetInt32());
        var entry = logged.GetProperty("entries")[0];
        Assert.True(entry.GetProperty("canEdit").GetBoolean());
        await OkAsync(sara.PostAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time", new { workedOn = Today.AddDays(-1), minutes = 30 }));
        await OkAsync(sara.PostAsJsonAsync($"{Api}/{space}/tasks/{other.Id}/time", new { workedOn = Today, minutes = 45, note = "=cmd" }));

        // Rules: no future days, 1 to 1,440 minutes; only the author edits; owners may remove any.
        Assert.Equal("validation.spaces.time_day", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time",
            new { workedOn = Today.AddDays(2), minutes = 30 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.time_minutes", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time",
            new { workedOn = Today, minutes = 1441 }), HttpStatusCode.BadRequest));
        var entryId = entry.GetProperty("id").GetGuid();
        Assert.Equal("authz.denied.spaces_time_entry", await ProblemKeyAsync(await sara.PutAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time/{entryId}",
            new { workedOn = Today, minutes = 10 }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.spaces_time_entry", await ProblemKeyAsync(await sara.DeleteAsync($"{Api}/{space}/tasks/{task.Id}/time/{entryId}"), HttpStatusCode.Forbidden));
        var changed = await OkAsync(laila.PutAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time/{entryId}", new { workedOn = Today, minutes = 120, note = "Mapped and checked" }));
        Assert.Equal(150, changed.GetProperty("loggedMinutes").GetInt32());

        // The viewer reads but cannot log; members cannot see totals.
        var huda = t["huda"].Client;
        Assert.False((await OkAsync(huda.GetAsync($"{Api}/{space}/tasks/{task.Id}/time"))).GetProperty("canLog").GetBoolean());
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.PostAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time",
            new { workedOn = Today, minutes = 30 }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.spaces_owner", await ProblemKeyAsync(await laila.GetAsync($"{Api}/{space}/time"), HttpStatusCode.Forbidden));

        // The owner's totals per person and per task, and the CSV with formulas made harmless.
        var totals = await OkAsync(omar.GetAsync($"{Api}/{space}/time"));
        Assert.Equal(195, totals.GetProperty("minutes").GetInt32());
        var people = totals.GetProperty("people").EnumerateArray().ToDictionary(p => p.GetProperty("person").GetProperty("userId").GetGuid(), p => p.GetProperty("minutes").GetInt32());
        Assert.Equal(120, people[t["laila"].Id]);
        Assert.Equal(75, people[t["sara"].Id]);
        Assert.Equal(task.Id, totals.GetProperty("tasks")[0].GetProperty("taskId").GetGuid());
        Assert.Equal(150, totals.GetProperty("tasks")[0].GetProperty("minutes").GetInt32());
        Assert.Equal("validation.spaces.time_range", await ProblemKeyAsync(await omar.GetAsync($"{Api}/{space}/time?from={Today.AddDays(-400):yyyy-MM-dd}&to={Today:yyyy-MM-dd}"),
            HttpStatusCode.BadRequest));
        var csv = await omar.GetAsync($"{Api}/{space}/time.csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("'=cmd", text, StringComparison.Ordinal);
        Assert.Contains("Mapped and checked", text, StringComparison.Ordinal);

        // The owner removes Sara's entry; an archived space takes no more time.
        var saraEntry = (await OkAsync(omar.GetAsync($"{Api}/{space}/tasks/{task.Id}/time"))).GetProperty("entries").EnumerateArray()
            .First(e => e.GetProperty("person").GetProperty("userId").GetGuid() == t["sara"].Id);
        Assert.True(saraEntry.GetProperty("canRemove").GetBoolean());
        Assert.False(saraEntry.GetProperty("canEdit").GetBoolean());
        Assert.Equal(120, (await OkAsync(omar.DeleteAsync($"{Api}/{space}/tasks/{task.Id}/time/{saraEntry.GetProperty("id").GetGuid()}"))).GetProperty("loggedMinutes").GetInt32());
        await OkAsync(omar.PostAsync($"{Api}/{space}:archive", null));
        Assert.Equal("conflict.spaces.archived", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{task.Id}/time",
            new { workedOn = Today, minutes = 30 }), HttpStatusCode.Conflict));

        // Another tenant never reaches the time of the space.
        var stranger = (await world.CreateTenantAsync("sto-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus"))["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Api}/{space}/time")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Api}/{space}/tasks/{task.Id}/time")).StatusCode);
    }

    [Fact]
    public async Task Status_summaries_use_only_the_readers_space_and_respect_the_ai_policy()
    {
        var (t, space) = await SetupAsync();
        var omar = t["omar"].Client;
        var marker = "Quokka" + Guid.NewGuid().ToString("N")[..6];
        await TaskAsync(omar, space, $"{marker} ledger import");
        // Another space of Omar's must never reach the prompt.
        var other = (await OkAsync(omar.PostAsJsonAsync(Api, new { name = "Office move", description = "", visibility = "private", template = "project" }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        var secret = "Wombat" + Guid.NewGuid().ToString("N")[..6];
        await TaskAsync(omar, other, $"{secret} boxes");
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/status", new { status = "at_risk", note = "Vendor is late." }));

        var before = world.Factory.OpenRouter.UserPrompts.Count;
        var summary = await OkAsync(t["huda"].Client.PostAsync($"{Api}/{space}/status-summary", null));
        Assert.Equal("summarized", summary.GetProperty("outcome").GetString());
        Assert.True(summary.GetProperty("aiGenerated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(summary.GetProperty("summary").GetString()));
        var prompts = world.Factory.OpenRouter.UserPrompts.Skip(before).ToList();
        Assert.Contains(prompts, p => p.Contains(marker, StringComparison.Ordinal) && p.Contains("Vendor is late.", StringComparison.Ordinal));
        Assert.DoesNotContain(prompts, p => p.Contains(secret, StringComparison.Ordinal));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a and target_id = @s",
            ("a", TeamBeats.Audit.Contracts.AuditActions.SpacesStatusSummarized), ("s", space.ToString())));

        // Kofi is not in the space; another tenant never finds it.
        Assert.Equal("authz.denied.spaces_private", await ProblemKeyAsync(await t["kofi"].Client.PostAsync($"{Api}/{space}/status-summary", null), HttpStatusCode.Forbidden));
        var stranger = (await world.CreateTenantAsync("ssx-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus"))["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"{Api}/{space}/status-summary", null)).StatusCode);

        // A Confidential space is blocked by the default AI policy, and nothing is sent.
        var classified = (await OkAsync(omar.PostAsJsonAsync(Api, new { name = "Board pay review", description = "", visibility = "private", template = "project", classification = "confidential" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        before = world.Factory.OpenRouter.UserPrompts.Count;
        var blocked = await OkAsync(omar.PostAsync($"{Api}/{classified}/status-summary", null));
        Assert.Equal("policy_blocked", blocked.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("summary").ValueKind);
        Assert.Equal(before, world.Factory.OpenRouter.UserPrompts.Count);

        // When the gateway cannot answer, the reader is told and nothing changes.
        world.Factory.OpenRouter.Unavailable = true;
        try
        {
            Assert.Equal("unavailable", (await OkAsync(omar.PostAsync($"{Api}/{space}/status-summary", null))).GetProperty("outcome").GetString());
        }
        finally
        {
            world.Factory.OpenRouter.Unavailable = false;
        }
        Assert.Equal("at_risk", (await OkAsync(omar.GetAsync($"{Api}/{space}/overview"))).GetProperty("status").GetProperty("status").GetString());
    }
}
