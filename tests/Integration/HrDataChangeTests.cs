using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Integrations.Hr;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T022 (US6 scenarios 1 and 3, FR-019, FR-020) with spec 019: a bank details change needs a document, goes to
/// HR officers, and once approved is written to the HR system through the HR REST pack. The values and the bank
/// document are gone once HR records it, nothing of them is left in the hand-off or the connector run, and the person
/// hears of the change on every channel. A refused write ends as failed with the values cleared too.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string DataChanges = "/api/v1/me/data-changes";

    // A valid fictional IBAN (check digits correct), not a real account.
    private const string FictionalIban = "GB82WEST12345698765432";

    private async Task<(TestTenant T, FakeHr Hr)> DataChangeTenantAsync()
    {
        var t = await TenantAsync(people: ["sara", "hr_fin"]);
        var finance = await world.CreateUnitAsync(t, "Finance", null, "organization");
        foreach (var name in new[] { "sara", "hr_fin" })
        {
            await world.AddMembershipAsync(t, name, "Finance");
        }
        await world.AssignAsync(t, "hr_fin", BuiltInRoles.HrOfficer, "org_subtree", finance);
        var (_, hr) = await RestConnectionAsync(t, dataChange: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100")];
        await SyncAsync(t);
        return (t, hr);
    }

    private async Task<Guid> UploadAsync(HttpClient client)
    {
        var content = new ByteArrayContent("%PDF-1.4 fictional bank letter"u8.ToArray()) { Headers = { ContentType = new("application/pdf") } };
        var uploaded = await client.PostAsync($"{DataChanges}/documents?name=bank-letter.pdf", content);
        Assert.True(uploaded.StatusCode == HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        return (await uploaded.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> DecideChangeAsync(HttpClient client, Guid id, string outcome)
    {
        var task = (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray()
            .Single(i => i.GetProperty("source").GetString() == "data_change" && i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));
        return await client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision",
            new { outcome, comment = outcome == "approve" ? null : "Document does not match" });
    }

    private async Task RunDataChangeJobsAsync(TestTenant t)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var now = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
            await sp.GetRequiredService<HrDataChange>().RetryDueAsync(now, 20, CancellationToken.None);
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(now, 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });
        await DispatchAsync();
        await DispatchAsync();
    }

    [Fact]
    public async Task An_approved_bank_change_is_written_to_HR_and_nothing_of_it_is_kept()
    {
        var (t, hr) = await DataChangeTenantAsync();
        var sara = t["sara"].Client;
        var bank = new Dictionary<string, string> { ["bank_name"] = "Fictional Bank", ["account_name"] = "Sara Example", ["iban"] = "gb82 west 1234 5698 7654 32" };

        var noDocument = await sara.PostAsJsonAsync(DataChanges, new { group = "bank", values = bank });
        Assert.Equal(HttpStatusCode.BadRequest, noDocument.StatusCode);
        Assert.Contains("validation.data_change.document_required", await noDocument.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var badIban = await sara.PostAsJsonAsync(DataChanges, new { group = "bank", values = new Dictionary<string, string>(bank) { ["iban"] = "GB00WEST12345698765432" } });
        Assert.Contains("values.iban", await badIban.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var document = await UploadAsync(sara);
        var created = await sara.PostAsJsonAsync(DataChanges, new { group = "bank", values = bank, reason = "New salary account", documentId = document });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var change = await created.ReadAsync<JsonElement>();
        var id = change.GetProperty("id").GetGuid();
        Assert.Equal("pending", change.GetProperty("status").GetString());
        Assert.Equal(FictionalIban, change.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "iban").GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await sara.PostAsJsonAsync(DataChanges, new { group = "bank", values = bank, documentId = await UploadAsync(sara) })).StatusCode);

        // A bank change is a security notice: mandatory, on every channel (FR-020).
        Assert.Equal(1L, await world.Database.ScalarInTenantAsync<long>(t.Id,
            "select count(*) from notifications.notification where tenant_id = @t and user_id = @u and category = 'security_notice' and template_key = 'hr.data_change_submitted'",
            ("t", t.Id), ("u", t["sara"].Id)));

        var approved = await DecideChangeAsync(t["hr_fin"].Client, id, "approve");
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync());
        var waiting = await (await sara.GetAsync($"/api/v1/hr/data-changes/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("approved", waiting.GetProperty("status").GetString());
        Assert.Equal("hr", waiting.GetProperty("writeBack").GetString());

        await RunDataChangeJobsAsync(t);
        var sent = Assert.Single(hr.DataChangesSent);
        Assert.Equal("bank", sent.Body.GetProperty("group").GetString());
        Assert.Equal("R1", sent.Body.GetProperty("record_id").GetString());
        Assert.Equal("E-100", sent.Body.GetProperty("employee_number").GetString());
        Assert.Equal(FictionalIban, sent.Body.GetProperty("values").GetProperty("iban").GetString());
        Assert.Equal($"teambeats-data-change-{id}", sent.Key);

        var done = await (await sara.GetAsync($"/api/v1/hr/data-changes/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal("HR-DC-1", done.GetProperty("externalReference").GetString());
        Assert.All(done.GetProperty("fields").EnumerateArray(), f => Assert.Equal(JsonValueKind.Null, f.GetProperty("value").ValueKind));
        Assert.Equal(JsonValueKind.Null, done.GetProperty("documentName").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await sara.GetAsync($"/api/v1/hr/data-changes/{id}/document")).StatusCode);

        // Nothing of the values is left anywhere TeamBeats keeps data.
        foreach (var (sql, name) in new[]
        {
            ("select count(*) from hr.data_change_request where values is not null", "request"),
            ("select count(*) from integrations.hr_data_change_handoff where input is not null", "hand-off"),
            ($"select count(*) from integrations.run where input::text like '%{FictionalIban[^8..]}%'", "run"),
            ($"select count(*) from audit.audit_event where changes::text like '%{FictionalIban[^8..]}%'", "audit"),
        })
        {
            Assert.True(await world.Database.TenantScalarAsync<long>(t.Id, sql) == 0, name);
        }
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.data_change_document where deleted_at is not null"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeApproved, id.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeWrittenBack, id.ToString());
        Assert.Equal(1L, await world.Database.ScalarInTenantAsync<long>(t.Id,
            "select count(*) from notifications.notification where tenant_id = @t and user_id = @u and category = 'security_notice' and template_key = 'hr.data_change_completed'",
            ("t", t.Id), ("u", t["sara"].Id)));

        // HR refuses the next one: it ends as failed, with its values cleared as well.
        hr.DataChangeAnswer = HttpStatusCode.UnprocessableEntity;
        var address = await sara.PostAsJsonAsync(DataChanges, new
        {
            group = "address", values = new Dictionary<string, string> { ["address_line"] = "1 Example Street", ["city"] = "Riyadh", ["country"] = "Saudi Arabia" },
        });
        Assert.True(address.StatusCode == HttpStatusCode.Created, await address.Content.ReadAsStringAsync());
        var second = (await address.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True((await DecideChangeAsync(t["hr_fin"].Client, second, "approve")).IsSuccessStatusCode);
        await RunDataChangeJobsAsync(t);
        var failed = await (await sara.GetAsync($"/api/v1/hr/data-changes/{second}")).ReadAsync<JsonElement>();
        Assert.Equal("write_failed", failed.GetProperty("status").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from integrations.hr_data_change_handoff where input is not null"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrDataChangeWriteFailed, second.ToString());
    }
}
