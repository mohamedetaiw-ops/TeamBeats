using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 T006 to T008: prefilled forms with read-only directory values, validation on submit, one request per
/// idempotency key, My requests with status, step and assignee, Tracking, attachments that are scanned and stored under
/// the tenant's path, nobody approving a request filed by or for them, cancel, and tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceRequestTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private sealed record Org(TestTenant T, Guid Gulf, Guid Dubai);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("sr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var dubai = await world.CreateUnitAsync(t, "Dubai", gulf, "department");
        foreach (var name in new[] { "sara", "omar", "layla", "hr" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        await world.AssignAsync(t, "hr", BuiltInRoles.OrganizationAdministrator, "org_subtree", gulf);
        return new Org(t, gulf, dubai);
    }

    private static object CardsForm(int notice = 0, bool attachments = false) => new
    {
        fields = new List<object>
        {
            new { key = "name", type = "text", labels = Names("Name", "الاسم"), required = true, prefill = "profile.display_name" },
            new { key = "email", type = "text", labels = Names("Email", "البريد"), prefill = "profile.email" },
            new { key = "mobile", type = "text", labels = Names("Mobile", "الجوال"), prefill = "profile.mobile_phone" },
            new { key = "manager", type = "person", labels = Names("Manager", "المدير"), prefill = "profile.manager" },
            new { key = "unit", type = "org_unit", labels = Names("Department", "القسم"), prefill = "org.primary_unit" },
            new { key = "company", type = "org_unit", labels = Names("Company", "الشركة"), prefill = "org.company" },
            new { key = "boxes", type = "number", labels = Names("Boxes", "العلب"), required = true, rules = new[] { new { kind = "max", value = 2 } } },
            new { key = "start", type = "date", labels = Names("Start", "البداية"), rules = new[] { new { kind = "min_days_from_today", value = notice } } },
            new { key = "policy", type = "policy_note", labels = Names("Policy", "السياسة"), note = Names("Cards take five working days.", "تستغرق البطاقات خمسة أيام عمل.") },
        }.Concat(attachments ? [new { key = "files", type = "attachment", labels = Names("Files", "ملفات") }] : Array.Empty<object>()).ToArray(),
    };

    private async Task<Guid> ServiceAsync(Org o, object? route = null, bool onBehalf = false, bool attachments = false)
    {
        var admin = o.T["admin"].Client;
        var created = await admin.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل") });
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
        var draft = await admin.PutAsJsonAsync($"{Admin}/{id}/draft", new { form = CardsForm(attachments: attachments), route, allowOnBehalf = onBehalf, maxAttachments = attachments ? 2 : 0 });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { })).StatusCode);
        return id;
    }

    private static object UsersRoute(params Guid[] users) => new { approval = new { names = Names("Logistics approval", "موافقة اللوجستيات"), approvers = new { kind = "users", users } } };

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid service, object data, string? key = null, Guid? onBehalfOf = null) =>
        client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data, idempotencyKey = key, onBehalfOf });

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => p.Name + ":" + e.GetProperty("key").GetString())).ToArray();
    }

    private Task<IReadOnlyList<UserTaskInfo>> OpenTasksAsync(TestTenant t, string user) =>
        world.Factory.InScopeAsync(t.Id, sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.User, t[user].Id);
            return sp.GetRequiredService<IProcessEngine>().MyOpenTasksAsync();
        });

    [Fact]
    public async Task The_form_is_prefilled_and_directory_values_stay_read_only_even_when_the_client_sends_others()
    {
        var o = await FreshAsync();
        var t = o.T;
        Assert.True((await t["admin"].Client.PutAsJsonAsync("/api/v1/people/policy", new { fields = new[] { new { key = "mobilePhone", source = "person", visibility = "managers_hr" } } })).IsSuccessStatusCode);
        Assert.True((await t["sara"].Client.PutAsJsonAsync("/api/v1/me/profile", new { values = new Dictionary<string, string> { ["mobilePhone"] = "+971 50 123 4567" } })).IsSuccessStatusCode);
        Assert.True((await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["omar"].Id })).IsSuccessStatusCode);
        var service = await ServiceAsync(o);

        var form = (await (await t["sara"].Client.GetAsync($"/api/v1/services/{service}")).ReadAsync<JsonElement>()).GetProperty("form");
        var note = form.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "policy").GetProperty("note");
        Assert.Equal("Cards take five working days.", note.GetProperty("en").GetString());
        Assert.Equal("تستغرق البطاقات خمسة أيام عمل.", note.GetProperty("ar").GetString());

        var prefill = await (await t["sara"].Client.GetAsync($"/api/v1/services/{service}/prefill")).ReadAsync<JsonElement>();
        var values = prefill.GetProperty("values");
        Assert.Equal(t["sara"].Email, values.GetProperty("email").GetString());
        Assert.Equal("+971 50 123 4567", values.GetProperty("mobile").GetString());
        Assert.Equal(t["omar"].Id, values.GetProperty("manager").GetGuid());
        Assert.Equal(o.Dubai, values.GetProperty("unit").GetGuid());
        Assert.Equal(o.Gulf, values.GetProperty("company").GetGuid());
        var readOnly = prefill.GetProperty("readOnly").EnumerateArray().Select(v => v.GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?> { "name", "email", "manager", "unit", "company" }, readOnly);
        Assert.DoesNotContain("mobile", readOnly);

        var submitted = await SubmitAsync(t["sara"].Client, service, new { name = "Someone else", email = "x@evil.example", mobile = "+971 4 000 0000", unit = o.Gulf, boxes = 1 });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var data = (await submitted.ReadAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(t["sara"].Email, data.GetProperty("email").GetString());
        Assert.NotEqual("Someone else", data.GetProperty("name").GetString());
        Assert.Equal(o.Dubai, data.GetProperty("unit").GetGuid());
        Assert.Equal("+971 4 000 0000", data.GetProperty("mobile").GetString());
    }

    [Fact]
    public async Task Validation_blocks_submit_and_a_repeated_submit_returns_the_same_request()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id, t["sara"].Id));
        var sara = t["sara"].Client;

        var tooMany = await ErrorKeysAsync(await SubmitAsync(sara, service, new { boxes = 3 }));
        Assert.Contains("data.boxes:validation.services.field_max", tooMany);
        Assert.Contains("data.boxes:validation.services.field_required", await ErrorKeysAsync(await SubmitAsync(sara, service, new { })));
        Assert.Empty((await (await sara.GetAsync("/api/v1/requests")).ReadAsync<JsonElement>()).EnumerateArray());

        var first = await SubmitAsync(sara, service, new { boxes = 2 }, key: "click-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var again = await SubmitAsync(sara, service, new { boxes = 1 }, key: "click-1");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var request = (await first.ReadAsync<JsonElement>()).GetProperty("request");
        Assert.Equal(request.GetProperty("id").GetGuid(), (await again.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid());
        Assert.Matches(@"^REQ-\d{4}-000001$", request.GetProperty("number").GetString());

        var mine = Assert.Single((await (await sara.GetAsync("/api/v1/requests")).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("in_approval", mine.GetProperty("status").GetString());
        Assert.Equal("Logistics approval", mine.GetProperty("currentStep").GetProperty("en").GetString());
        // The requester is named on the step but is never their own approver.
        Assert.Equal([t["omar"].Id], mine.GetProperty("assignees").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList());
        Assert.Empty(await OpenTasksAsync(t, "sara"));
        Assert.Single(await OpenTasksAsync(t, "omar"));

        var detail = await (await sara.GetAsync($"/api/v1/requests/{request.GetProperty("id").GetGuid()}")).ReadAsync<JsonElement>();
        var tracking = Assert.Single(detail.GetProperty("tracking").EnumerateArray());
        Assert.Equal("submitted", tracking.GetProperty("event").GetString());
        Assert.Equal(t["sara"].Id, tracking.GetProperty("actor").GetProperty("id").GetGuid());
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.RequestSubmitted));
        Assert.Equal(1, await world.Database.OutboxCountAsync("RequestSubmitted", t.Id));

        // The approver may read the request; a colleague may not.
        Assert.Equal(HttpStatusCode.OK, (await t["omar"].Client.GetAsync($"/api/v1/requests/{request.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["layla"].Client.GetAsync($"/api/v1/requests/{request.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task On_behalf_requests_need_the_permission_and_the_person_it_is_for_never_approves_it()
    {
        var o = await FreshAsync();
        var t = o.T;
        var closed = await ServiceAsync(o, UsersRoute(t["omar"].Id));
        Assert.Contains("onBehalfOf:validation.services.on_behalf_not_allowed",
            await ErrorKeysAsync(await SubmitAsync(t["hr"].Client, closed, new { boxes = 1 }, onBehalfOf: t["sara"].Id)));

        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id, t["layla"].Id), onBehalf: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await SubmitAsync(t["sara"].Client, service, new { boxes = 1 }, onBehalfOf: t["layla"].Id)).StatusCode);
        var filed = await SubmitAsync(t["hr"].Client, service, new { boxes = 1 }, onBehalfOf: t["layla"].Id);
        Assert.Equal(HttpStatusCode.Created, filed.StatusCode);
        var request = (await filed.ReadAsync<JsonElement>()).GetProperty("request");
        Assert.Equal(t["layla"].Id, request.GetProperty("onBehalfOf").GetProperty("id").GetGuid());
        Assert.Empty(await OpenTasksAsync(t, "layla"));
        Assert.Single(await OpenTasksAsync(t, "omar"));
        Assert.Single((await (await t["layla"].Client.GetAsync("/api/v1/requests")).ReadAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task Attachments_are_scanned_stored_under_the_tenant_and_readable_only_through_the_request_once_clean()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id), attachments: true);
        var sara = t["sara"].Client;

        static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string name, string text)
        {
            var content = new ByteArrayContent(Encoding.ASCII.GetBytes(text));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            return await client.PostAsync($"/api/v1/requests/attachments?name={Uri.EscapeDataString(name)}", content);
        }

        Assert.Contains("file:validation.services.attachment_infected", await ErrorKeysAsync(await UploadAsync(sara, "virus.pdf", TestSignatureScanner.Eicar)));
        var upload = await UploadAsync(sara, "../../quote.pdf", "%PDF-1.4 quote");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var file = await upload.ReadAsync<JsonElement>();
        var fileId = file.GetProperty("id").GetGuid();
        Assert.Equal("quote.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal("clean", file.GetProperty("scanState").GetString());
        Assert.True(File.Exists(Path.Combine(world.Factory.StorageRoot, "tenants", t.Id.ToString("D"), "services", "attachments", fileId.ToString("N"))));

        // Someone else's upload cannot be attached.
        var omarsFile = (await (await UploadAsync(t["omar"].Client, "x.pdf", "x")).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Contains("data:validation.services.attachment_unknown", await ErrorKeysAsync(await SubmitAsync(sara, service, new { boxes = 1, files = new[] { omarsFile } })));

        var submitted = await SubmitAsync(sara, service, new { boxes = 1, files = new[] { fileId } });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        var requestId = (await submitted.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
        var url = $"/api/v1/requests/{requestId}/attachments/{fileId}";
        var download = await sara.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("%PDF-1.4 quote", await download.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.OK, (await t["omar"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["layla"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync(url)).StatusCode);
        // Attached once means attached for good: it cannot go on a second request.
        Assert.Contains("data:validation.services.attachment_unknown", await ErrorKeysAsync(await SubmitAsync(sara, service, new { boxes = 1, files = new[] { fileId } })));

        await world.Database.OwnerExecInTenantAsync(t.Id, "update services.attachment set scan_state = 'failed' where id = @id", ("id", fileId));
        Assert.Equal(HttpStatusCode.Conflict, (await sara.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task The_requester_cancels_until_it_is_decided_and_the_open_task_is_cancelled_with_it()
    {
        var o = await FreshAsync();
        var t = o.T;
        var service = await ServiceAsync(o, UsersRoute(t["omar"].Id), onBehalf: true);
        var request = (await (await SubmitAsync(t["hr"].Client, service, new { boxes = 1 }, onBehalfOf: t["sara"].Id)).ReadAsync<JsonElement>()).GetProperty("request");
        var id = request.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PostAsJsonAsync($"/api/v1/requests/{id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["layla"].Client.PostAsJsonAsync($"/api/v1/requests/{id}/cancel", new { })).StatusCode);
        var cancelled = await (await t["hr"].Client.PostAsJsonAsync($"/api/v1/requests/{id}/cancel", new { })).ReadAsync<JsonElement>();
        Assert.Equal("cancelled", cancelled.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(["submitted", "cancelled"], cancelled.GetProperty("tracking").EnumerateArray().Select(e => e.GetProperty("event").GetString()).ToList());
        Assert.Empty(await OpenTasksAsync(t, "omar"));
        Assert.Equal(HttpStatusCode.Conflict, (await t["hr"].Client.PostAsJsonAsync($"/api/v1/requests/{id}/cancel", new { })).StatusCode);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.RequestCancelled));
        Assert.Equal(1, await world.Database.OutboxCountAsync("RequestCompleted", t.Id));
    }

    [Fact]
    public async Task Requests_of_another_tenant_are_not_found_and_a_service_of_another_tenant_cannot_be_requested()
    {
        var o = await FreshAsync();
        var service = await ServiceAsync(o);
        var id = (await (await SubmitAsync(o.T["sara"].Client, service, new { boxes = 1 })).ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
        var other = world.B["admin"].Client;

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/v1/requests/{id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/services/{service}/prefill")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(other, service, new { boxes = 1 })).StatusCode);
        Assert.Empty((await (await other.GetAsync("/api/v1/requests")).ReadAsync<JsonElement>()).EnumerateArray());
        // Without an approval step the request waits as submitted.
        Assert.Equal("submitted", (await (await o.T["sara"].Client.GetAsync($"/api/v1/requests/{id}")).ReadAsync<JsonElement>()).GetProperty("request").GetProperty("status").GetString());
    }
}
