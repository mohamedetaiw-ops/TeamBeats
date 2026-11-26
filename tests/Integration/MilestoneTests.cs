using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Milestones;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 R3 T026 (US9 scenario 4, FR-027): without an HR connection an HR officer sets probation and contract end
/// dates by hand and the same reviews and reminders follow; nothing is written back. Only HR officers covering the
/// person set dates, the reminder goes once seven days before while the review is open, a person without a line
/// manager is reviewed by HR, and the plan and other tenants are respected.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class MilestoneTests(TenantWorld world)
{
    private const string Url = "/api/v1/hr/milestones";

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ms-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "noor", "omar", "karim", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "hr_egypt", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "hr_egypt", "Egypt");
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["noor"].Id}/manager", new { managerId = t["karim"].Id })).StatusCode);
        return t;
    }

    private Task<long> NotesAsync(TestTenant t, string user, string template) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private static async Task<IReadOnlyList<JsonElement>> ReviewTasksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == "probation_review").ToList();

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, TestTenant t, string person, string kind, DateOnly? due) =>
        client.PutAsJsonAsync($"{Url}/people/{t[person].Id}/{kind}", new { dueDate = due is { } d ? Iso(d) : null });

    [Fact]
    public async Task An_HR_officer_sets_dates_by_hand_and_the_review_follows_without_write_back()
    {
        var t = await FreshAsync();
        var hr = t["hr_gulf"].Client;

        // Only HR officers covering the person set dates, and only from today on.
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(t["hr_egypt"].Client, t, "noor", "probation", Today.AddDays(5))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(t["karim"].Client, t, "noor", "probation", Today.AddDays(5))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(t["noor"].Client, t, "noor", "probation", Today.AddDays(5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(hr, t, "noor", "probation", Today.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(hr, t, "noor", "holiday", Today.AddDays(5))).StatusCode);

        // Five days left: the review opens at once, and the task itself is the reminder.
        var set = await OkAsync(SetAsync(hr, t, "noor", "probation", Today.AddDays(5)));
        Assert.Equal("manual", set.GetProperty("source").GetString());
        Assert.True(set.GetProperty("reviewOpen").GetBoolean());
        var id = set.GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);
        Assert.Single(await ReviewTasksAsync(t["karim"].Client));
        Assert.Empty(await ReviewTasksAsync(hr));
        Assert.Equal(1L, await NotesAsync(t, "karim", Milestones.ReviewOpenedTemplate));
        Assert.Equal(0L, await NotesAsync(t, "karim", Milestones.ReviewReminderTemplate));

        // Ending at probation closes it; nothing is written back without a connection.
        var ended = await OkAsync(t["karim"].Client.PostAsJsonAsync($"{Url}/{id}/review", new { outcome = "end", comment = "Did not meet the role's goals" }));
        Assert.Equal("closed", ended.GetProperty("status").GetString());
        Assert.Equal("none", ended.GetProperty("reviews")[0].GetProperty("writeBack").GetString());
        Assert.Empty(await ReviewTasksAsync(t["karim"].Client));
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1L, await NotesAsync(t, "hr_gulf", Milestones.ReviewedTemplate));
        Assert.Equal(0L, await NotesAsync(t, "hr_egypt", Milestones.ReviewedTemplate));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.hr_data_change_handoff"));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.HrMilestoneSet)));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.HrMilestoneReviewed)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where changes::text like '%role''s goals%'"));

        // Contract end in 40 days: the unit's HR officers are told once.
        await OkAsync(SetAsync(hr, t, "omar", "contract", Today.AddDays(40)));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<Milestones>().RunDueAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1L, await NotesAsync(t, "hr_gulf", Milestones.ContractAlertTemplate));
        Assert.Equal(0L, await NotesAsync(t, "hr_egypt", Milestones.ContractAlertTemplate));
        // A moved date alerts again; clearing the date removes the milestone.
        await OkAsync(SetAsync(hr, t, "omar", "contract", Today.AddDays(45)));
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(2L, await NotesAsync(t, "hr_gulf", Milestones.ContractAlertTemplate));
        Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(hr, t, "omar", "contract", null)).StatusCode);
        Assert.DoesNotContain((await OkAsync(hr.GetAsync(Url))).EnumerateArray(), m => m.GetProperty("kind").GetString() == "contract");
        Assert.Equal(0, (await OkAsync(t["hr_egypt"].Client.GetAsync(Url))).GetArrayLength());
    }

    [Fact]
    public async Task The_reminder_goes_once_a_week_before_and_HR_reviews_a_person_without_a_manager_and_an_extension_moves_the_date()
    {
        var t = await FreshAsync();
        var hr = t["hr_gulf"].Client;
        // Omar has no line manager, so the HR officers covering him review.
        var set = await OkAsync(SetAsync(hr, t, "omar", "probation", Today.AddDays(25)));
        var id = set.GetProperty("id").GetGuid();
        Assert.True(set.GetProperty("rights").GetProperty("review").GetBoolean());
        await world.DispatchTenantAsync(t.Id);
        Assert.Single(await ReviewTasksAsync(hr));
        Assert.Equal(1L, await NotesAsync(t, "hr_gulf", Milestones.ReviewOpenedTemplate));

        // Weeks later the review is still open and five days are left: one reminder.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update hr.milestone set due_date = @due, review_opened_for = @due where id = @id",
            ("due", Today.AddDays(5)), ("id", id));
        for (var i = 0; i < 2; i++)
        {
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<Milestones>().RunDueAsync(CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return 0;
            });
        }
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1L, await NotesAsync(t, "hr_gulf", Milestones.ReviewReminderTemplate));

        // Omar cannot review himself; an extension of a date set by hand becomes the new date.
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.PostAsJsonAsync($"{Url}/{id}/review", new { outcome = "confirm", comment = "Me" })).StatusCode);
        var newDate = Today.AddDays(65);
        var extended = await OkAsync(hr.PostAsJsonAsync($"{Url}/{id}/review", new { outcome = "extend", newDate = Iso(newDate), comment = "Three more months" }));
        Assert.Equal("open", extended.GetProperty("status").GetString());
        Assert.Equal(Iso(newDate), extended.GetProperty("dueDate").GetString());
        Assert.False(extended.GetProperty("reviewOpen").GetBoolean());
        Assert.Empty(await ReviewTasksAsync(hr));
    }

    [Fact]
    public async Task Milestones_need_the_plan_and_stay_in_their_tenant()
    {
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["hr_gulf"].Client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(basic["hr_gulf"].Client, basic, "noor", "probation", Today.AddDays(5))).StatusCode);

        var t = await FreshAsync();
        var id = (await OkAsync(SetAsync(t["hr_gulf"].Client, t, "noor", "probation", Today.AddDays(5)))).GetProperty("id").GetGuid();
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["hr_gulf"].Client.GetAsync($"{Url}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Url}/{id}/review", new { outcome = "confirm", comment = "x" })).StatusCode);
        Assert.Empty(await ReviewTasksAsync(other["karim"].Client));
    }
}
