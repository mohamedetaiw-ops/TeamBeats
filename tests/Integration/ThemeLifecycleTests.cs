using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T014 (US2, FR-012): the theme lifecycle. A brand keeps one working draft that employees never see. A publisher
/// publishes it as the next version or schedules it for a tenant-local time, checked again at the time; an editor without
/// theme.publish submits it, and a publisher approves or returns it from the inbox; the submitter never decides. Every version
/// records who published it, how and why, and a revert publishes an earlier version again as a new one. Other tenants and
/// employees reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ThemeLifecycleTests(TenantWorld world)
{
    private const string Brands = "/api/v1/experience/brands";

    private static object Token(string value) => new Dictionary<string, object> { ["$value"] = value };

    private static Dictionary<string, object> Radius(string value) => new() { ["radius.md"] = Token(value) };

    private async Task<(TestTenant T, Guid Brand)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("thl-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "manager", MembershipTypes.Employee);
        await world.AssignAsync(t, "manager", "brand_manager", "tenant", null);
        var created = await t["admin"].Client.PostAsJsonAsync(Brands, new { name = "Campaign", tokens = Radius("12px") });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var brand = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/brand-assignments/tenant", new { brandId = brand }));
        return (t, brand);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Task<JsonElement> DraftAsync(TestTenant t, Guid brand, string radius, string user = "admin") =>
        OkAsync(t[user].Client.PutAsJsonAsync($"{Brands}/{brand}/draft", new { name = "Campaign", tokens = Radius(radius) }));

    /// <summary>The radius employees get from the effective theme of the whole tenant.</summary>
    private static async Task<string> LiveRadiusAsync(TestTenant t)
    {
        var theme = await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/theme?scope=tenant");
        return theme.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("mode").GetString() == "light" && v.GetProperty("density").GetString() == "comfortable")
            .GetProperty("tokens").GetProperty("radius.md").GetString()!;
    }

    private static async Task<JsonElement[]> TasksAsync(TestTenant t, string user) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == ThemeApprovals.Source).ToArray();

    private Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private Task<int> RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<ThemeService>().RunDueSchedulesAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private static object At(DateTime local) => local.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_draft_stays_unseen_until_published_and_every_version_records_how_it_went_live()
    {
        var (t, brand) = await FreshAsync();
        var none = await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}/draft"));
        Assert.False(none.GetProperty("hasDraft").GetBoolean());
        Assert.Equal("12px", none.GetProperty("tokens").GetProperty("radius.md").GetProperty("$value").GetString());
        var nothing = await t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/draft/publish", new { });
        Assert.Equal("conflict.theme.no_draft", await KeyAsync(nothing));

        var draft = await DraftAsync(t, brand, "4px");
        Assert.True(draft.GetProperty("hasDraft").GetBoolean());
        Assert.Equal(1, draft.GetProperty("baseVersion").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeDrafted, brand.ToString());
        Assert.Equal("12px", await LiveRadiusAsync(t));

        var tooLong = await t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/draft/publish", new { reason = new string('x', 501) });
        Assert.Equal("validation.theme.reason_too_long", await KeyAsync(tooLong));
        var published = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/draft/publish", new { reason = "Softer corners" }));
        Assert.Equal(2, published.GetProperty("version").GetInt32());
        Assert.Equal("4px", await LiveRadiusAsync(t));
        Assert.False((await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}/draft"))).GetProperty("hasDraft").GetBoolean());

        // A revert publishes version 1 again as version 3; nothing is rewritten, and the version in force cannot be reverted to.
        var same = await t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/revert", new { version = 2 });
        Assert.Equal("validation.theme.revert_version", await KeyAsync(same));
        var missing = await t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/revert", new { version = 9 });
        Assert.Equal("validation.theme.revert_version", await KeyAsync(missing));
        var reverted = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/revert", new { version = 1, reason = "Back to launch look" }));
        Assert.Equal(3, reverted.GetProperty("version").GetInt32());
        Assert.Equal("12px", await LiveRadiusAsync(t));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeRolledBack, brand.ToString());

        var releases = (await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}/releases"))).EnumerateArray().ToList();
        Assert.Equal([3, 2, 1], releases.Select(r => r.GetProperty("version").GetInt32()));
        Assert.Equal(["reverted", "direct", "direct"], releases.Select(r => r.GetProperty("how").GetString()));
        Assert.True(releases[0].GetProperty("current").GetBoolean());
        Assert.Equal(1, releases[0].GetProperty("rollbackOf").GetInt32());
        Assert.Equal("Softer corners", releases[1].GetProperty("reason").GetString());
        Assert.Equal(t["admin"].Id, releases[1].GetProperty("publishedBy").GetGuid());

        // A discarded draft leaves the version in force alone.
        await DraftAsync(t, brand, "20px");
        await OkAsync(t["admin"].Client.DeleteAsync($"{Brands}/{brand}/draft"), HttpStatusCode.NoContent);
        Assert.False((await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}/draft"))).GetProperty("hasDraft").GetBoolean());
        Assert.Equal("12px", await LiveRadiusAsync(t));
    }

    [Fact]
    public async Task An_editor_submits_and_a_publisher_returns_or_approves_while_the_submitter_never_decides()
    {
        var (t, brand) = await FreshAsync();
        await DraftAsync(t, brand, "6px", "manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["manager"].Client.PostAsJsonAsync($"{Brands}/{brand}/draft/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["manager"].Client.PostAsJsonAsync($"{Brands}/{brand}/revert", new { version = 1 })).StatusCode);
        var manager = await OkAsync(t["manager"].Client.GetAsync($"{Brands}/{brand}/approval"));
        Assert.False(manager.GetProperty("publish").GetBoolean());

        var submitted = await OkAsync(t["manager"].Client.PostAsJsonAsync($"{Brands}/{brand}/submit", new { reason = "Ramadan look" }));
        Assert.Equal("pending", submitted.GetProperty("status").GetString());
        Assert.False(submitted.GetProperty("decide").GetBoolean());
        var change = Assert.Single(submitted.GetProperty("changes").EnumerateArray());
        Assert.Equal("radius.md", change.GetProperty("token").GetString());
        Assert.Equal("changed", change.GetProperty("change").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeSubmitted, brand.ToString());
        Assert.Equal("conflict.theme.in_approval", await KeyAsync(await t["manager"].Client.PostAsJsonAsync($"{Brands}/{brand}/submit", new { })));
        Assert.Equal("conflict.theme.in_approval", await KeyAsync(await t["admin"].Client.PostAsJsonAsync($"{Brands}/{brand}/draft/publish", new { })));

        // Only publishers get the task; employees see neither the task, the submission nor the draft, and the theme is unchanged.
        Assert.Empty(await TasksAsync(t, "manager"));
        Assert.Empty(await TasksAsync(t, "employee"));
        var task = Assert.Single(await TasksAsync(t, "admin"));
        Assert.Equal($"/admin/branding?brand={brand}&task={task.GetProperty("taskId").GetString()}", task.GetProperty("link").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.GetAsync($"{Brands}/{brand}/approval")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync($"{Brands}/{brand}/draft")).StatusCode);
        Assert.Equal("12px", await LiveRadiusAsync(t));
        Assert.True((await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}/approval"))).GetProperty("pending").GetProperty("decide").GetBoolean());

        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetString()}/decision", new { outcome = "return", comment = "Too sharp" }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeReturned, brand.ToString());
        Assert.Equal(1, await NotificationsAsync(t, "manager", ThemeApprovals.ReturnedTemplate));
        var returned = (await OkAsync(t["manager"].Client.GetAsync($"{Brands}/{brand}/approval"))).GetProperty("last");
        Assert.Equal("returned", returned.GetProperty("status").GetString());
        Assert.Equal("Too sharp", returned.GetProperty("comment").GetString());

        // Edited and sent again; editing after sending leaves the submission as sent, and approval publishes exactly that.
        await DraftAsync(t, brand, "8px", "manager");
        await OkAsync(t["manager"].Client.PostAsJsonAsync($"{Brands}/{brand}/submit", new { }));
        await DraftAsync(t, brand, "30px", "manager");
        var second = Assert.Single(await TasksAsync(t, "admin"));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/tasks/{second.GetProperty("taskId").GetString()}/decision", new { outcome = "approve" }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeApproved, brand.ToString());
        Assert.Equal(1, await NotificationsAsync(t, "manager", ThemeApprovals.ApprovedTemplate));
        Assert.Equal("8px", await LiveRadiusAsync(t));
        var approved = (await OkAsync(t["manager"].Client.GetAsync($"{Brands}/{brand}/approval"))).GetProperty("last");
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(2, approved.GetProperty("version").GetInt32());
        Assert.Equal("approved", (await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}/releases")))[0].GetProperty("how").GetString());

        // A withdrawn submission leaves the inbox.
        await OkAsync(t["manager"].Client.PostAsJsonAsync($"{Brands}/{brand}/submit", new { }));
        Assert.Single(await TasksAsync(t, "admin"));
        await OkAsync(t["manager"].Client.PostAsync($"{Brands}/{brand}/withdraw", null), HttpStatusCode.NoContent);
        Assert.Empty(await TasksAsync(t, "admin"));
    }

    [Fact]
    public async Task A_scheduled_theme_goes_live_at_its_local_time_and_one_failing_the_checks_then_stays_out()
    {
        var (t, brand) = await FreshAsync();
        var client = t["admin"].Client;
        await DraftAsync(t, brand, "2px");
        var past = await client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(DateTime.UtcNow.AddDays(-1)) });
        Assert.Equal("validation.theme.schedule_time", await KeyAsync(past));
        var far = await client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(DateTime.UtcNow.AddYears(2)) });
        Assert.Equal("validation.theme.schedule_time", await KeyAsync(far));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t["manager"].Client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(DateTime.UtcNow.AddDays(2)) })).StatusCode);

        var local = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2).AddHours(9), DateTimeKind.Unspecified);
        var state = await OkAsync(client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(local), reason = "National day" }));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(state.GetProperty("timeZone").GetString()!);
        var goLive = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
        Assert.Equal(goLive, state.GetProperty("current").GetProperty("goLiveAt").GetDateTimeOffset());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeScheduled, brand.ToString());
        Assert.Equal("conflict.theme.scheduled", await KeyAsync(await client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(local.AddHours(1)) })));

        Assert.Equal(0, await RunAsync(t, goLive.AddMinutes(-1)));
        Assert.Equal("12px", await LiveRadiusAsync(t));
        Assert.Equal(1, await RunAsync(t, goLive));
        Assert.Equal("2px", await LiveRadiusAsync(t));
        var live = (await OkAsync(client.GetAsync($"{Brands}/{brand}/schedule"))).GetProperty("current");
        Assert.Equal("live", live.GetProperty("status").GetString());
        Assert.Equal(2, live.GetProperty("version").GetInt32());
        var release = (await OkAsync(client.GetAsync($"{Brands}/{brand}/releases")))[0];
        Assert.Equal("scheduled", release.GetProperty("how").GetString());
        Assert.Equal("National day", release.GetProperty("reason").GetString());

        // A schedule kept with values that fail contrast at the time does not go live; the publisher is told.
        await DraftAsync(t, brand, "16px");
        await OkAsync(client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(local) }));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id,
            """
            with changed as (update experience.brand_schedule set tokens = '{"color.shell.text": {"$value": "#3b3f8f"}}'::jsonb where status = 'scheduled' returning 1)
            select count(*)::int from changed
            """));
        Assert.Equal(1, await RunAsync(t, goLive));
        var failed = (await OkAsync(client.GetAsync($"{Brands}/{brand}/schedule"))).GetProperty("current");
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("2px", await LiveRadiusAsync(t));
        Assert.Equal(1, await NotificationsAsync(t, "admin", ThemeService.ScheduleFailedTemplate));

        // A waiting schedule can be cancelled; with none waiting, cancelling is a conflict.
        await OkAsync(client.PutAsJsonAsync($"{Brands}/{brand}/schedule", new { goLiveAt = At(local) }));
        await OkAsync(client.DeleteAsync($"{Brands}/{brand}/schedule"), HttpStatusCode.NoContent);
        Assert.Equal("conflict.theme.not_scheduled", await KeyAsync(await client.DeleteAsync($"{Brands}/{brand}/schedule")));
        Assert.Equal(0, await RunAsync(t, goLive.AddDays(1)));
    }

    [Fact]
    public async Task Another_tenant_reaches_no_draft_release_schedule_or_approval()
    {
        var (t, brand) = await FreshAsync();
        await DraftAsync(t, brand, "4px");
        var other = world.B["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Brands}/{brand}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Brands}/{brand}/draft", new { name = "x", tokens = Radius("1px") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Brands}/{brand}/draft/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Brands}/{brand}/releases")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Brands}/{brand}/revert", new { version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Brands}/{brand}/schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Brands}/{brand}/approval")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Brands}/{brand}/submit", new { })).StatusCode);
        Assert.Equal("12px", await LiveRadiusAsync(t));
    }
}
