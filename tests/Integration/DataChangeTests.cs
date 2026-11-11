using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T022 (US6, FR-019): without an HR connection that takes data changes, HR enters an approved change by
/// hand and marks it done, which clears the values. Tenant settings choose the groups and which need a document. A
/// refused or withdrawn change keeps no values; a sibling unit's officer and other tenants see nothing; the person
/// never decides their own change.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DataChangeTests(TenantWorld world)
{
    private const string Mine = "/api/v1/me/data-changes";
    private const string SettingsUrl = "/api/v1/hr/data-change-settings";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("dc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "hr_egypt", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "hr_egypt", "Egypt");
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid id, string outcome)
    {
        var task = (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray()
            .Single(i => i.GetProperty("source").GetString() == "data_change" && i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));
        return await client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision",
            new { outcome, comment = outcome == "approve" ? null : "Please attach the certificate" });
    }

    private static readonly Dictionary<string, string> Contact = new() { ["contact_name"] = "Omar Example", ["relationship"] = "Brother", ["phone"] = "+971 50 000 0000" };

    [Fact]
    public async Task Without_a_connection_HR_enters_an_approved_change_and_marks_it_done()
    {
        var t = await FreshAsync();
        var mona = t["mona"].Client;

        var groups = await OkAsync(mona.GetAsync($"{Mine}/groups"));
        Assert.Equal(["address", "emergency_contact", "bank", "marital_status", "dependants"], groups.EnumerateArray().Select(g => g.GetProperty("key").GetString()!).ToArray());
        Assert.True(groups.EnumerateArray().Single(g => g.GetProperty("key").GetString() == "bank").GetProperty("document").GetBoolean());

        var bad = await mona.PostAsJsonAsync(Mine, new { group = "emergency_contact", values = new Dictionary<string, string> { ["contact_name"] = "Omar", ["phone"] = "call me", ["shoe_size"] = "9" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        Assert.Contains("values.relationship", text, StringComparison.Ordinal);
        Assert.Contains("values.phone", text, StringComparison.Ordinal);
        Assert.Contains("values.shoe_size", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await mona.PostAsJsonAsync(Mine, new { group = "salary", values = Contact })).StatusCode);

        var change = await OkAsync(mona.PostAsJsonAsync(Mine, new { group = "emergency_contact", values = Contact }), HttpStatusCode.Created);
        var id = change.GetProperty("id").GetGuid();
        var url = $"/api/v1/hr/data-changes/{id}";
        Assert.True(change.GetProperty("rights").GetProperty("withdraw").GetBoolean());
        // Not a sensitive group: no security notice.
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from notifications.notification where category = 'security_notice'"));

        // A sibling unit's officer, another tenant and the person's own inbox see nothing to decide.
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync(url)).StatusCode);
        var other = await world.CreateTenantAsync("dc-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{url}:complete", null)).StatusCode);
        Assert.DoesNotContain((await OkAsync(mona.GetAsync("/api/v1/tasks"))).EnumerateArray(), i => i.GetProperty("source").GetString() == "data_change");
        Assert.Empty((await OkAsync(t["hr_egypt"].Client.GetAsync("/api/v1/hr/data-changes"))).EnumerateArray());
        Assert.Single((await OkAsync(t["hr_gulf"].Client.GetAsync("/api/v1/hr/data-changes"))).EnumerateArray());

        Assert.True((await DecideAsync(t["hr_gulf"].Client, id, "approve")).IsSuccessStatusCode);
        var approved = await OkAsync(t["hr_gulf"].Client.GetAsync(url));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal("manual", approved.GetProperty("writeBack").GetString());
        Assert.True(approved.GetProperty("rights").GetProperty("complete").GetBoolean());
        Assert.Equal("+971 50 000 0000", approved.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "phone").GetProperty("value").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await mona.PostAsync($"{url}:complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.PostAsync($"{url}:complete", null)).StatusCode);
        var done = await OkAsync(t["hr_gulf"].Client.PostAsync($"{url}:complete", null));
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.All(done.GetProperty("fields").EnumerateArray(), f => Assert.Equal(JsonValueKind.Null, f.GetProperty("value").ValueKind));
        Assert.Equal(HttpStatusCode.Conflict, (await t["hr_gulf"].Client.PostAsync($"{url}:complete", null)).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeRequested, id.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeWrittenBack, id.ToString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where changes::text like '%000 0000%'"));

        // Refused and withdrawn changes keep no values.
        var refused = (await OkAsync(mona.PostAsJsonAsync(Mine, new { group = "emergency_contact", values = Contact }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.True((await DecideAsync(t["hr_gulf"].Client, refused, "reject")).IsSuccessStatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeRejected, refused.ToString());
        var withdrawn = (await OkAsync(mona.PostAsJsonAsync(Mine, new { group = "emergency_contact", values = Contact }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_gulf"].Client.PostAsync($"/api/v1/hr/data-changes/{withdrawn}:withdraw", null)).StatusCode);
        Assert.Equal("withdrawn", (await OkAsync(mona.PostAsync($"/api/v1/hr/data-changes/{withdrawn}:withdraw", null))).GetProperty("status").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.data_change_request where values is not null"));
        Assert.Equal(3, (await OkAsync(mona.GetAsync(Mine))).GetArrayLength());
    }

    [Fact]
    public async Task The_tenant_chooses_the_groups_and_which_need_a_document()
    {
        var t = await FreshAsync();
        var current = await t["admin"].Client.GetAsync(SettingsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync(SettingsUrl)).StatusCode);
        var put = new HttpRequestMessage(HttpMethod.Put, SettingsUrl)
        {
            Content = JsonContent.Create(new { groups = new Dictionary<string, object> { ["address"] = new { enabled = true, document = true }, ["dependants"] = new { enabled = false, document = true } } }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        var forbidden = new HttpRequestMessage(HttpMethod.Put, SettingsUrl) { Content = JsonContent.Create(new { groups = new Dictionary<string, object>() }) };
        forbidden.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["hr_gulf"].Client.SendAsync(forbidden)).StatusCode);
        await OkAsync(t["admin"].Client.SendAsync(put));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeSettingsChanged, "data_change");

        var groups = (await OkAsync(t["mona"].Client.GetAsync($"{Mine}/groups"))).EnumerateArray().Select(g => g.GetProperty("key").GetString()).ToArray();
        Assert.DoesNotContain("dependants", groups);
        var address = new Dictionary<string, string> { ["address_line"] = "1 Example Street", ["city"] = "Cairo", ["country"] = "Egypt" };
        var refused = await t["mona"].Client.PostAsJsonAsync(Mine, new { group = "address", values = address });
        Assert.Contains("validation.data_change.document_required", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["mona"].Client.PostAsJsonAsync(Mine, new { group = "dependants", values = new { dependants_count = "2" } })).StatusCode);

        // A document of another person cannot be attached.
        var upload = await t["hr_gulf"].Client.PostAsync($"{Mine}/documents?name=lease.pdf",
            new ByteArrayContent("%PDF-1.4 fictional lease"u8.ToArray()) { Headers = { ContentType = new("application/pdf") } });
        var foreign = (await OkAsync(Task.FromResult(upload), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Contains("validation.data_change.document_unknown",
            await (await t["mona"].Client.PostAsJsonAsync(Mine, new { group = "address", values = address, documentId = foreign })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
