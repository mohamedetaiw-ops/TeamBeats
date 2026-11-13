using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 R2 slice 4 (T026, T027; US6, FR-015, FR-018): sensitive data detection on content publish and document
/// upload. Warnings are answered by removing the data, raising the classification or confirming with an audited reason;
/// block mode stops guest space and "everyone" targets until the data is gone; tenant patterns are tested on sample
/// text before they are enabled; nothing matched is stored or logged. Every number here is made up.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityDetectionTests(TenantWorld world)
{
    private const string Detection = "/api/v1/security/detection";
    private const string Items = "/api/v1/content/items";
    private const string Iban = "GB82WEST12345698765432";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected) => await OkAsync(call, expected);

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sd-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "pia", "officer", "omar", "laila" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);
        return t;
    }

    private static object Variants(string title, string body) => new Dictionary<string, object>
    {
        ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = body } } } } } },
    };

    private static async Task<Guid> DraftAsync(TestTenant t, string body, bool everyone = true, string classification = "internal")
    {
        object? audience = everyone ? null : new { anyOf = new object[] { new { membershipTypes = new[] { "employee" } } } };
        var created = await OkAsync(t["pia"].Client.PostAsJsonAsync(Items, new { type = "page", variants = Variants("Payments", body), classification, audience, ownerId = t["pia"].Id }),
            HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Settings_and_patterns_follow_permissions_plan_and_the_sample_test()
    {
        var t = await FreshAsync();
        var basic = await FreshAsync("workplace");

        var view = await OkAsync(t["officer"].Client.GetAsync(Detection));
        Assert.False(view.GetProperty("enabled").GetBoolean());
        Assert.Equal("warn", view.GetProperty("modes").GetProperty("guest_space").GetString());
        Assert.Contains("saudi_id", view.GetProperty("builtIn").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal((false, true), (view.GetProperty("canManage").GetBoolean(), view.GetProperty("canEditPatterns").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.GetAsync(Detection)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["officer"].Client.PutAsJsonAsync(Detection, new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(Detection)).StatusCode);

        var bad = await t["admin"].Client.PutAsJsonAsync(Detection, new { modes = new Dictionary<string, string> { ["guest_space"] = "shout" } });
        Assert.Contains("validation.security.detection_mode", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var saved = await OkAsync(t["admin"].Client.PutAsJsonAsync(Detection, new { enabled = true, modes = new Dictionary<string, string> { ["everyone"] = "block" } }));
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.Equal("block", saved.GetProperty("modes").GetProperty("everyone").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityDetectionChanged);

        // A tenant pattern is tried on sample text; it is enabled only once a sample matched.
        var trial = await OkAsync(t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns:test", new { pattern = @"\bEMP-\d{6}\b", sample = "EMP-004211, EMP-004212" }));
        Assert.Equal((true, 2), (trial.GetProperty("valid").GetBoolean(), trial.GetProperty("matches").GetInt32()));
        Assert.False((await OkAsync(t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns:test", new { pattern = "(open", sample = "x" }))).GetProperty("valid").GetBoolean());
        var untested = await t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns", new { name = "Employee number", pattern = @"\bEMP-\d{6}\b", enabled = true });
        Assert.Contains("validation.security.pattern_untested", await untested.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var miss = await t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns", new { name = "Employee number", pattern = @"\bEMP-\d{6}\b", sample = "none here", enabled = true });
        Assert.Contains("validation.security.pattern_no_match", await miss.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var pattern = await OkAsync(t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns",
            new { name = "Employee number", pattern = @"\bEMP-\d{6}\b", sample = "Staff EMP-004211", enabled = true }), HttpStatusCode.Created);
        Assert.True(pattern.GetProperty("enabled").GetBoolean());
        var id = pattern.GetProperty("id").GetGuid();
        Assert.Contains("validation.security.pattern_name_taken", await (await t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns",
            new { name = "employee NUMBER", pattern = "x" })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Changing the expression needs a new test before it can stay on; a rename does not.
        Assert.True((await OkAsync(t["officer"].Client.PutAsJsonAsync($"{Detection}/patterns/{id}", new { name = "Staff number" }))).GetProperty("enabled").GetBoolean());
        var changed = await t["officer"].Client.PutAsJsonAsync($"{Detection}/patterns/{id}", new { pattern = @"\bSTF-\d{6}\b" });
        Assert.Contains("validation.security.pattern_untested", await changed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityDetectionPatternChanged, id.ToString());
        // Samples are never kept, and the audit holds the pattern, not sample text.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where changes::text like '%004211%'"));

        // Other tenants see none of it and cannot touch it.
        var other = await FreshAsync();
        Assert.Empty((await OkAsync(other["officer"].Client.GetAsync(Detection))).GetProperty("patterns").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["officer"].Client.DeleteAsync($"{Detection}/patterns/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["laila"].Client.DeleteAsync($"{Detection}/patterns/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["officer"].Client.DeleteAsync($"{Detection}/patterns/{id}")).StatusCode);
    }

    [Fact]
    public async Task Publishing_content_with_sensitive_data_warns_blocks_or_is_confirmed_with_a_reason()
    {
        var t = await FreshAsync();
        var off = await DraftAsync(t, $"Pay to {Iban}.");
        // Detection is off until the tenant turns it on.
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{off}/publish", new { }));

        await OkAsync(t["admin"].Client.PutAsJsonAsync(Detection, new { enabled = true }));
        await OkAsync(t["officer"].Client.PostAsJsonAsync($"{Detection}/patterns", new { name = "Employee number", pattern = @"\bEMP-\d{6}\b", sample = "EMP-000001", enabled = true }),
            HttpStatusCode.Created);

        // Warn: the author sees the type and where it is, never the value.
        var item = await DraftAsync(t, $"Pay to {Iban}, staff EMP-004211.", everyone: false);
        var warning = await ProblemAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{item}/publish", new { }), HttpStatusCode.Conflict);
        Assert.Equal("conflict.security.sensitive_data_found", warning.GetProperty("detailKey").GetString());
        var findings = warning.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Contains(findings, f => f.GetProperty("type").GetString() == "iban" && f.GetProperty("location").GetString() == "body.en");
        Assert.Contains(findings, f => f.GetProperty("type").GetString() == "custom" && f.GetProperty("name").GetString() == "Employee number");
        Assert.DoesNotContain(Iban, warning.GetRawText(), StringComparison.Ordinal);

        // A reason confirms it, and the reason is audited without the data.
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{item}/publish", new { sensitiveReason = "Supplier's published bank details." }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecuritySensitiveDataConfirmed, item.ToString());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from audit.audit_event where changes::text like '%{Iban}%'"));

        // Raising the classification answers the warning too.
        var confidential = await DraftAsync(t, $"Pay to {Iban}.", everyone: false, classification: "confidential");
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{confidential}/publish", new { }));

        // Block for "everyone": no reason gets it out until the data is removed.
        await OkAsync(t["admin"].Client.PutAsJsonAsync(Detection, new { modes = new Dictionary<string, string> { ["everyone"] = "block" } }));
        var wide = await DraftAsync(t, $"Pay to {Iban}.");
        var blocked = await ProblemAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{wide}/publish", new { sensitiveReason = "Needed." }), HttpStatusCode.Conflict);
        Assert.Equal("conflict.security.sensitive_data_blocked", blocked.GetProperty("detailKey").GetString());
        // A passport never blocks; it only warns.
        var passport = await DraftAsync(t, "Bring your passport: A1234567.");
        Assert.Equal("conflict.security.sensitive_data_found",
            (await ProblemAsync(t["pia"].Client.PostAsJsonAsync($"{Items}/{passport}/publish", new { }), HttpStatusCode.Conflict)).GetProperty("detailKey").GetString());

        // Another tenant's settings and patterns do not apply here, and the reverse.
        var other = await FreshAsync();
        var clean = await DraftAsync(other, "Staff EMP-004211 and IBAN " + Iban);
        await OkAsync(other["pia"].Client.PostAsJsonAsync($"{Items}/{clean}/publish", new { }));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid library, string name, byte[] content, string? reason = null) =>
        await client.PostAsync($"/api/v1/documents/libraries/{library}/files?name={name}&publish=true&classification=internal"
            + (reason is null ? "" : "&sensitiveReason=" + Uri.EscapeDataString(reason)), new ByteArrayContent(content));

    private static byte[] Docx(string text)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write($"""<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>""");
        }
        return stream.ToArray();
    }

    [Fact]
    public async Task Uploads_to_a_guest_space_are_blocked_while_other_libraries_warn()
    {
        var t = await FreshAsync();
        await OkAsync(t["admin"].Client.PutAsJsonAsync(Detection, new { enabled = true, modes = new Dictionary<string, string> { ["guest_space"] = "block" } }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/spaces/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        async Task<(Guid Space, Guid Library)> SpaceAsync(string name)
        {
            var created = await OkAsync(t["omar"].Client.PostAsJsonAsync("/api/v1/spaces", new { name, description = "Work.", visibility = "private", template = "project" }),
                HttpStatusCode.Created);
            return (created.GetProperty("id").GetGuid(), created.GetProperty("libraryId").GetGuid());
        }
        var (guestSpace, guestLibrary) = await SpaceAsync("Vendor rollout");
        var (_, ownLibrary) = await SpaceAsync("Budget 2027");
        await world.SeedUserAsync(t, "karim", MembershipTypes.Guest);
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"/api/v1/spaces/{guestSpace}/guests", new { email = t["karim"].Email }));
        var omar = t["omar"].Client;

        // A space with a guest blocks the file; nothing of it is kept.
        var blocked = await ProblemAsync(UploadAsync(omar, guestLibrary, "payees.docx", Docx($"Pay {Iban}"), "Needed."), HttpStatusCode.Conflict);
        Assert.Equal("conflict.security.sensitive_data_blocked", blocked.GetProperty("detailKey").GetString());
        Assert.Equal("file", blocked.GetProperty("findings")[0].GetProperty("location").GetString());
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document where name = 'payees.docx'"));

        // A space with no guests warns; a reason keeps the file, and its text is kept for search.
        var warned = await ProblemAsync(UploadAsync(omar, ownLibrary, "payees.txt", Encoding.UTF8.GetBytes($"Pay {Iban}")), HttpStatusCode.Conflict);
        Assert.Equal("conflict.security.sensitive_data_found", warned.GetProperty("detailKey").GetString());
        var kept = await OkAsync(UploadAsync(omar, ownLibrary, "payees.txt", Encoding.UTF8.GetBytes($"Pay {Iban}"), "Supplier list."), HttpStatusCode.Created);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecuritySensitiveDataConfirmed, kept.GetProperty("id").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document_version where text_extract like 'Pay %'"));

        // Files with nothing sensitive, and formats with no text, go through as before.
        await OkAsync(UploadAsync(omar, guestLibrary, "plan.txt", Encoding.UTF8.GetBytes("Kick-off on Sunday.")), HttpStatusCode.Created);
        await OkAsync(UploadAsync(omar, guestLibrary, "scan.png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), HttpStatusCode.Created);

        // On Workplace detection is not in the plan, so nothing is checked.
        var basic = await FreshAsync("workplace");
        var library = (await OkAsync(basic["admin"].Client.PostAsJsonAsync("/api/v1/documents/libraries",
            new { names = new Dictionary<string, string> { ["en"] = "Finance" }, scope = "tenant" }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(UploadAsync(basic["admin"].Client, library, "payees.txt", Encoding.UTF8.GetBytes($"Pay {Iban}")), HttpStatusCode.Created);
    }
}
