using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Letters;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 slice 5 (US7, FR-021, FR-022): a letter type without approval is issued at once from the HR record; one
/// with approval goes to HR officers covering the person, who type in what HR does not hold. The PDF carries the
/// reference and a verification code, the public page shows only the reference, type, date and company, a template change
/// leaves issued letters as they were, and other tenants, sibling units and a turned off page see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed partial class LetterTests(TenantWorld world)
{
    private const string Mine = "/api/v1/me/letters";
    private const string Types = "/api/v1/hr/letter-types";
    private const string SettingsUrl = "/api/v1/hr/letter-settings";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    [GeneratedRegex(@"[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}")]
    private static partial Regex CodePattern();

    private async Task<(TestTenant T, Guid Gulf, Guid Egypt)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("lt-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
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
        return (t, gulf, egypt);
    }

    /// <summary>Mona's matched HR record: her names and employee number, no job title.</summary>
    private async Task HrRecordAsync(TestTenant t)
    {
        var data = new Dictionary<string, string>
        {
            ["record_id"] = "E-100", ["status"] = "active", ["name_en"] = "Mona Hassan", ["name_ar"] = "منى حسن", ["employee_number"] = "100",
            ["hire_date"] = "2021-03-14",
        };
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @c, 'hr_rest', 'HR', 'healthy', true, 600);
            insert into integrations.hr_employee (tenant_id, record_id, connection_id, data, user_id, match_state, first_seen_at, last_seen_at, last_run_id)
            values (@t, 'E-100', @c, @data::jsonb, @u, 'matched', now(), now(), gen_random_uuid())
            """, ("t", t.Id), ("c", Guid.NewGuid()), ("data", JsonSerializer.Serialize(data)), ("u", t["mona"].Id));
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

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        return await client.SendAsync(request);
    }

    private static object Employment(bool needsApproval = false, Guid? unit = null, string en = "This is to certify that {name}, employee {employee_number}, has worked at {company} since {hire_date}.\n\n{addressee}",
        string ar = "نشهد بأن {name} يعمل لدى {company} منذ {hire_date}.\n\n{addressee}", object? manual = null) => new
    {
        names = Names("Employment certificate", "شهادة عمل"),
        templates = Names(en, ar),
        manualFields = manual ?? Array.Empty<object>(),
        needsApproval,
        orgUnitId = unit,
    };

    private static async Task<JsonElement?> TaskForAsync(HttpClient client, Guid letterId) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray()
            .Where(i => i.GetProperty("source").GetString() == "letter" && i.GetProperty("link").GetString()!.Contains(letterId.ToString(), StringComparison.Ordinal))
            .Cast<JsonElement?>().SingleOrDefault();

    private static async Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid letterId, string outcome)
    {
        var task = await TaskForAsync(client, letterId);
        Assert.NotNull(task);
        return await client.PostAsJsonAsync($"/api/v1/tasks/{task.Value.GetProperty("taskId").GetGuid()}/decision",
            new { outcome, comment = outcome == "approve" ? null : "Not for this bank" });
    }

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    private static async Task<string> PdfTextAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/v1/hr/letters/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_letter_without_approval_is_issued_at_once_and_its_code_verifies_only_the_public_details()
    {
        var (t, _, _) = await FreshAsync();
        await HrRecordAsync(t);
        var mona = t["mona"].Client;

        // Only HR officers manage types; the whole tenant type needs one covering the whole tenant.
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(mona, $"{Types}/employment", Employment())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(t["hr_gulf"].Client, $"{Types}/employment", Employment())).StatusCode);
        Assert.Contains("validation.letters.placeholder", await ErrorKeysAsync(await PutAsync(t["admin"].Client, $"{Types}/employment", Employment(en: "Hello {salary}"))));
        Assert.Contains("validation.letters.manual_needs_approval", await ErrorKeysAsync(await PutAsync(t["admin"].Client, $"{Types}/employment",
            Employment(manual: new[] { new { key = "salary", names = Names("Salary", "الراتب") } }))));
        Assert.Contains("validation.letters.templates", await ErrorKeysAsync(await PutAsync(t["admin"].Client, $"{Types}/employment", Employment(ar: ""))));
        var type = await OkAsync(PutAsync(t["admin"].Client, $"{Types}/employment", Employment()));
        Assert.Equal(1, type.GetProperty("version").GetInt32());
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrLetterTypeChanged));

        // The employee sees the choice, never the template.
        var choices = await OkAsync(mona.GetAsync($"{Mine}/types"));
        Assert.Equal("employment", choices[0].GetProperty("key").GetString());
        Assert.False(choices[0].TryGetProperty("templates", out _));

        var bad = await mona.PostAsJsonAsync(Mine, new { type = "nope", language = "fr" });
        Assert.Contains("validation.letters.type", await ErrorKeysAsync(bad));
        Assert.Contains("validation.letters.language", await ErrorKeysAsync(bad));

        var letter = await OkAsync(mona.PostAsJsonAsync(Mine, new { type = "employment", language = "en", addressee = "Emirates Bank" }), HttpStatusCode.Created);
        var id = letter.GetProperty("id").GetGuid();
        Assert.Equal("issued", letter.GetProperty("status").GetString());
        Assert.Equal("LT-000001", letter.GetProperty("reference").GetString());
        Assert.True(letter.GetProperty("rights").GetProperty("download").GetBoolean());
        Assert.Equal(JsonValueKind.Null, letter.GetProperty("fields").ValueKind);

        var pdf = await PdfTextAsync(mona, id);
        Assert.StartsWith("%PDF", pdf, StringComparison.Ordinal);
        Assert.Contains("Mona Hassan, employee 100", pdf, StringComparison.Ordinal);
        Assert.Contains("14 March 2021", pdf, StringComparison.Ordinal);
        Assert.Contains("Emirates Bank", pdf, StringComparison.Ordinal);
        Assert.Contains("LT-000001", pdf, StringComparison.Ordinal);
        Assert.Contains($"https://{t.Key}.", pdf, StringComparison.Ordinal);
        var code = CodePattern().Match(pdf).Value;
        Assert.NotEmpty(code);
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from hr.letter where code_hash = sha256(convert_to(@c, 'UTF8')) and status = 'issued'", ("c", code)));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from hr.letter where code_hash = sha256(convert_to(@c, 'UTF8')) and status = 'issued'", ("c", code.Replace("-", "", StringComparison.Ordinal))));
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrLetterIssued));
        Assert.Equal(1L, await world.Database.ScalarInTenantAsync<long>(t.Id,
            "select count(*) from messaging.outbox where tenant_id = @t and message_type like '%LetterIssued%'", ("t", t.Id)));

        // The public page: the reference, type, issue date and company, nothing about the person, typed in any case.
        var anonymous = world.Factory.TenantClient(t.Key);
        var verified = await OkAsync(anonymous.GetAsync($"/api/v1/letters/verify/{code.ToLowerInvariant()}"));
        Assert.Equal("LT-000001", verified.GetProperty("reference").GetString());
        Assert.Equal("Employment certificate", verified.GetProperty("typeNames").GetProperty("en").GetString());
        Assert.Equal(new[] { "reference", "typeNames", "issuedOn", "companyName" }, verified.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.DoesNotContain("Mona", verified.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrLetterVerified));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/letters/verify/AAAA-AAAA-AAAA")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/letters/verify/short")).StatusCode);

        // Another tenant's host, the platform host and another tenant's admin see nothing.
        var other = await world.CreateTenantAsync("lt-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.TenantClient(other.Key).GetAsync($"/api/v1/letters/verify/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.PlatformClient().GetAsync($"/api/v1/letters/verify/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"/api/v1/hr/letters/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"/api/v1/hr/letters/{id}/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"/api/v1/hr/letters/{id}/pdf")).StatusCode);
        await PdfTextAsync(t["hr_gulf"].Client, id);

        // A template change leaves the issued letter as it was.
        var changed = await OkAsync(PutAsync(t["admin"].Client, $"{Types}/employment", Employment(en: "Changed text for {name}."), type.GetProperty("eTag").GetString()));
        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(t["admin"].Client, $"{Types}/employment", Employment(), type.GetProperty("eTag").GetString())).StatusCode);
        Assert.Contains("Mona Hassan, employee 100", await PdfTextAsync(mona, id), StringComparison.Ordinal);
        Assert.Equal(1, (await OkAsync(mona.GetAsync(Mine))).GetArrayLength());

        // The tenant can turn the page off; the code then verifies nothing.
        var settings = await t["admin"].Client.GetAsync(SettingsUrl);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(t["hr_gulf"].Client, SettingsUrl, new { verificationEnabled = false }, settings.Headers.ETag!.Tag)).StatusCode);
        await OkAsync(PutAsync(t["admin"].Client, SettingsUrl, new { verificationEnabled = false }, settings.Headers.ETag!.Tag));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/letters/verify/{code}")).StatusCode);
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrLetterSettingsChanged));
    }

    [Fact]
    public async Task A_letter_needing_approval_waits_for_HR_covering_the_person_who_types_in_what_HR_lacks()
    {
        var (t, gulf, _) = await FreshAsync();
        await HrRecordAsync(t);
        var mona = t["mona"].Client;
        await OkAsync(PutAsync(t["hr_gulf"].Client, $"{Types}/salary", new
        {
            names = Names("Salary certificate", "شهادة راتب"),
            templates = Names("{name} works as {job_title} and earns {salary} a month.\n\n{addressee}", "{name} يعمل {job_title} وراتبه {salary} شهريًا.\n\n{addressee}"),
            manualFields = new[] { new { key = "salary", names = Names("Monthly salary", "الراتب الشهري") } },
            needsApproval = true,
            orgUnitId = gulf,
        }));
        Assert.Empty((await OkAsync(t["hr_egypt"].Client.GetAsync($"{Mine}/types"))).EnumerateArray());

        var letter = await OkAsync(mona.PostAsJsonAsync(Mine, new { type = "salary", language = "ar" }), HttpStatusCode.Created);
        var id = letter.GetProperty("id").GetGuid();
        var url = $"/api/v1/hr/letters/{id}";
        Assert.Equal("pending", letter.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, letter.GetProperty("fields").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await mona.GetAsync($"{url}/pdf")).StatusCode);

        // The person never gets the task; a sibling unit's officer sees nothing.
        Assert.Null(await TaskForAsync(mona, id));
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(mona, $"{url}/values", new { values = new { salary = "1" } })).StatusCode);

        var seen = await OkAsync(t["hr_gulf"].Client.GetAsync(url));
        Assert.True(seen.GetProperty("rights").GetProperty("editValues").GetBoolean());
        var fields = seen.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!, f => f);
        Assert.Equal("منى حسن", fields["name"].GetProperty("value").GetString());
        Assert.True(fields["name"].GetProperty("fromHr").GetBoolean());
        Assert.Equal(JsonValueKind.Null, fields["job_title"].GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, fields["salary"].GetProperty("value").ValueKind);

        // Approving with a value missing is refused and the task stays open.
        var early = await DecideAsync(t["hr_gulf"].Client, id, "approve");
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("conflict.letters.data_missing", await early.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("validation.letters.value", await ErrorKeysAsync(await PutAsync(t["hr_gulf"].Client, $"{url}/values", new { values = new { name = "Someone else" } })));
        await OkAsync(PutAsync(t["hr_gulf"].Client, $"{url}/values", new { values = new { salary = "25000 AED", job_title = "مهندسة" } }));

        // The conversion service is down: the letter is approved and waits; the worker issues it later.
        world.Factory.Gotenberg.HtmlDown = true;
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["hr_gulf"].Client, id, "approve")).StatusCode);
            Assert.Equal("approved", (await OkAsync(mona.GetAsync(url))).GetProperty("status").GetString());
        }
        finally
        {
            world.Factory.Gotenberg.HtmlDown = false;
        }
        Assert.Equal(1, await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var issued = await sp.GetRequiredService<LetterRequests>().IssueDueAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return issued;
        }));
        var done = await OkAsync(mona.GetAsync(url));
        Assert.Equal("issued", done.GetProperty("status").GetString());
        var pdf = await PdfTextAsync(mona, id);
        Assert.Contains("dir=\"rtl\"", pdf, StringComparison.Ordinal);
        Assert.Contains("منى حسن يعمل مهندسة وراتبه 25000 AED", pdf, StringComparison.Ordinal);
        Assert.Contains("إلى من يهمه الأمر", pdf, StringComparison.Ordinal);
        // The typed values are cleared once the PDF holds them.
        Assert.Equal("{}", await world.Database.TenantScalarAsync<string>(t.Id, "select manual_values::text from hr.letter where id = @id", ("id", id)));

        // A rejected letter is never issued; a withdrawn one leaves no task.
        var second = (await OkAsync(mona.PostAsJsonAsync(Mine, new { type = "salary", language = "en", addressee = "Embassy" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(t["hr_gulf"].Client, second, "reject")).StatusCode);
        Assert.Equal("rejected", (await OkAsync(mona.GetAsync($"/api/v1/hr/letters/{second}"))).GetProperty("status").GetString());
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrLetterRejected));
        var third = (await OkAsync(mona.PostAsJsonAsync(Mine, new { type = "salary", language = "en" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_gulf"].Client.PostAsync($"/api/v1/hr/letters/{third}:withdraw", null)).StatusCode);
        Assert.Equal("withdrawn", (await OkAsync(mona.PostAsync($"/api/v1/hr/letters/{third}:withdraw", null))).GetProperty("status").GetString());
        Assert.Null(await TaskForAsync(t["hr_gulf"].Client, third));

        var list = await OkAsync(t["hr_gulf"].Client.GetAsync("/api/v1/hr/letters"));
        Assert.Equal(3, list.GetArrayLength());
        Assert.Empty((await OkAsync(t["hr_egypt"].Client.GetAsync("/api/v1/hr/letters"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await mona.GetAsync("/api/v1/hr/letters")).StatusCode);
    }

    [Fact]
    public async Task A_letter_without_approval_and_with_HR_data_missing_cannot_be_asked_for()
    {
        var (t, _, _) = await FreshAsync();
        await OkAsync(PutAsync(t["admin"].Client, $"{Types}/experience", Employment(en: "{name} holds the grade {grade}.", ar: "{name} على الدرجة {grade}.")));
        var refused = await t["mona"].Client.PostAsJsonAsync(Mine, new { type = "experience", language = "en" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("conflict.letters.data_missing", await ErrorKeysAsync(refused));
        Assert.Empty((await OkAsync(t["mona"].Client.GetAsync(Mine))).EnumerateArray());
        Assert.Equal(0L, await AuditCountAsync(t, AuditActions.HrLetterRequested));
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrLetterTypeChanged));
        Assert.Single((await OkAsync(t["admin"].Client.GetAsync(Types))).EnumerateArray());
    }
}
