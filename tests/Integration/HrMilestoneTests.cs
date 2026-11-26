using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 R3 T026 (US9 scenarios 1 to 3, FR-027, FR-028) with spec 019: probation and contract end dates come from
/// HR. Thirty days before probation ends the line manager has a review task; sixty days before a contract ends the HR
/// officers of the person's unit are told, once per date. The review's outcome is written back to HR as a data change,
/// never the comment, and a date HR moves reopens the milestone and alerts again. HR officers of a sibling unit and the
/// person themselves see nothing.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string MilestonesUrl = "/api/v1/hr/milestones";

    private async Task<long> NotesAsync(TestTenant t, string user, string template) =>
        await world.Database.TenantScalarAsync<long>(t.Id, $"select count(*) from notifications.notification where user_id = '{t[user].Id}' and template_key = '{template}'");

    private static async Task<JsonElement?> ReviewTaskAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray()
            .Where(i => i.GetProperty("source").GetString() == "probation_review").Cast<JsonElement?>().SingleOrDefault();

    [Fact]
    public async Task Milestones_from_HR_open_reviews_and_alerts_once_and_write_the_outcome_back_without_the_comment()
    {
        var t = await TenantAsync(people: ["sara", "karim", "hr_fin", "hr_sales"]);
        var finance = await world.CreateUnitAsync(t, "Finance", null, "organization");
        var sales = await world.CreateUnitAsync(t, "Sales", null, "organization");
        foreach (var name in new[] { "sara", "karim", "hr_fin" })
        {
            await world.AddMembershipAsync(t, name, "Finance");
        }
        await world.AddMembershipAsync(t, "hr_sales", "Sales");
        await world.AssignAsync(t, "hr_fin", BuiltInRoles.HrOfficer, "org_subtree", finance);
        await world.AssignAsync(t, "hr_sales", BuiltInRoles.HrOfficer, "org_subtree", sales);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["karim"].Id })).StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var probation = today.AddDays(20);
        var (_, hr) = await RestConnectionAsync(t, dataChange: true);
        var sara = Person("R1", t["sara"].Email, "E-100");
        sara["probationEnd"] = Iso(probation);
        sara["contractEnd"] = Iso(today.AddDays(50));
        hr.Employees = [sara];
        await SyncAsync(t);
        await world.DispatchTenantAsync(t.Id);

        // The line manager has the review task, with the start date and role; the person does not.
        var task = await ReviewTaskAsync(t["karim"].Client);
        Assert.NotNull(task);
        Assert.Null(await ReviewTaskAsync(t["sara"].Client));
        var link = task!.Value.GetProperty("link").GetString()!;
        var id = Guid.Parse(link[(link.LastIndexOf('/') + 1)..]);
        var view = await (await t["karim"].Client.GetAsync($"{MilestonesUrl}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(Iso(probation), view.GetProperty("dueDate").GetString());
        Assert.Equal("2024-03-01", view.GetProperty("hireDate").GetString());
        Assert.Equal("Accountant", view.GetProperty("jobTitle").GetString());
        Assert.True(view.GetProperty("rights").GetProperty("review").GetBoolean());
        Assert.Equal(1L, await NotesAsync(t, "karim", "hr.milestone_review_opened"));
        Assert.Equal(1L, await NotesAsync(t, "hr_fin", "hr.milestone_contract_alert"));
        Assert.Equal(0L, await NotesAsync(t, "hr_sales", "hr.milestone_contract_alert"));

        // Another sync sends nothing again.
        await SyncAsync(t);
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1L, await NotesAsync(t, "karim", "hr.milestone_review_opened"));
        Assert.Equal(1L, await NotesAsync(t, "hr_fin", "hr.milestone_contract_alert"));

        // HR officers of the unit see both milestones; a sibling unit's HR officer and the person see nothing.
        var list = await (await t["hr_fin"].Client.GetAsync(MilestonesUrl)).ReadAsync<JsonElement>();
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(0, (await (await t["hr_sales"].Client.GetAsync(MilestonesUrl)).ReadAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_sales"].Client.GetAsync($"{MilestonesUrl}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"{MilestonesUrl}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync(MilestonesUrl)).StatusCode);
        // A date from HR is changed in HR, not here.
        var byHand = await t["hr_fin"].Client.PutAsJsonAsync($"{MilestonesUrl}/people/{t["sara"].Id}/probation", new { dueDate = Iso(today.AddDays(40)) });
        Assert.Equal(HttpStatusCode.Conflict, byHand.StatusCode);
        // Only the reviewer submits.
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["hr_fin"].Client.PostAsJsonAsync($"{MilestonesUrl}/{id}/review", new { outcome = "confirm", comment = "Fine" })).StatusCode);

        var karim = t["karim"].Client;
        var noComment = await karim.PostAsJsonAsync($"{MilestonesUrl}/{id}/review", new { outcome = "confirm" });
        Assert.Contains("validation.hr.milestone_comment", await noComment.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var earlier = await karim.PostAsJsonAsync($"{MilestonesUrl}/{id}/review", new { outcome = "extend", newDate = Iso(probation.AddDays(-1)), comment = "More time" });
        Assert.Contains("validation.hr.milestone_new_date", await earlier.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var extended = probation.AddDays(60);
        var reviewed = await karim.PostAsJsonAsync($"{MilestonesUrl}/{id}/review",
            new { outcome = "extend", newDate = Iso(extended), comment = "Needs more time on the reconciliations" });
        Assert.True(reviewed.IsSuccessStatusCode, await reviewed.Content.ReadAsStringAsync());
        Assert.Equal("reviewed", (await reviewed.ReadAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Null(await ReviewTaskAsync(karim));
        Assert.Equal(1L, await NotesAsync(t, "hr_fin", "hr.milestone_reviewed"));
        Assert.Equal(HttpStatusCode.Conflict, (await karim.PostAsJsonAsync($"{MilestonesUrl}/{id}/review", new { outcome = "confirm", comment = "Again" })).StatusCode);

        // The outcome and the new date go to HR; the comment stays here and is not audited.
        await RunDataChangeJobsAsync(t);
        var sent = Assert.Single(hr.DataChangesSent);
        Assert.Equal("probation", sent.Body.GetProperty("group").GetString());
        Assert.Equal("extend", sent.Body.GetProperty("values").GetProperty("outcome").GetString());
        Assert.Equal(Iso(extended), sent.Body.GetProperty("values").GetProperty("probation_end_date").GetString());
        Assert.DoesNotContain("reconciliations", sent.Body.GetRawText(), StringComparison.Ordinal);
        var after = await (await t["hr_fin"].Client.GetAsync($"{MilestonesUrl}/{id}")).ReadAsync<JsonElement>();
        var review = after.GetProperty("reviews")[0];
        Assert.Equal("recorded", review.GetProperty("writeBack").GetString());
        Assert.Equal("Needs more time on the reconciliations", review.GetProperty("comment").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where changes::text like '%reconciliations%'"));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = 'hr.milestone_written_back'"));

        // HR brings the new date: the milestone reopens. A moved contract date alerts again.
        sara["probationEnd"] = Iso(extended);
        sara["contractEnd"] = Iso(today.AddDays(55));
        await SyncAsync(t);
        await world.DispatchTenantAsync(t.Id);
        var reopened = await (await t["hr_fin"].Client.GetAsync($"{MilestonesUrl}/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("open", reopened.GetProperty("status").GetString());
        Assert.Equal(Iso(extended), reopened.GetProperty("dueDate").GetString());
        Assert.False(reopened.GetProperty("reviewOpen").GetBoolean());
        Assert.Equal(2L, await NotesAsync(t, "hr_fin", "hr.milestone_contract_alert"));
    }
}
