using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 R3 US10 (FR-023): a person reads their own compensation statement live from HR after step-up, with each pay
/// component and a total when the components agree on currency and frequency. Only their own: no one else, manager or
/// administrator, can name them. Never cached; every read is audited with the count and never the amounts; when HR does
/// not answer nothing is shown. A letter reads one person's statement for one letter. Without hr.payslips it is refused.
/// Figures are fictional.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string Compensation = "/api/v1/me/compensation";

    private static Dictionary<string, object?> Pay(string code, string label, string amount, string cur = "AED", string per = "Monthly", string from = "2026-01-01") =>
        new() { ["code"] = code, ["label"] = label, ["amount"] = amount, ["cur"] = cur, ["per"] = per, ["from"] = from };

    [Fact]
    public async Task People_read_only_their_own_compensation_live_after_step_up_with_a_total_and_an_audit_without_amounts()
    {
        var t = await TenantAsync(people: ["sara", "omar"]);
        var (_, hr) = await RestConnectionAsync(t, compensation: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100", manager: "E-200"), Person("R2", t["omar"].Email, "E-200")];
        hr.Pay["R1"] = [Pay("BASIC", "Basic salary", "15000"), Pay("HOUSING", "Housing allowance", "6000.50", from: "2026-04-01"), Pay("TRANSPORT", "Transport allowance", "1500")];
        await SyncAsync(t);

        Assert.True((await (await t["sara"].Client.GetAsync($"{Compensation}-available")).ReadAsync<JsonElement>()).GetProperty("available").GetBoolean());
        var response = await t["sara"].Client.GetAsync(Compensation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var read = await response.ReadAsync<JsonElement>();
        Assert.Equal("ok", read.GetProperty("state").GetString());
        var statement = read.GetProperty("statement");
        Assert.Equal((22500.50m, "AED", "Monthly", "2026-04-01"), (statement.GetProperty("total").GetDecimal(), statement.GetProperty("currency").GetString(),
            statement.GetProperty("frequency").GetString(), statement.GetProperty("effectiveDate").GetString()));
        Assert.Equal(["BASIC", "HOUSING", "TRANSPORT"], statement.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToArray());
        Assert.Equal(15000m, statement.GetProperty("basic").GetProperty("amount").GetDecimal());

        // Audited with the count, never the amounts.
        var details = await world.Database.TenantScalarAsync<string>(t.Id, "select changes::text from audit.audit_event where action = @a", ("a", AuditActions.HrCompensationViewed));
        Assert.Contains("\"components\": 3", details!, StringComparison.Ordinal);
        Assert.DoesNotContain("15000", details!, StringComparison.Ordinal);

        // Omar, Sara's manager, reads his own (he has none in HR) and cannot name hers; the administrator has no HR record.
        Assert.Equal("ok", (await (await t["omar"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Empty((await (await t["omar"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>()).GetProperty("statement").GetProperty("components").EnumerateArray());
        Assert.Equal("not_matched", (await (await t["admin"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>()).GetProperty("state").GetString());

        // Components that disagree on currency give no total.
        hr.Pay["R1"].Add(Pay("BONUS", "Bonus", "500", cur: "USD"));
        var mixed = (await (await t["sara"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>()).GetProperty("statement");
        Assert.Equal(JsonValueKind.Null, mixed.GetProperty("total").ValueKind);

        // HR down: nothing is shown, not the statement from before.
        hr.Down = true;
        var down = await (await t["sara"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>();
        Assert.Equal(("unavailable", JsonValueKind.Null), (down.GetProperty("state").GetString(), down.GetProperty("statement").ValueKind));
        hr.Down = false;

        // A letter reads one person's statement for that letter, audited with the letter.
        var letter = Guid.NewGuid();
        var forLetter = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var read = await sp.GetRequiredService<IHrCompensation>().ForLetterAsync(t["sara"].Id, letter, CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return read;
        });
        Assert.Equal((HrCompensationRead.Ok, 15000m), (forLetter.State, forLetter.Statement!.Basic!.Amount));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a and changes->>'letter' = @l",
            ("a", AuditActions.HrCompensationReadForLetter), ("l", letter.ToString())));

        // The statement needs a recent sign-in; the availability check shows nothing and does not.
        var endpoints = world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(Compensation, StringComparison.Ordinal) == true).ToList();
        Assert.Equal(2, endpoints.Count);
        Assert.NotNull(endpoints.Single(e => e.RoutePattern.RawText == Compensation).Metadata.GetMetadata<StepUpMetadata>());

        // Without hr.payslips there is no compensation.
        var plain = await TenantAsync("workplace", "sara");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["sara"].Client.GetAsync(Compensation)).StatusCode);
        Assert.False((await (await plain["sara"].Client.GetAsync($"{Compensation}-available")).ReadAsync<JsonElement>()).GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task A_compensation_answer_that_cannot_be_read_whole_shows_nothing()
    {
        var t = await TenantAsync(people: ["sara"]);
        var (_, hr) = await RestConnectionAsync(t, compensation: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100")];
        await SyncAsync(t);
        hr.Pay["R1"] = [Pay("BASIC", "Basic salary", "15000"), Pay("HOUSING", "Housing allowance", "not a number")];
        Assert.Equal("unavailable", (await (await t["sara"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>()).GetProperty("state").GetString());
        hr.Pay["R1"] = Enumerable.Range(1, 31).Select(i => Pay($"C{i}", "Part", "1")).ToList();
        Assert.Equal("unavailable", (await (await t["sara"].Client.GetAsync(Compensation)).ReadAsync<JsonElement>()).GetProperty("state").GetString());
    }
}
