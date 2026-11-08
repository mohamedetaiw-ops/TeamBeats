using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 T016: the line manager approver rule and Team requests. The step goes to the current manager of the person
/// the request is for, from the reporting line (spec 010) and never from org unit membership; a manager change moves an
/// open step; no manager, or a manager who filed the request themselves, falls back to the step's permission; the
/// manager's delegate acts on their behalf; Team requests lists direct and indirect reports only; and tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class LineManagerTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("lm-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "mona", "kofi", "omar", "layla" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        // Omar approves for Dubai and is the fallback; Sara reports to Mona, who reports to Kofi. Layla has no manager.
        await world.AssignAsync(t, "omar", BuiltInRoles.Approver, "org_subtree", dubai);
        await SetManagerAsync(t, "sara", "mona");
        await SetManagerAsync(t, "mona", "kofi");
        return t;
    }

    private static async Task SetManagerAsync(TestTenant t, string person, string? manager) =>
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[person].Id}/manager",
            new { managerId = manager is null ? (Guid?)null : t[manager].Id })).StatusCode);

    private static object Route(object approvers) => new { approval = new { names = Names("Manager approval", "موافقة المدير"), approvers } };

    private static object LineManager(string? fallback = null) => Route(new { kind = "line_manager", permission = fallback });

    private static Task<HttpResponseMessage> DraftAsync(HttpClient admin, Guid id, object route, bool onBehalf = false) =>
        admin.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true } } },
            route,
            allowOnBehalf = onBehalf,
        });

    private static async Task<Guid> ServiceAsync(TestTenant t, object route, bool onBehalf = false)
    {
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل") });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DraftAsync(admin, id, route, onBehalf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        return id;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, Guid service, Guid? onBehalfOf = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { boxes = 1 }, onBehalfOf });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    private static async Task<Guid[]> TeamAsync(HttpClient client, string? status = null) =>
        (await (await client.GetAsync("/api/v1/requests/team" + (status is null ? "" : $"?status={status}"))).ReadAsync<JsonElement>()).EnumerateArray()
            .Select(r => r.GetProperty("id").GetGuid()).ToArray();

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid task, string outcome) =>
        client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment = (string?)null });

    private static async Task<JsonElement> RequestAsync(HttpClient client, Guid id) =>
        (await (await client.GetAsync($"/api/v1/requests/{id}")).ReadAsync<JsonElement>()).GetProperty("request");

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<JsonElement[]> NotificationsAsync(HttpClient client, Guid request) =>
        (await (await client.GetAsync("/api/v1/me/notifications")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("link").GetString()!.StartsWith($"/requests/{request}", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task The_line_manager_approves_a_report_request_and_the_unit_approver_does_not_get_it()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, LineManager());
        var request = await SubmitAsync(t["sara"].Client, service);

        // Mona holds no role at all: the reporting line makes her the approver.
        var item = Assert.Single(await InboxAsync(t["mona"].Client));
        Assert.Equal(request, item.GetProperty("requestId").GetGuid());
        Assert.Equal("Manager approval", item.GetProperty("step").GetProperty("en").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("onBehalfOf").ValueKind);
        Assert.Empty(await InboxAsync(t["omar"].Client));
        Assert.Empty(await InboxAsync(t["kofi"].Client));
        var task = item.GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["omar"].Client, task, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["kofi"].Client, task, "approve")).StatusCode);

        var summary = await RequestAsync(t["sara"].Client, request);
        Assert.Equal(t["mona"].Id, Assert.Single(summary.GetProperty("assignees").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["mona"].Client, task, "approve")).StatusCode);
        Assert.Equal("approved", (await RequestAsync(t["sara"].Client, request)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_manager_change_during_approval_moves_the_open_step_to_the_new_manager()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, LineManager());
        var request = await SubmitAsync(t["sara"].Client, service);
        var task = Assert.Single(await InboxAsync(t["mona"].Client)).GetProperty("taskId").GetGuid();

        await SetManagerAsync(t, "sara", "layla");
        Assert.Empty(await InboxAsync(t["mona"].Client));
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["mona"].Client, task, "approve")).StatusCode);
        Assert.Equal(task, Assert.Single(await InboxAsync(t["layla"].Client)).GetProperty("taskId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["layla"].Client, task, "return")).StatusCode);
        Assert.Equal("returned", (await RequestAsync(t["sara"].Client, request)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Without_a_manager_or_when_the_manager_filed_it_the_step_falls_back_to_the_permission()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, LineManager(), onBehalf: true);

        // Layla has no manager: holders of workflow.approve at her unit get it.
        var layla = await SubmitAsync(t["layla"].Client, service);
        var fallback = Assert.Single(await InboxAsync(t["omar"].Client));
        Assert.Equal(layla, fallback.GetProperty("requestId").GetGuid());
        Assert.Empty((await RequestAsync(t["layla"].Client, layla)).GetProperty("assignees").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["omar"].Client, fallback.GetProperty("taskId").GetGuid(), "approve")).StatusCode);

        // Mona files for her report Sara: she never approves it, and Omar does.
        await world.AssignAsync(t, "mona", BuiltInRoles.OrganizationAdministrator, "org_subtree", t.Units["Dubai"]);
        var filed = await SubmitAsync(t["mona"].Client, service, onBehalfOf: t["sara"].Id);
        Assert.DoesNotContain(await InboxAsync(t["mona"].Client), i => i.GetProperty("requestId").GetGuid() == filed);
        var task = Assert.Single(await InboxAsync(t["omar"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["mona"].Client, task, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["omar"].Client, task, "approve")).StatusCode);
    }

    [Fact]
    public async Task The_managers_delegate_acts_on_their_behalf_and_a_fallback_permission_is_validated()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, LineManager(Permissions.WorkflowApprove));
        Assert.Equal(HttpStatusCode.OK, (await t["mona"].Client.PutAsJsonAsync("/api/v1/me/delegation",
            new { delegateId = t["layla"].Id, startsOn = Today.AddDays(-1), endsOn = Today.AddDays(3) })).StatusCode);
        var request = await SubmitAsync(t["sara"].Client, service);

        var delegated = Assert.Single(await InboxAsync(t["layla"].Client));
        Assert.Equal(t["mona"].Id, delegated.GetProperty("onBehalfOf").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["layla"].Client, delegated.GetProperty("taskId").GetGuid(), "approve")).StatusCode);
        Assert.Equal("approved", (await RequestAsync(t["sara"].Client, request)).GetProperty("status").GetString());

        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Cards", "بطاقات") });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        foreach (var bad in new object[]
        {
            Route(new { kind = "line_manager", users = new[] { t["omar"].Id } }),
            Route(new { kind = "line_manager", permission = "not.a.permission" }),
        })
        {
            var response = await DraftAsync(admin, id, bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("validation.services.approvers", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Team_requests_show_direct_and_indirect_reports_only()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, LineManager());
        var sara = await SubmitAsync(t["sara"].Client, service);
        var mona = await SubmitAsync(t["mona"].Client, service);
        var layla = await SubmitAsync(t["layla"].Client, service);

        Assert.Equal([sara], await TeamAsync(t["mona"].Client));
        Assert.Equal(new HashSet<Guid> { sara, mona }, (await TeamAsync(t["kofi"].Client)).ToHashSet());
        Assert.Empty(await TeamAsync(t["kofi"].Client, "approved"));
        Assert.Contains(sara, await TeamAsync(t["kofi"].Client, "in_approval"));
        // Omar approves for the whole unit but manages nobody: org unit membership never adds anyone.
        Assert.Empty(await TeamAsync(t["omar"].Client));
        Assert.Empty(await TeamAsync(t["sara"].Client));

        // Managers read their reports' requests; a peer gets not found.
        Assert.Equal(HttpStatusCode.OK, (await t["kofi"].Client.GetAsync($"/api/v1/requests/{sara}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"/api/v1/requests/{mona}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"/api/v1/requests/{layla}")).StatusCode);

        // Another tenant sees none of it.
        var other = world.B["admin"].Client;
        Assert.Empty(await TeamAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/requests/{sara}")).StatusCode);
    }

    [Fact]
    public async Task The_manager_is_told_a_request_waits_and_the_requester_is_told_each_decision()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, LineManager());
        var request = await SubmitAsync(t["sara"].Client, service);
        var number = (await RequestAsync(t["sara"].Client, request)).GetProperty("number").GetString()!;
        await DispatchAsync();

        var waiting = Assert.Single(await NotificationsAsync(t["mona"].Client, request));
        Assert.Equal($"Request {number} waits for your approval", waiting.GetProperty("title").GetString());
        Assert.StartsWith($"/requests/{request}?task=", waiting.GetProperty("link").GetString());
        Assert.Empty(await NotificationsAsync(t["omar"].Client, request));
        Assert.Empty(await NotificationsAsync(t["sara"].Client, request));

        // Returned, then submitted again and approved: the requester hears about both.
        var task = Assert.Single(await InboxAsync(t["mona"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["mona"].Client, task, "return")).StatusCode);
        await DispatchAsync();
        Assert.Equal($"Request {number} needs changes", Assert.Single(await NotificationsAsync(t["sara"].Client, request)).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.OK, (await t["sara"].Client.PostAsJsonAsync($"/api/v1/requests/{request}/resubmit", new { data = new { boxes = 2 } })).StatusCode);
        await DispatchAsync();
        Assert.Equal(2, (await NotificationsAsync(t["mona"].Client, request)).Length);
        var again = Assert.Single(await InboxAsync(t["mona"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["mona"].Client, again, "approve")).StatusCode);
        await DispatchAsync();
        var told = await NotificationsAsync(t["sara"].Client, request);
        Assert.Contains(told, n => n.GetProperty("title").GetString() == $"Request {number} approved");
        Assert.Equal(2, told.Length);
        Assert.Empty(await NotificationsAsync(world.B["admin"].Client, request));
    }
}
