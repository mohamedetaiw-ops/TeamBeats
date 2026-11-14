using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R2 FR-022: a library manager imports a ZIP with a manifest. The manifest is checked whole; then each row
/// becomes a document in its folder with its type, metadata, original dates and matched author, or keeps the reason it
/// did not; duplicates are skipped, infected files quarantined, and the starter hears when it is done. Only managers
/// import, and another tenant never sees the import.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentImportTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Imports = "/api/v1/documents/imports";

    private async Task<(TestTenant T, Guid Library, Guid Type)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("imp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "maya", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var admin = t["admin"].Client;
        var library = Id(await OkAsync(admin.PostAsJsonAsync(Libraries, new { names = new Dictionary<string, string> { ["en"] = "Archive" }, scope = "tenant" }),
            HttpStatusCode.Created));
        var type = Id(await OkAsync(admin.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "Procedure", ["ar"] = "إجراء" },
            fields = new object[]
            {
                new { key = "site", names = new Dictionary<string, string> { ["en"] = "Site" }, kind = "text", required = true },
                new { key = "reviewed", names = new Dictionary<string, string> { ["en"] = "Reviewed" }, kind = "boolean" },
            },
        }), HttpStatusCode.Created));
        return (t, library, type);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value.EnumerateArray().First().GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static byte[] Zip(string? manifest, params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifest is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry("manifest.csv").Open(), new UTF8Encoding(true));
                writer.Write(manifest);
            }
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, Guid library, byte[] zip, string name = "share.zip") =>
        client.PostAsync($"{Libraries}/{library}/imports?name={Uri.EscapeDataString(name)}", new ByteArrayContent(zip));

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 50 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    [Fact]
    public async Task A_manager_imports_a_zip_and_every_row_keeps_its_result()
    {
        var (t, library, _) = await FreshAsync();
        var admin = t["admin"].Client;
        var existing = await admin.PostAsync($"{Libraries}/{library}/files?name=old.txt&publish=true", new ByteArrayContent(Encoding.UTF8.GetBytes("already here")));
        Assert.Equal(HttpStatusCode.Created, existing.StatusCode);
        var maya = t["maya"];

        var manifest =
            $"""
            path,folder,name,type,created,modified,author,field:site,field:reviewed
            hr/leave.txt,Policies/HR,Leave policy.txt,Procedure,2019-03-01,2021-06-30T10:00:00Z,{maya.Email},Dubai,yes
            hr/travel.txt,Policies/HR,,,2018-01-01,,nobody@example.invalid,,
            "notes, misc.txt",,,,,,,,
            dup.txt,,,,,,,,
            missing.txt,,,,,,,,
            bad.exe,,,,,,,,
            draft.txt,,,Procedure,,,,,
            virus.txt,,,,,,,,
            future.txt,,,,2999-01-01,,,,
            """;
        var zip = Zip(manifest, ("hr/leave.txt", "Leave rules"), ("hr/travel.txt", "Travel rules"), ("notes, misc.txt", "Notes"), ("dup.txt", "already here"),
            ("bad.exe", "MZ"), ("draft.txt", "Needs a site"), ("virus.txt", TestSignatureScanner.Eicar), ("future.txt", "Later"));

        // Only the library's managers import.
        Assert.Equal(HttpStatusCode.Forbidden, (await ImportAsync(t["omar"].Client, library, zip)).StatusCode);

        var started = await OkAsync(ImportAsync(admin, library, zip), HttpStatusCode.Created);
        var import = Id(started);
        Assert.Equal(9, started.GetProperty("total").GetInt32());
        Assert.Equal(9, started.GetProperty("pending").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocImportStarted, import.ToString());

        await DispatchAsync();

        var done = await OkAsync(admin.GetAsync($"{Imports}/{import}"));
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal(3, done.GetProperty("imported").GetInt32());
        Assert.Equal(1, done.GetProperty("incomplete").GetInt32());
        Assert.Equal(1, done.GetProperty("skipped").GetInt32());
        Assert.Equal(1, done.GetProperty("quarantined").GetInt32());
        Assert.Equal(3, done.GetProperty("failed").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocImportCompleted, import.ToString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'documents.import_finished'", ("u", t["admin"].Id)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.import where object_key is not null"));

        var rows = (await OkAsync(admin.GetAsync($"{Imports}/{import}/rows"))).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("path").GetString()!, r => r);
        Assert.Equal("skipped", rows["dup.txt"].GetProperty("status").GetString());
        Assert.Equal("conflict.documents.duplicate", rows["dup.txt"].GetProperty("problem").GetString());
        Assert.False(string.IsNullOrEmpty(rows["dup.txt"].GetProperty("problemText").GetString()));
        Assert.NotEqual("conflict.documents.duplicate", rows["dup.txt"].GetProperty("problemText").GetString());
        Assert.Equal("validation.documents.import_missing", rows["missing.txt"].GetProperty("problem").GetString());
        Assert.Equal("validation.documents.file_type", rows["bad.exe"].GetProperty("problem").GetString());
        Assert.Equal("validation.documents.import_date", rows["future.txt"].GetProperty("problem").GetString());
        Assert.Equal("incomplete", rows["draft.txt"].GetProperty("status").GetString());
        Assert.Equal("quarantined", rows["virus.txt"].GetProperty("status").GetString());
        Assert.True(rows["hr/leave.txt"].GetProperty("authorMatched").GetBoolean());
        Assert.False(rows["hr/travel.txt"].GetProperty("authorMatched").GetBoolean());
        Assert.Equal("imported", rows["notes, misc.txt"].GetProperty("status").GetString());

        // The leave policy keeps its folder, type, metadata, dates and author; the importer owns it.
        var leave = rows["hr/leave.txt"].GetProperty("documentId").GetGuid();
        var doc = await OkAsync(admin.GetAsync($"/api/v1/documents/{leave}"));
        Assert.Equal("Leave policy.txt", doc.GetProperty("name").GetString());
        Assert.Equal("Dubai", doc.GetProperty("metadata").GetProperty("site").GetString());
        Assert.True(doc.GetProperty("metadata").GetProperty("reviewed").GetBoolean());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            """
            select count(*) from documents.document d join documents.folder f on f.id = d.folder_id join documents.folder p on p.id = f.parent_id
            join documents.document_version v on v.document_id = d.id
            where d.id = @id and f.name = 'HR' and p.name = 'Policies' and d.created_at = '2019-03-01T00:00:00Z' and d.updated_at = '2021-06-30T10:00:00Z'
              and d.created_by = @maya and v.author_user_id = @maya and v.created_at = '2021-06-30T10:00:00Z' and d.owner_user_id = @admin
            """, ("id", leave), ("maya", maya.Id), ("admin", t["admin"].Id)));
        // Both HR rows share one folder.
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.folder"));

        var report = await admin.GetStringAsync($"{Imports}/{import}/report");
        Assert.Contains("\"notes, misc.txt\",imported", report, StringComparison.Ordinal);
        Assert.Contains("\"hr/travel.txt\",imported,\"\",", report, StringComparison.Ordinal);
        var failed = await OkAsync(admin.GetAsync($"{Imports}/{import}/rows?status=failed"));
        Assert.Equal(3, failed.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"{Imports}/{import}/rows?status=odd")).StatusCode);

        // Readers of the library do not see imports; another tenant never does.
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.GetAsync($"{Imports}/{import}")).StatusCode);
        var (other, _, _) = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Imports}/{import}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Imports}/{import}/report")).StatusCode);
    }

    [Fact]
    public async Task A_zip_without_a_valid_manifest_is_refused_whole()
    {
        var (t, library, _) = await FreshAsync();
        var admin = t["admin"].Client;
        Assert.Equal("validation.documents.import_zip", await ProblemKeyAsync(ImportAsync(admin, library, Zip("path\na.txt\n", ("a.txt", "x")), "share.rar"),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.import_zip", await ProblemKeyAsync(ImportAsync(admin, library, Encoding.UTF8.GetBytes("not a zip")), HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.import_no_manifest", await ProblemKeyAsync(ImportAsync(admin, library, Zip(null, ("a.txt", "x"))), HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.import_columns", await ProblemKeyAsync(ImportAsync(admin, library, Zip("file,owner\na.txt,x\n", ("a.txt", "x"))),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.import_paths", await ProblemKeyAsync(ImportAsync(admin, library, Zip("path\na.txt\na.txt\n", ("a.txt", "x"))),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.import_paths", await ProblemKeyAsync(ImportAsync(admin, library, Zip("path\n../a.txt\n", ("a.txt", "x"))),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.import_manifest", await ProblemKeyAsync(ImportAsync(admin, library, Zip("path,name\na.txt\n", ("a.txt", "x"))),
            HttpStatusCode.BadRequest));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.import"));
        Assert.Empty((await OkAsync(admin.GetAsync($"{Libraries}/{library}/imports"))).EnumerateArray());
    }
}
