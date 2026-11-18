using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T016 (US7, FR-011, FR-012): an export is made in the background with the same minimum group size as the page,
/// is audited when asked for, made and downloaded, and is refused without <c>analytics.export</c>; a schedule to a unit
/// sends each member only what they may open at send time, skips the rest and tells the owner; a report belongs to its
/// recipient alone; and nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AnalyticsReportTests(TenantWorld world)
{
    private const string Exports = "/api/v1/analytics/exports";
    private const string Schedules = "/api/v1/analytics/schedules";
    private const string Reports = "/api/v1/analytics/reports";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private Task<ReportRunner.RunResult> RunAsync(TestTenant t, DateTimeOffset? at = null) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<ReportRunner>().RunAsync(at ?? DateTimeOffset.UtcNow, 200, CancellationToken.None));

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    /// <summary>Plant A has six active people, Plant B and Cairo three each; everyone opened the home page today.</summary>
    private async Task<(TestTenant T, Guid PlantA, Guid PlantB)> PlantsAsync(string package)
    {
        var t = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var plantA = await world.CreateUnitAsync(t, "PlantA", holding, "department");
        var plantB = await world.CreateUnitAsync(t, "PlantB", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var (prefix, unit, count) in new[] { ("a", "PlantA", 6), ("b", "PlantB", 3), ("c", "Cairo", 3) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
            }
        }
        await DispatchAsync();
        foreach (var user in Enumerable.Range(1, 6).Select(i => "a" + i).Concat(Enumerable.Range(1, 3).SelectMany(i => new[] { "b" + i, "c" + i })))
        {
            await OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/analytics/events",
                new { events = new[] { new { name = AnalyticsEvents.PageViewed, channel = AnalyticsChannels.Web, fields = new { area = "home" } } } }), HttpStatusCode.Accepted);
        }
        await DispatchAsync();
        return (t, plantA, plantB);
    }

    private static object Overview(string? groupBy = "unit", object? filters = null) =>
        new { dashboard = ReportDashboards.Overview, metric = Modules.Analytics.Workspace.ActivePeople, groupBy, filters };

    [Fact]
    public async Task Export_is_made_in_the_background_with_the_page_rules_audited_and_kept_to_its_recipient()
    {
        var (t, plantA, _) = await PlantsAsync("enterprise_gov");
        var admin = t["admin"].Client;

        var queued = await OkAsync(admin.PostAsJsonAsync(Exports, new { definition = Overview(), format = "xlsx", name = "Weekly reach" }), HttpStatusCode.Accepted);
        Assert.Equal(ReportStatuses.Queued, queued.GetProperty("status").GetString());
        var id = queued.GetProperty("id").GetGuid();
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AnalyticsExportRequested));

        // Not ready yet: no file.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync($"{Reports}/{id}/file")).StatusCode);

        var run = await RunAsync(t);
        Assert.Equal(1, run.Ready);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AnalyticsExportCompleted));

        var report = await OkAsync(admin.GetAsync($"{Reports}/{id}"));
        Assert.Equal(ReportStatuses.Ready, report.GetProperty("report").GetProperty("status").GetString());
        Assert.True(report.GetProperty("canDownload").GetBoolean());
        // The same answer as the page: twelve people, Plant A alone and the two small units folded into "other".
        var result = report.GetProperty("result");
        Assert.Equal(12, result.GetProperty("total").GetInt32());
        var groups = result.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("group").GetString()!, g => g.GetProperty("value").GetInt32());
        Assert.Equal(6, groups[plantA.ToString()]);
        Assert.Equal(6, groups[ItemAnalytics.Other]);
        var tables = report.GetProperty("tables").EnumerateArray().Select(x => x.GetProperty("key").GetString()).ToList();
        Assert.Contains("summary", tables);
        Assert.Contains("groups", tables);
        Assert.Contains("weeks", tables);

        var csv = await admin.GetAsync($"{Reports}/{id}/file?format=csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.StartsWith("text/csv", csv.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        Assert.Contains("# groups", text, StringComparison.Ordinal);
        Assert.Contains(ItemAnalytics.Other + ",", text, StringComparison.Ordinal);
        var xlsx = await admin.GetAsync($"{Reports}/{id}/file");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        var bytes = await xlsx.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
        Assert.Equal(2, await AuditCountAsync(t, AuditActions.AnalyticsExportDownloaded));

        // The recipient got a notification with the link.
        var links = (await OkAsync(admin.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()).ToArray();
        Assert.Contains(AnalyticsReports.Link(id), links);

        // A report is its recipient's only, even for another analytics viewer.
        await world.AssignAsync(t, "a1", BuiltInRoles.AnalyticsViewer, "tenant", null);
        Assert.Equal(HttpStatusCode.NotFound, (await t["a1"].Client.GetAsync($"{Reports}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["a1"].Client.GetAsync($"{Reports}/{id}/file")).StatusCode);
        Assert.DoesNotContain((await OkAsync(t["a1"].Client.GetAsync(Reports))).GetProperty("reports").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        // Nor does anyone in another tenant see it.
        var other = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Reports}/{id}")).StatusCode);

        // Filters and formats are checked before anything is queued; an employee without analytics.view is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Exports, new { definition = Overview(), format = "pdf" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Exports, new { definition = new { dashboard = "payroll" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Exports, new { definition = Overview("salary") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Exports, new { definition = new { dashboard = ReportDashboards.Service } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["b1"].Client.PostAsJsonAsync(Exports, new { definition = Overview() })).StatusCode);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AnalyticsExportRequested));
    }

    [Fact]
    public async Task Export_is_refused_without_the_export_capability()
    {
        var (t, _, _) = await PlantsAsync("workplace_plus");
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(Exports, new { definition = Overview() })).StatusCode);
        var list = await OkAsync(admin.GetAsync(Reports));
        Assert.False(list.GetProperty("canExport").GetBoolean());
        Assert.True(list.GetProperty("canSchedule").GetBoolean());
        var options = await OkAsync(admin.GetAsync($"{Reports}/options"));
        Assert.False(options.GetProperty("canExport").GetBoolean());
        Assert.Contains(options.GetProperty("units").EnumerateArray(), u => u.GetProperty("names").GetProperty("en").GetString() == "PlantA");

        // A scheduled report is still made, but there is no file to download.
        var schedule = await OkAsync(admin.PostAsJsonAsync(Schedules, new { name = "Reach", definition = Overview(), cadence = ReportCadences.Weekly }), HttpStatusCode.Created);
        await RunAsync(t);
        var report = Assert.Single((await OkAsync(admin.GetAsync(Reports))).GetProperty("reports").EnumerateArray());
        Assert.Equal(schedule.GetProperty("id").GetGuid(), report.GetProperty("scheduleId").GetGuid());
        var id = report.GetProperty("id").GetGuid();
        Assert.False((await OkAsync(admin.GetAsync($"{Reports}/{id}"))).GetProperty("canDownload").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{Reports}/{id}/file")).StatusCode);

        // The workplace package has neither exports nor schedules.
        var basic = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.PostAsJsonAsync(Schedules, new { definition = Overview(), cadence = ReportCadences.Weekly })).StatusCode);
    }

    [Fact]
    public async Task Schedule_to_a_unit_sends_each_member_what_they_may_see_skips_the_rest_and_tells_the_owner()
    {
        var (t, plantA, plantB) = await PlantsAsync("enterprise_gov");
        var admin = t["admin"].Client;
        // a1 may view Plant A only; the other five members of Plant A have no analytics.view.
        await world.AssignAsync(t, "a1", BuiltInRoles.AnalyticsViewer, "org_subtree", plantA);

        // Schedules always send the last period, so dates are refused; a cadence must be weekly or monthly.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Schedules,
            new { definition = Overview(filters: new { from = "2026-01-01" }), cadence = ReportCadences.Weekly })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Schedules, new { definition = Overview(), cadence = "daily" })).StatusCode);
        // A scoped owner may only send to a unit they cover.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a1"].Client.PostAsJsonAsync(Schedules,
            new { definition = Overview(), cadence = ReportCadences.Weekly, recipientUnitId = plantB })).StatusCode);

        var schedule = await OkAsync(admin.PostAsJsonAsync(Schedules,
            new { name = "Plant reach", definition = Overview(), cadence = ReportCadences.Weekly, recipientUnitId = plantA }), HttpStatusCode.Created);
        var scheduleId = schedule.GetProperty("id").GetGuid();
        Assert.Equal("PlantA", schedule.GetProperty("recipientUnitNames").GetProperty("en").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AnalyticsScheduleCreated));

        // The first send is due at once; run it tomorrow, so the seven days before the send include today's visits.
        var now = DateTimeOffset.UtcNow.AddDays(1);
        var run = await RunAsync(t, now);
        Assert.Equal(6, run.Expanded);
        Assert.Equal(1, run.Ready);
        Assert.Equal(5, run.Skipped);
        var report = Assert.Single((await OkAsync(t["a1"].Client.GetAsync(Reports))).GetProperty("reports").EnumerateArray());
        Assert.Equal(ReportStatuses.Ready, report.GetProperty("status").GetString());
        Assert.False(report.GetProperty("mine").GetBoolean());
        var view = await OkAsync(t["a1"].Client.GetAsync($"{Reports}/{report.GetProperty("id").GetGuid()}"));
        // a1's scope is Plant A: six people, not the whole tenant's twelve.
        Assert.Equal(6, view.GetProperty("result").GetProperty("total").GetInt32());
        // a2 has no analytics.view: nothing was sent and their report list is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a2"].Client.GetAsync(Reports)).StatusCode);
        Assert.Equal(5, await world.Database.CountInTenantAsync(t.Id, "select count(*) from analytics.report where status = @status and result is null",
            ("status", ReportStatuses.Skipped)));

        // The owner is told how many were skipped, and is not a recipient (not a member of Plant A).
        var notes = (await OkAsync(admin.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()).ToArray();
        Assert.Contains("/analytics/reports", notes);
        Assert.Empty((await OkAsync(admin.GetAsync(Reports))).GetProperty("reports").EnumerateArray());

        // Nothing more until the next week.
        Assert.Equal(0, (await RunAsync(t, now.AddDays(1))).Expanded);
        var schedules = (await OkAsync(admin.GetAsync(Reports))).GetProperty("schedules").EnumerateArray().ToList();
        Assert.Equal(scheduleId, Assert.Single(schedules).GetProperty("id").GetGuid());

        // Only the owner deletes a schedule; the change is audited.
        Assert.Equal(HttpStatusCode.NotFound, (await t["a1"].Client.DeleteAsync($"{Schedules}/{scheduleId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Schedules}/{scheduleId}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AnalyticsScheduleChanged));
        Assert.Equal(0, (await RunAsync(t, now.AddDays(8))).Expanded);

        // Another tenant cannot send to this tenant's unit.
        var other = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        Assert.Equal(HttpStatusCode.BadRequest, (await other["admin"].Client.PostAsJsonAsync(Schedules,
            new { definition = Overview(), cadence = ReportCadences.Weekly, recipientUnitId = plantA })).StatusCode);
    }
}
