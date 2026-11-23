using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 T019 (section 14): a compliance officer attaches the scanned signed forms and imports a CSV of who signed
/// which version on which day. Good lines become attestations with source "paper", the importer and the import kept,
/// settle the campaign membership and are counted apart; bad lines are reported by line. Employees cannot import,
/// a file that is not a scan or carries malware is refused, an import runs once, and other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PolicyPaperTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ppa-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali", "eman", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static async Task<Guid> PublishedAsync(TestTenant t, string code)
    {
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names = Both(code), route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var clause = new
        {
            level = 1, titles = Both("Use"),
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Use it well." } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "استخدمه جيدًا." } } } } },
            },
        };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both(code), clauses = new[] { clause } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    private static Task<HttpResponseMessage> ScanAsync(HttpClient client, Guid policy, string name, string type, string content)
    {
        var body = new ByteArrayContent(Encoding.Latin1.GetBytes(content));
        body.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PostAsync($"{Policies}/{policy}/paper-imports?name={Uri.EscapeDataString(name)}", body);
    }

    private static Task<HttpResponseMessage> RowsAsync(HttpClient client, Guid policy, Guid import, string csv) =>
        client.PostAsync($"{Policies}/{policy}/paper-imports/{import}/rows", new StringContent(csv, Encoding.UTF8, "text/csv"));

    private static string Email(TestTenant t, string user) => $"{user}@{t.Key}.example";

    [Fact]
    public async Task Officers_import_paper_attestations_with_the_scan_and_bad_lines_are_reported()
    {
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var campaign = (await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{aup}/campaigns", new { }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await world.DispatchTenantAsync(t.Id);

        // Only a scan, only from someone who manages the policy, and never malware.
        Assert.Equal(HttpStatusCode.Forbidden, (await ScanAsync(t["ali"].Client, aup, "forms.pdf", "application/pdf", "%PDF-1.7 signed")).StatusCode);
        Assert.Contains("validation.policies.paper_scan_type", await ErrorKeysAsync(await ScanAsync(t["co"].Client, aup, "forms.pdf", "application/pdf", "not a pdf")));
        Assert.Contains("validation.policies.paper_scan_type", await ErrorKeysAsync(await ScanAsync(t["co"].Client, aup, "forms.txt", "text/plain", "%PDF-1.7")));
        Assert.Contains("validation.policies.paper_scan_infected", await ErrorKeysAsync(await ScanAsync(t["co"].Client, aup, "forms.pdf", "application/pdf",
            "%PDF-1.7 " + TestSignatureScanner.Eicar)));
        var draft = await OkAsync(ScanAsync(t["co"].Client, aup, "signed-forms.pdf", "application/pdf", "%PDF-1.7 signed forms"), HttpStatusCode.Created);
        var import = draft.GetProperty("id").GetGuid();
        Assert.Equal("draft", draft.GetProperty("status").GetString());

        // The header is checked as a whole; each line on its own.
        Assert.Contains("validation.policies.paper_header", await ErrorKeysAsync(await RowsAsync(t["co"].Client, aup, import, "name,date\nAli,2026-01-01\n")));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd");
        var csv = new StringBuilder("email,attested_on,version\n")
            .Append(Email(t, "ali")).Append(',').Append(today).Append(",\n")
            .Append(Email(t, "eman")).Append(',').Append(tomorrow).Append(",\n")
            .Append("nobody@").Append(t.Key).Append(".example,").Append(today).Append(",\n")
            .Append(Email(t, "ali")).Append(',').Append(today).Append(",1\n")
            .Append(Email(t, "omar")).Append(',').Append(today).Append(",7\n")
            .Append("\"").Append(Email(t, "eman")).Append("\",\"").Append(today).Append("\",1\n")
            .ToString();
        Assert.Equal(HttpStatusCode.Forbidden, (await RowsAsync(t["ali"].Client, aup, import, csv)).StatusCode);
        var report = await OkAsync(RowsAsync(t["co"].Client, aup, import, csv));
        Assert.Equal("completed", report.GetProperty("status").GetString());
        Assert.Equal(2, report.GetProperty("imported").GetInt32());
        Assert.Equal(
            [(3, "validation.policies.paper_date"), (4, "validation.policies.paper_person"), (5, "validation.policies.paper_duplicate"), (6, "validation.policies.paper_version")],
            report.GetProperty("rejected").EnumerateArray().Select(r => (r.GetProperty("line").GetInt32(), r.GetProperty("key").GetString()!)).ToArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyPaperImported, aup.ToString());
        Assert.Contains("conflict.policies.paper_import_done", await ErrorKeysAsync(await RowsAsync(t["co"].Client, aup, import, csv)));

        // The records say paper, who imported them and from which import; the campaign counts them apart.
        Assert.Equal(2L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from policies.attestation where source = 'paper' and channel = 'paper' and imported_by = @co and import_id = @import",
            ("co", t["co"].Id), ("import", import)));
        var counts = (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{aup}/campaigns"))).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == campaign)
            .GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("attested").GetInt32());
        Assert.Equal(2, counts.GetProperty("paper").GetInt32());
        Assert.Contains("conflict.policies.nothing_to_attest", await ErrorKeysAsync(await t["ali"].Client.PostAsJsonAsync($"{Policies}/{aup}/attest", new { })));

        // A second import refuses those already attested; the scan opens for officers only.
        var again = (await OkAsync(ScanAsync(t["co"].Client, aup, "more.png", "image/png", "\u0089PNG more"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var second = await OkAsync(RowsAsync(t["co"].Client, aup, again, $"email,attested_on\n{Email(t, "ali")},{today}\n"));
        Assert.True(second.GetProperty("rejected").GetArrayLength() == 1, second.ToString());
        Assert.Equal("validation.policies.paper_already", second.GetProperty("rejected")[0].GetProperty("key").GetString());
        Assert.Equal(2, (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{aup}/paper-imports"))).GetArrayLength());
        var scan = await t["co"].Client.GetAsync($"{Policies}/{aup}/paper-imports/{import}/scan");
        Assert.Equal("%PDF-1.7 signed forms", await scan.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync($"{Policies}/{aup}/paper-imports/{import}/scan")).StatusCode);
    }

    [Fact]
    public async Task Without_campaigns_there_is_no_import_and_other_tenants_reach_nothing()
    {
        var plain = await FreshAsync("workplace");
        var t = await FreshAsync();
        var aup = await PublishedAsync(t, "AUP");
        var import = (await OkAsync(ScanAsync(t["co"].Client, aup, "forms.pdf", "application/pdf", "%PDF-1.7 signed"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ScanAsync(other["co"].Client, aup, "forms.pdf", "application/pdf", "%PDF-1.7 x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RowsAsync(other["co"].Client, aup, import, "email,attested_on\n")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.GetAsync($"{Policies}/{aup}/paper-imports/{import}/scan")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["co"].Client.GetAsync($"{Policies}/{Guid.NewGuid()}/paper-imports")).StatusCode);
    }
}
