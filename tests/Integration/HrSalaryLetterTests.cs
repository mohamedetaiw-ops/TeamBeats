using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Hr.Letters;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 R3 US9 (FR-021, FR-022, FR-024): a salary certificate takes the employment values from the person's HR record
/// and the salary from a live compensation read when it is printed. When HR does not answer the letter waits, HR sees
/// why, and the worker prints it once HR answers; the salary never lands on the stored request. Salary placeholders need
/// payslips in the plan. Figures are fictional.
/// </summary>
public sealed partial class HrSyncTests
{
    private static object SalaryCertificate() => new
    {
        names = new Dictionary<string, string> { ["en"] = "Salary certificate", ["ar"] = "شهادة راتب" },
        templates = new Dictionary<string, string>
        {
            ["en"] = "{name} ({employment_type}) earns a basic salary of {salary_basic} {salary_currency} and {salary_total} {salary_currency} in total.\n\n{addressee}",
            ["ar"] = "يتقاضى {name} راتبًا أساسيًا قدره {salary_basic} {salary_currency} وإجماليًا قدره {salary_total} {salary_currency}.\n\n{addressee}",
        },
        manualFields = Array.Empty<object>(),
        needsApproval = false,
    };

    [Fact]
    public async Task A_salary_certificate_reads_compensation_live_waits_while_HR_is_down_and_keeps_no_salary_on_the_request()
    {
        var t = await TenantAsync(people: ["sara"]);
        var (_, hr) = await RestConnectionAsync(t, compensation: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100")];
        hr.Pay["R1"] = [Pay("BASIC", "Basic salary", "15000"), Pay("HOUSING", "Housing allowance", "7500.50")];
        await SyncAsync(t);
        var put = await t["admin"].Client.PutAsJsonAsync("/api/v1/hr/letter-types/salary", SalaryCertificate());
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        var issued = await (await t["sara"].Client.PostAsJsonAsync("/api/v1/me/letters", new { type = "salary", language = "en", addressee = "Gulf Bank" })).ReadAsync<JsonElement>();
        Assert.Equal("issued", issued.GetProperty("status").GetString());
        var pdf = await (await t["sara"].Client.GetAsync($"/api/v1/hr/letters/{issued.GetProperty("id").GetGuid()}/pdf")).Content.ReadAsStringAsync();
        Assert.Contains("(Permanent) earns a basic salary of 15,000.00 AED and 22,500.50 AED in total.", pdf, StringComparison.Ordinal);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a and changes->>'letter' = @l",
            ("a", AuditActions.HrCompensationReadForLetter), ("l", issued.GetProperty("id").GetGuid().ToString())));

        // HR down: the letter waits, HR sees why, the person does not.
        hr.Down = true;
        var waiting = await (await t["sara"].Client.PostAsJsonAsync("/api/v1/me/letters", new { type = "salary", language = "en" })).ReadAsync<JsonElement>();
        var id = waiting.GetProperty("id").GetGuid();
        Assert.Equal("approved", waiting.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("issueProblem").ValueKind);
        var forHr = await (await t["admin"].Client.GetAsync($"/api/v1/hr/letters/{id}")).ReadAsync<JsonElement>();
        Assert.Equal(LetterIssueProblems.CompensationUnavailable, forHr.GetProperty("issueProblem").GetString());
        // Approvers never see a salary value: no salary field is listed.
        Assert.DoesNotContain(forHr.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString()!.StartsWith("salary_", StringComparison.Ordinal));

        // HR answers again: the worker prints it with the figures as they are now.
        hr.Down = false;
        hr.Pay["R1"][0]["amount"] = "16000";
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var printed = await sp.GetRequiredService<LetterRequests>().IssueDueAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return printed;
        });
        var done = await (await t["sara"].Client.GetAsync($"/api/v1/hr/letters/{id}")).ReadAsync<JsonElement>();
        Assert.Equal("issued", done.GetProperty("status").GetString());
        Assert.Contains("basic salary of 16,000.00 AED", await (await t["sara"].Client.GetAsync($"/api/v1/hr/letters/{id}/pdf")).Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // A basic salary code HR does not have: the letter waits with that reason.
        hr.Pay["R1"].RemoveAt(0);
        var missing = await (await t["sara"].Client.PostAsJsonAsync("/api/v1/me/letters", new { type = "salary", language = "en" })).ReadAsync<JsonElement>();
        Assert.Equal(LetterIssueProblems.CompensationMissing,
            (await (await t["admin"].Client.GetAsync($"/api/v1/hr/letters/{missing.GetProperty("id").GetGuid()}")).ReadAsync<JsonElement>()).GetProperty("issueProblem").GetString());

        // FR-024: no salary figure is kept on any letter request, and audit details carry none.
        var stored = await world.Database.TenantScalarAsync<string>(t.Id, "select coalesce(string_agg(row_to_json(l)::text, ' '), '') from hr.letter l");
        Assert.DoesNotContain("15000", stored!, StringComparison.Ordinal);
        Assert.DoesNotContain("16,000", stored!, StringComparison.Ordinal);
        var audits = await world.Database.TenantScalarAsync<string>(t.Id, "select coalesce(string_agg(changes::text, ' '), '') from audit.audit_event");
        Assert.DoesNotContain("15000", audits!, StringComparison.Ordinal);
        Assert.DoesNotContain("16000", audits!, StringComparison.Ordinal);

        // Without payslips in the plan, salary placeholders are refused.
        var plain = await TenantAsync("workplace");
        var refused = await plain["admin"].Client.PutAsJsonAsync("/api/v1/hr/letter-types/salary", SalaryCertificate());
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.letters.salary_needs_payroll", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
