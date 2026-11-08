using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Services.Contracts;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 T009 to T012: one task inbox with a count; approve, reject with a reason and return, each moving the request
/// on and recorded in Tracking and audit; nobody else can act; <c>workflow.approve</c> is needed besides the assignment;
/// resubmit after a return; delegation with "on behalf of", the requester as delegate never approving, tasks coming back
/// after the window; and tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TaskInboxTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private sealed record Org(TestTenant T, Guid Dubai);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ti-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "omar", "layla", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        // Omar approves for Dubai; Layla and Kofi hold no approval permission of their own.
        await world.AssignAsync(t, "omar", BuiltInRoles.Approver, "org_subtree", dubai);
        return new Org(t, dubai);
    }

    private static object Form() => new
    {
        fields = new object[]
        {
            new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true, rules = new[] { new { kind = "max", value = 3 } } },
        },
    };

    private static object UsersRoute(params Guid[] users) => new { approval = new { names = Names("Logistics approval", "موافقة اللوجستيات"), approvers = new { kind = "users", users } } };

    private static object PermissionRoute(string permission) =>
        new { approval = new { names = Names("Unit approval", "موافقة الوحدة"), approvers = new { kind = "permission_at_unit", permission } } };

    private static async Task<Guid> ServiceAsync(Org o, object route)
    {
        var admin = o.T["admin"].Client;
        var created = await admin.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل") });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Admin}/{id}/draft", new { form = Form(), route })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        return id;
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient client, Guid service, int boxes = 1)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { boxes } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("request");
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client, Guid? service = null) =>
        (await (await client.GetAsync("/api/v1/tasks" + (service is { } s ? $"?service={s}" : ""))).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    private static async Task<int> CountAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks/count")).ReadAsync<JsonElement>()).GetProperty("open").GetInt32();

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid task, string outcome, string? comment = null) =>
        client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment });

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid request) => await (await client.GetAsync($"/api/v1/requests/{request}")).ReadAsync<JsonElement>();

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => p.Name + ":" + e.GetProperty("key").GetString())).ToArray();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static Task<HttpResponseMessage> DelegateAsync(HttpClient client, Guid to, DateOnly starts, DateOnly ends) =>
        client.PutAsJsonAsync("/api/v1/me/delegation", new { delegateId = to, startsOn = starts, endsOn = ends });

    [Fact]
    public async Task The_approver_sees_one_inbox_with_a_count_and_approving_closes_the_request_while_nobody_else_can_act()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id, t["kofi"].Id));
        var other = await ServiceAsync(o, UsersRoute(t["omar"].Id));
        var request = await SubmitAsync(t["sara"].Client, service);
        await SubmitAsync(t["sara"].Client, other);
        var requestId = request.GetProperty("id").GetGuid();

        var omar = t["omar"].Client;
        Assert.Equal(2, await CountAsync(omar));
        var item = Assert.Single(await InboxAsync(omar, service));
        Assert.Equal("service_request", item.GetProperty("source").GetString());
        Assert.Equal(requestId, item.GetProperty("requestId").GetGuid());
        Assert.Equal(request.GetProperty("number").GetString(), item.GetProperty("number").GetString());
        Assert.Equal("Logistics approval", item.GetProperty("step").GetProperty("en").GetString());
        Assert.Equal("موافقة اللوجستيات", item.GetProperty("step").GetProperty("ar").GetString());
        Assert.Equal(t["sara"].Id, item.GetProperty("requester").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("onBehalfOf").ValueKind);
        var task = item.GetProperty("taskId").GetGuid();

        // Kofi is named on the step but holds no workflow.approve, so it is not in his inbox and he cannot act.
        Assert.Equal(0, await CountAsync(t["kofi"].Client));
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["kofi"].Client, task, "approve")).StatusCode);
        // Layla is not assigned at all: a crafted call is refused (US3 scenario 3).
        Assert.Empty(await InboxAsync(t["layla"].Client));
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["layla"].Client, task, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(omar, Guid.NewGuid(), "approve")).StatusCode);
        Assert.Contains("outcome:validation.workflow.outcome", await ErrorKeysAsync(await DecideAsync(omar, task, "maybe")));

        var approved = await DecideAsync(omar, task, "approve", "Fine");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var detail = await DetailAsync(t["sara"].Client, requestId);
        Assert.Equal("approved", detail.GetProperty("request").GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("request").GetProperty("closedAt").ValueKind);
        var tracked = detail.GetProperty("tracking").EnumerateArray().Last();
        Assert.Equal("approved", tracked.GetProperty("event").GetString());
        Assert.Equal(t["omar"].Id, tracked.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal("Fine", tracked.GetProperty("comment").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TaskApproved, task.ToString());
        Assert.True(await world.Database.OutboxCountAsync(nameof(RequestCompleted), t.Id) >= 1);

        Assert.Equal(1, await CountAsync(omar));
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(omar, task, "approve")).StatusCode);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_returning_lets_the_requester_change_and_submit_again()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id));
        var omar = t["omar"].Client;
        var sara = t["sara"].Client;

        var rejected = (await SubmitAsync(sara, service)).GetProperty("id").GetGuid();
        var task = Assert.Single(await InboxAsync(omar)).GetProperty("taskId").GetGuid();
        Assert.Contains("comment:validation.services.reason_required", await ErrorKeysAsync(await DecideAsync(omar, task, "reject", "  ")));
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(omar, task, "reject", "Budget is closed")).StatusCode);
        var detail = await DetailAsync(sara, rejected);
        Assert.Equal("rejected", detail.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal("Budget is closed", detail.GetProperty("tracking").EnumerateArray().Last().GetProperty("comment").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TaskRejected, task.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await sara.PostAsJsonAsync($"/api/v1/requests/{rejected}/resubmit", new { data = new { boxes = 1 } })).StatusCode);

        var returned = await SubmitAsync(sara, service);
        var returnedId = returned.GetProperty("id").GetGuid();
        task = Assert.Single(await InboxAsync(omar)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(omar, task, "return", "Two boxes are enough")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TaskReturned, task.ToString());
        Assert.True(await world.Database.OutboxCountAsync(nameof(RequestReturned), t.Id) >= 1);
        detail = await DetailAsync(sara, returnedId);
        Assert.Equal("returned", detail.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("request").GetProperty("closedAt").ValueKind);
        Assert.Empty(await InboxAsync(omar));

        // Only the person who filed it submits it again, with valid data.
        Assert.Equal(HttpStatusCode.NotFound, (await t["layla"].Client.PostAsJsonAsync($"/api/v1/requests/{returnedId}/resubmit", new { data = new { boxes = 2 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await omar.PostAsJsonAsync($"/api/v1/requests/{returnedId}/resubmit", new { data = new { boxes = 2 } })).StatusCode);
        Assert.Contains("data.boxes:validation.services.field_max",
            await ErrorKeysAsync(await sara.PostAsJsonAsync($"/api/v1/requests/{returnedId}/resubmit", new { data = new { boxes = 9 } })));
        var again = await sara.PostAsJsonAsync($"/api/v1/requests/{returnedId}/resubmit", new { data = new { boxes = 2 } });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        detail = await again.ReadAsync<JsonElement>();
        Assert.Equal("in_approval", detail.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(returned.GetProperty("number").GetString(), detail.GetProperty("request").GetProperty("number").GetString());
        Assert.Equal(2, detail.GetProperty("data").GetProperty("boxes").GetInt32());
        Assert.Equal(new string?[] { "submitted", "returned", "resubmitted" }, detail.GetProperty("tracking").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RequestResubmitted, returnedId.ToString());

        var second = Assert.Single(await InboxAsync(omar));
        Assert.NotEqual(task, second.GetProperty("taskId").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(omar, second.GetProperty("taskId").GetGuid(), "approve")).StatusCode);
        Assert.Equal("approved", (await DetailAsync(sara, returnedId)).GetProperty("request").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_delegate_acts_on_behalf_of_the_approver_inside_the_window_and_tasks_come_back_after_it()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id));
        var omar = t["omar"].Client;
        var layla = t["layla"].Client;

        Assert.Contains("delegateId:validation.services.delegate", await ErrorKeysAsync(await DelegateAsync(omar, t["omar"].Id, Today, Today.AddDays(3))));
        Assert.Contains("endsOn:validation.services.delegation_dates", await ErrorKeysAsync(await DelegateAsync(omar, t["layla"].Id, Today.AddDays(3), Today.AddDays(1))));
        Assert.Contains("endsOn:validation.services.delegation_dates", await ErrorKeysAsync(await DelegateAsync(omar, t["layla"].Id, Today.AddDays(-9), Today.AddDays(-5))));

        // A window that has not started yet hands nothing over.
        Assert.Equal(HttpStatusCode.OK, (await DelegateAsync(omar, t["layla"].Id, Today.AddDays(5), Today.AddDays(9))).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DelegationSet, t["omar"].Id.ToString());
        var mine = (await (await omar.GetAsync("/api/v1/me/delegation")).ReadAsync<JsonElement>()).GetProperty("delegation");
        Assert.False(mine.GetProperty("active").GetBoolean());
        var first = (await SubmitAsync(t["sara"].Client, service)).GetProperty("id").GetGuid();
        Assert.Empty(await InboxAsync(layla));

        Assert.Equal(HttpStatusCode.OK, (await DelegateAsync(omar, t["layla"].Id, Today.AddDays(-1), Today.AddDays(3))).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DelegationChanged, t["omar"].Id.ToString());
        var delegated = Assert.Single(await InboxAsync(layla));
        Assert.Equal(t["omar"].Id, delegated.GetProperty("onBehalfOf").GetProperty("id").GetGuid());
        Assert.Single(await InboxAsync(omar)); // Omar still sees it.

        // Layla holds no workflow.approve herself; she acts with Omar's authority.
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(layla, delegated.GetProperty("taskId").GetGuid(), "approve")).StatusCode);
        var tracked = (await DetailAsync(t["sara"].Client, first)).GetProperty("tracking").EnumerateArray().Last();
        Assert.Equal(t["layla"].Id, tracked.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal(t["omar"].Id, tracked.GetProperty("onBehalfOf").GetProperty("id").GetGuid());
        var history = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.User, t["layla"].Id);
            var engine = sp.GetRequiredService<IProcessEngine>();
            var instance = await engine.FindInstanceAsync("service." + service.ToString("N"), first.ToString("N"));
            return await engine.HistoryAsync(instance!.Id);
        });
        var completed = Assert.Single(history, h => h.Event == "task_completed:approve");
        Assert.Equal((t["layla"].Id, t["omar"].Id), (completed.ActorId!.Value, completed.OnBehalfOf!.Value));

        // After the delegation ends, the next task is Omar's alone.
        Assert.Equal(HttpStatusCode.NoContent, (await omar.DeleteAsync("/api/v1/me/delegation")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DelegationEnded, t["omar"].Id.ToString());
        await SubmitAsync(t["sara"].Client, service);
        Assert.Empty(await InboxAsync(layla));
        Assert.Single(await InboxAsync(omar));
        Assert.Equal(JsonValueKind.Null, (await (await omar.GetAsync("/api/v1/me/delegation")).ReadAsync<JsonElement>()).GetProperty("delegation").ValueKind);
    }

    [Fact]
    public async Task A_requester_who_is_the_delegate_never_approves_their_own_request_and_permission_steps_delegate_too()
    {
        var o = await FreshAsync();
        var t = o.T;
        var named = await ServiceAsync(o, UsersRoute(t["omar"].Id));
        var byPermission = await ServiceAsync(o, PermissionRoute(Permissions.WorkflowApprove));
        Assert.Equal(HttpStatusCode.OK, (await DelegateAsync(t["omar"].Client, t["sara"].Id, Today.AddDays(-1), Today.AddDays(3))).StatusCode);

        // Sara is Omar's delegate and files a request: it never reaches her, and Omar can still decide it.
        await SubmitAsync(t["sara"].Client, named);
        Assert.Empty(await InboxAsync(t["sara"].Client));
        var task = Assert.Single(await InboxAsync(t["omar"].Client)).GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(t["sara"].Client, task, "approve")).StatusCode);

        // A step for holders of a permission at the unit: Kofi's request reaches Sara as Omar's delegate.
        await SubmitAsync(t["kofi"].Client, byPermission);
        var delegated = Assert.Single(await InboxAsync(t["sara"].Client));
        Assert.Equal(t["omar"].Id, delegated.GetProperty("onBehalfOf").GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["sara"].Client, delegated.GetProperty("taskId").GetGuid(), "approve")).StatusCode);
    }

    [Fact]
    public async Task Tasks_and_delegations_of_another_tenant_are_not_found()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id));
        await SubmitAsync(t["sara"].Client, service);
        var task = Assert.Single(await InboxAsync(t["omar"].Client)).GetProperty("taskId").GetGuid();

        var other = world.B["admin"].Client;
        Assert.Empty(await InboxAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(other, task, "approve")).StatusCode);
        Assert.Contains("delegateId:validation.services.delegate", await ErrorKeysAsync(await DelegateAsync(other, t["omar"].Id, Today, Today.AddDays(2))));
        Assert.Single(await InboxAsync(t["omar"].Client));
    }
}
