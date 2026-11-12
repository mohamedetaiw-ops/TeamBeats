using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 T004 and T005 (US1): libraries at tenant, unit and site scope; document types with required fields that keep
/// an upload a draft only its uploader sees; type, size, empty and duplicate refusals; quarantine on a scan hit;
/// sibling units and other tenants get not found; metadata filters, sorting and saved views; permission breaks on
/// folders and documents; download tokens bound to the person and tenant; the plan's storage limit and capability.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentLibraryTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";

    private sealed record Org(TestTenant T, Guid Dubai, Guid Cairo);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("dl-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        var cairo = await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "lm", "cm", "con", "dee", "cai", "out" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "dee", "Dubai");
        await world.AddMembershipAsync(t, "con", "Dubai");
        await world.AddMembershipAsync(t, "cai", "Cairo");
        await world.AssignAsync(t, "lm", BuiltInRoles.OrganizationAdministrator, "org_subtree", dubai);
        await world.AssignAsync(t, "cm", BuiltInRoles.OrganizationAdministrator, "org_subtree", cairo);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "doc_contributor", names = new Dictionary<string, string> { ["en"] = "Contributor" },
            permissions = new[] { Permissions.DocView, Permissions.DocContribute }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "con", "doc_contributor", "org_unit", dubai);
        return new Org(t, dubai, cairo);
    }

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en, ["ar"] = "مكتبة " + en };

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

    private static Task<JsonElement> LibraryAsync(HttpClient client, string name, string scope, Guid? scopeId, object? extra = null) =>
        OkAsync(client.PostAsJsonAsync(Libraries, Merge(new { names = Names(name), scope, scopeId }, extra)), HttpStatusCode.Created);

    private static object Merge(object a, object? b)
    {
        var d = JsonSerializer.SerializeToNode(a)!.AsObject();
        if (b is not null)
        {
            foreach (var (k, v) in JsonSerializer.SerializeToNode(b)!.AsObject())
            {
                d[k] = v?.DeepClone();
            }
        }
        return d;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid library, string name, string content, Guid? folder = null, Guid? type = null,
        object? metadata = null, bool publish = true, string? classification = null)
    {
        var query = new List<string> { "name=" + Uri.EscapeDataString(name), "publish=" + (publish ? "true" : "false") };
        if (folder is { } f) query.Add($"folder={f}");
        if (type is { } t) query.Add($"type={t}");
        if (metadata is not null) query.Add("metadata=" + Uri.EscapeDataString(JsonSerializer.Serialize(metadata)));
        if (classification is not null) query.Add("classification=" + classification);
        return client.PostAsync($"{Libraries}/{library}/files?{string.Join('&', query)}", new ByteArrayContent(Encoding.UTF8.GetBytes(content)));
    }

    private static async Task<Guid> PublishedAsync(HttpClient client, Guid library, string name, string content, Guid? folder = null, Guid? type = null,
        object? metadata = null, string? classification = null)
    {
        var doc = await OkAsync(UploadAsync(client, library, name, content, folder, type, metadata, true, classification), HttpStatusCode.Created);
        Assert.Equal("1.0", doc.GetProperty("effective").GetProperty("label").GetString());
        return doc.GetProperty("id").GetGuid();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static async Task<IReadOnlyList<Guid>> ItemIdsAsync(HttpClient client, Guid library, string query = "")
    {
        var page = await OkAsync(client.GetAsync($"{Libraries}/{library}/items{query}"));
        return page.GetProperty("items").EnumerateArray().Select(Id).ToList();
    }

    private static async Task<string> DownloadAsync(HttpClient client, Guid document)
    {
        var token = await OkAsync(client.GetAsync($"/api/v1/documents/{document}/download-token"));
        var file = await client.GetAsync(token.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());
        return await file.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_unit_library_is_read_by_its_members_and_hidden_from_siblings_other_tenants_and_other_people_holding_a_token()
    {
        var o = await FreshAsync();
        var t = o.T;
        // A Cairo manager cannot create a library for Dubai.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["cm"].Client.PostAsJsonAsync(Libraries, new { names = Names("Quality"), scope = "org_unit", scopeId = o.Dubai })).StatusCode);
        var library = Id(await LibraryAsync(t["lm"].Client, "Quality", "org_unit", o.Dubai));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocLibraryCreated, library.ToString());

        var doc = await PublishedAsync(t["con"].Client, library, "Working at height.pdf", "procedure body", classification: "confidential");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocUploaded, doc.ToString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DocumentVersionPublished", t.Id));

        // Dubai members read it; the Cairo member and a person in no unit see nothing, not even that it exists.
        var mine = await OkAsync(t["dee"].Client.GetAsync(Libraries));
        Assert.Contains(mine.EnumerateArray(), l => Id(l) == library);
        Assert.Equal([doc], await ItemIdsAsync(t["dee"].Client, library));
        Assert.Equal("procedure body", await DownloadAsync(t["dee"].Client, doc));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocDownloaded, doc.ToString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DocumentDownloaded", t.Id));
        foreach (var name in new[] { "cai", "out", "cm" })
        {
            var client = t[name].Client;
            Assert.DoesNotContain((await OkAsync(client.GetAsync(Libraries))).EnumerateArray(), l => Id(l) == library);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Libraries}/{library}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Libraries}/{library}/items")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/documents/{doc}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/documents/{doc}/download-token")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(client, library, "x.pdf", "x")).StatusCode);
        }
        // Readers do not upload.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(t["dee"].Client, library, "mine.pdf", "mine")).StatusCode);

        // A token works only for the person it was issued to, and only in its tenant.
        var token = (await OkAsync(t["dee"].Client.GetAsync($"/api/v1/documents/{doc}/download-token"))).GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await t["con"].Client.GetAsync(token)).StatusCode);
        var other = await world.CreateTenantAsync("dl-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Libraries}/{library}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"/api/v1/documents/{doc}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"/api/v1/authz/resources/doc_library/{library}/acl")).StatusCode);
        // Another tenant's unit is not a scope here.
        var foreign = await world.CreateUnitAsync(other, "Elsewhere", null, "organization");
        Assert.Contains("validation.documents.scope", await ErrorKeysAsync(await t["admin"].Client.PostAsJsonAsync(Libraries, new { names = Names("X"), scope = "org_unit", scopeId = foreign })));

        // A tenant library is open to every member, including people in no unit; a site library takes the site's reach.
        var everyone = Id(await LibraryAsync(t["admin"].Client, "Policies", "tenant", null));
        Assert.Contains((await OkAsync(t["out"].Client.GetAsync(Libraries))).EnumerateArray(), l => Id(l) == everyone);
        var site = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/content/sites")))[0].GetProperty("id").GetGuid();
        var onSite = await LibraryAsync(t["admin"].Client, "Intranet files", "site", site);
        Assert.Equal(JsonValueKind.Null, onSite.GetProperty("orgUnitId").ValueKind);
        Assert.Contains("validation.documents.scope", await ErrorKeysAsync(await t["admin"].Client.PostAsJsonAsync(Libraries, new { names = Names("X"), scope = "site", scopeId = Guid.NewGuid() })));
    }

    [Fact]
    public async Task An_upload_missing_required_fields_stays_a_draft_only_its_uploader_sees_until_they_are_filled()
    {
        var o = await FreshAsync();
        var t = o.T;
        var library = Id(await LibraryAsync(t["lm"].Client, "Quality", "org_unit", o.Dubai));
        var sop = await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = Names("SOP"),
            fields = new object[]
            {
                new { key = "process", names = Names("Process"), kind = "choice", required = true,
                    options = new[] { new { value = "quality", names = Names("Quality") }, new { value = "safety", names = Names("Safety") } } },
                new { key = "site", names = Names("Site"), kind = "text", required = true },
                new { key = "reviewed", names = Names("Reviewed"), kind = "boolean", @default = false },
            },
        }), HttpStatusCode.Created);
        var type = Id(sop);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocTypeChanged, type.ToString());

        var draft = await OkAsync(UploadAsync(t["con"].Client, library, "SOP-12.docx", "first draft", type: type, metadata: new { process = "quality" }), HttpStatusCode.Created);
        var id = Id(draft);
        Assert.False(draft.GetProperty("checkedIn").GetBoolean());
        Assert.False(draft.GetProperty("complete").GetBoolean());
        Assert.Equal(t["con"].Id, draft.GetProperty("checkedOutBy").GetGuid());
        Assert.Equal("pending", draft.GetProperty("pending").GetProperty("state").GetString());
        Assert.False(draft.GetProperty("metadata").GetProperty("reviewed").GetBoolean());
        // Nobody else sees it, not even the library manager.
        foreach (var name in new[] { "lm", "dee", "admin" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await t[name].Client.GetAsync($"/api/v1/documents/{id}")).StatusCode);
            Assert.DoesNotContain(id, await ItemIdsAsync(t[name].Client, library));
        }
        Assert.Contains(id, await ItemIdsAsync(t["con"].Client, library));

        // Values are checked against the type: an unknown field and a value off the term set are refused.
        var bad = await t["con"].Client.PutAsJsonAsync($"/api/v1/documents/{id}/metadata", new { version = 1, metadata = new { process = "finance", colour = "red" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("validation.documents.field_value", await ErrorKeysAsync(bad));
        Assert.Contains("validation.documents.unknown_field", await ErrorKeysAsync(bad));

        var filled = await OkAsync(t["con"].Client.PutAsJsonAsync($"/api/v1/documents/{id}/metadata",
            new { version = 1, metadata = new { process = "quality", site = "Jebel Ali" }, publish = true }));
        Assert.True(filled.GetProperty("checkedIn").GetBoolean());
        Assert.Equal("1.0", filled.GetProperty("effective").GetProperty("label").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocMetadataChanged, id.ToString());
        Assert.Contains(id, await ItemIdsAsync(t["dee"].Client, library));

        // A complete upload that is not published is a 0.1 draft: contributors and managers see it, readers do not.
        var minor = await OkAsync(UploadAsync(t["con"].Client, library, "SOP-13.docx", "second", type: type, metadata: new { process = "safety", site = "Dubai" }, publish: false),
            HttpStatusCode.Created);
        Assert.Equal("0.1", minor.GetProperty("current").GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, minor.GetProperty("effective").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await t["lm"].Client.GetAsync($"/api/v1/documents/{Id(minor)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"/api/v1/documents/{Id(minor)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dee"].Client.PutAsJsonAsync($"/api/v1/documents/{id}/metadata",
            new { version = 2, metadata = new { process = "quality", site = "Elsewhere" } })).StatusCode);
    }

    [Fact]
    public async Task Uploads_outside_the_allowlist_over_the_size_limit_empty_or_duplicate_are_refused_and_infected_files_are_quarantined()
    {
        var o = await FreshAsync();
        var t = o.T;
        Assert.Contains("validation.documents.extensions", await ErrorKeysAsync(await t["lm"].Client.PostAsJsonAsync(Libraries,
            new { names = Names("X"), scope = "org_unit", scopeId = o.Dubai, allowedExtensions = new[] { "exe" } })));
        var library = Id(await LibraryAsync(t["lm"].Client, "Forms", "org_unit", o.Dubai, new { allowedExtensions = new[] { "pdf", "txt" }, maxFileMb = 1 }));
        var con = t["con"].Client;

        Assert.Contains("validation.documents.file_type", await ErrorKeysAsync(await UploadAsync(con, library, "setup.exe", "MZ")));
        Assert.Contains("validation.documents.file_type", await ErrorKeysAsync(await UploadAsync(con, library, "letter.docx", "allowed by the tenant, not here")));
        Assert.Contains("validation.documents.file_type", await ErrorKeysAsync(await UploadAsync(con, library, "noextension", "x")));
        Assert.Contains("validation.documents.file_size", await ErrorKeysAsync(await UploadAsync(con, library, "big.txt", new string('a', 1024 * 1024 + 1))));
        Assert.Contains("validation.documents.file_empty", await ErrorKeysAsync(await UploadAsync(con, library, "empty.txt", "")));
        await PublishedAsync(con, library, "form.txt", "the form");
        var duplicate = await UploadAsync(con, library, "copy.txt", "the form");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("conflict.documents.duplicate", await ErrorKeysAsync(duplicate));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document where name = 'copy.txt'"));

        // The tenant list bounds every library: narrowing it to PDF stops text files here too.
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/documents/settings", new { allowedExtensions = new[] { "pdf" }, maxFileMb = 100 }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocSettingsChanged);
        Assert.Contains("validation.documents.file_type", await ErrorKeysAsync(await UploadAsync(con, library, "later.txt", "later")));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["lm"].Client.PutAsJsonAsync("/api/v1/documents/settings", new { allowedExtensions = new[] { "pdf" } })).StatusCode);

        // An infected file is kept in quarantine: never downloadable, shown only to the uploader, and people are told.
        var infected = await OkAsync(UploadAsync(con, library, "invoice.pdf", TestSignatureScanner.Eicar), HttpStatusCode.Created);
        Assert.Equal("infected", infected.GetProperty("pending").GetProperty("scanState").GetString());
        Assert.False(infected.GetProperty("checkedIn").GetBoolean());
        var refused = await con.GetAsync($"/api/v1/documents/{Id(infected)}/download-token");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("conflict.documents.not_clean", await ErrorKeysAsync(refused));
        // Filling metadata never checks in an infected file.
        var still = await OkAsync(con.PutAsJsonAsync($"/api/v1/documents/{Id(infected)}/metadata", new { version = 1, publish = true }));
        Assert.False(still.GetProperty("checkedIn").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["lm"].Client.GetAsync($"/api/v1/documents/{Id(infected)}")).StatusCode);
        foreach (var name in new[] { "con", "admin" })
        {
            Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
                "select count(*) from notifications.notification where user_id = @u and template_key = 'documents.quarantined'", ("u", t[name].Id)));
        }
    }

    [Fact]
    public async Task Readers_filter_and_sort_by_metadata_and_use_saved_views_that_only_managers_create()
    {
        var o = await FreshAsync();
        var t = o.T;
        var lm = t["lm"].Client;
        var library = Id(await LibraryAsync(lm, "Quality", "org_unit", o.Dubai));
        var type = Id(await OkAsync(lm.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = Names("Procedure"),
            fields = new object[]
            {
                new { key = "process", names = Names("Process"), kind = "choice", options = new[] { new { value = "quality", names = Names("Quality") }, new { value = "hr", names = Names("HR") } } },
                new { key = "site", names = Names("Site"), kind = "text" },
                new { key = "pages", names = Names("Pages"), kind = "number" },
                new { key = "issued", names = Names("Issued"), kind = "date" },
            },
        }), HttpStatusCode.Created));
        var folder = Id(await OkAsync(lm.PostAsJsonAsync($"{Libraries}/{library}/folders", new { name = "Plants" }), HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.Conflict, (await lm.PostAsJsonAsync($"{Libraries}/{library}/folders", new { name = "plants" })).StatusCode);
        var con = t["con"].Client;
        var a = await PublishedAsync(con, library, "Audit.pdf", "a", type: type, metadata: new { process = "quality", site = "Dubai plant", pages = 12, issued = "2026-01-10" });
        var b = await PublishedAsync(con, library, "Boilers.pdf", "b", type: type, metadata: new { process = "quality", site = "Cairo plant", pages = 3, issued = "2026-05-02" });
        var c = await PublishedAsync(con, library, "Contracts.pdf", "c", type: type, metadata: new { process = "hr", site = "Head office", pages = 40, issued = "2025-11-30" });
        var d = await PublishedAsync(con, library, "Drills.pdf", "d", folder, type, new { process = "quality", site = "Dubai plant", pages = 7, issued = "2026-03-15" });

        var dee = t["dee"].Client;
        Assert.Equal([a, b, c], await ItemIdsAsync(dee, library));
        Assert.Equal([d], await ItemIdsAsync(dee, library, $"?folder={folder}"));
        Assert.Equal([a, b, d], await ItemIdsAsync(dee, library, "?recursive=true&f.process=quality"));
        Assert.Equal([a, d], await ItemIdsAsync(dee, library, "?recursive=true&f.site=dubai"));
        Assert.Equal([c, a], await ItemIdsAsync(dee, library, "?recursive=true&f.pages.from=10&sort=pages&desc=true"));
        Assert.Equal([d, b], await ItemIdsAsync(dee, library, "?recursive=true&f.issued.from=2026-02-01&f.issued.to=2026-12-31&sort=issued"));
        Assert.Equal([b, d, a, c], await ItemIdsAsync(dee, library, "?recursive=true&sort=issued&desc=true"));
        Assert.Equal([b], await ItemIdsAsync(dee, library, "?q=boil"));
        Assert.Contains("validation.documents.unknown_field", await ErrorKeysAsync(await dee.GetAsync($"{Libraries}/{library}/items?f.colour=red")));
        Assert.Contains("validation.documents.field_value", await ErrorKeysAsync(await dee.GetAsync($"{Libraries}/{library}/items?f.pages=many")));

        var view = new { names = Names("Quality by size"), query = new { recursive = true, filters = new Dictionary<string, string> { ["process"] = "quality" }, sort = "pages", descending = true } };
        Assert.Equal(HttpStatusCode.Forbidden, (await dee.PostAsJsonAsync($"{Libraries}/{library}/views", view)).StatusCode);
        var saved = Id(await OkAsync(lm.PostAsJsonAsync($"{Libraries}/{library}/views", view), HttpStatusCode.Created));
        Assert.Contains((await OkAsync(dee.GetAsync($"{Libraries}/{library}/views"))).EnumerateArray(), v => Id(v) == saved);
        Assert.Equal([a, d, b], await ItemIdsAsync(dee, library, $"?view={saved}"));
        // A reader's own filter narrows the saved view.
        Assert.Equal([a, d], await ItemIdsAsync(dee, library, $"?view={saved}&f.site=dubai"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.GetAsync($"{Libraries}/{library}/views")).StatusCode);
    }

    [Fact]
    public async Task A_permission_break_on_a_folder_or_document_starts_from_a_copy_and_can_then_hide_it_from_library_readers()
    {
        var o = await FreshAsync();
        var t = o.T;
        var lm = t["lm"].Client;
        var library = Id(await LibraryAsync(lm, "Quality", "org_unit", o.Dubai));
        var folder = Id(await OkAsync(lm.PostAsJsonAsync($"{Libraries}/{library}/folders", new { name = "Investigations" }), HttpStatusCode.Created));
        var inner = Id(await OkAsync(lm.PostAsJsonAsync($"{Libraries}/{library}/folders", new { name = "2026", parentId = folder }), HttpStatusCode.Created));
        var report = await PublishedAsync(t["con"].Client, library, "Incident.pdf", "incident", inner);
        var open = await PublishedAsync(t["con"].Client, library, "Open.pdf", "open");
        var dee = t["dee"].Client;
        Assert.Equal([report], await ItemIdsAsync(dee, library, $"?folder={inner}"));

        // An inheriting folder has no ACL of its own to edit, and readers cannot break.
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"/api/v1/authz/resources/doc_folder/{folder}/acl")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dee.PostAsJsonAsync($"/api/v1/documents/folders/{folder}/permissions/break", new { })).StatusCode);

        var broken = await OkAsync(lm.PostAsJsonAsync($"/api/v1/documents/folders/{folder}/permissions/break", new { }));
        Assert.False(broken.GetProperty("inherits").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocPermissionsBroken, folder.ToString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DocumentPermissionsChanged", t.Id));
        // The copy keeps today's access: the unit's members still read.
        Assert.Equal([report], await ItemIdsAsync(dee, library, $"?folder={inner}"));
        var acl = await OkAsync(lm.GetAsync($"/api/v1/authz/resources/doc_folder/{folder}/acl"));
        Assert.Contains(acl.EnumerateArray(), e => e.GetProperty("subjectType").GetString() == "org_unit" && e.GetProperty("subjectId").GetGuid() == o.Dubai);

        // The manager narrows the folder to the contributor: readers of the library lose it, the rest of the library stays.
        (await lm.PutAsJsonAsync($"/api/v1/authz/resources/doc_folder/{folder}/acl",
            new[] { new { subjectType = "user", subjectId = t["con"].Id, permission = Permissions.DocContribute } })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await dee.GetAsync($"/api/v1/documents/{report}")).StatusCode);
        Assert.DoesNotContain((await OkAsync(dee.GetAsync($"{Libraries}/{library}/folders"))).EnumerateArray(), f => Id(f) == folder || Id(f) == inner);
        Assert.Empty(await ItemIdsAsync(dee, library, "?recursive=true&q=incident"));
        Assert.Equal([open], await ItemIdsAsync(dee, library));
        Assert.Equal("incident", await DownloadAsync(t["con"].Client, report));
        // The manager keeps managing through their role at the unit.
        Assert.Equal(HttpStatusCode.OK, (await lm.GetAsync($"/api/v1/documents/{report}")).StatusCode);

        // Inheriting again removes the folder's ACL and readers are back.
        var restored = await OkAsync(lm.PostAsJsonAsync($"/api/v1/documents/folders/{folder}/permissions/inherit", new { }));
        Assert.True(restored.GetProperty("inherits").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocPermissionsChanged, folder.ToString());
        Assert.Equal([report], await ItemIdsAsync(dee, library, $"?folder={inner}"));
        Assert.Equal(HttpStatusCode.NotFound, (await lm.GetAsync($"/api/v1/authz/resources/doc_folder/{folder}/acl")).StatusCode);

        // The same works for one document.
        await OkAsync(lm.PostAsync($"/api/v1/documents/{open}/permissions/break", null));
        (await lm.PutAsJsonAsync($"/api/v1/authz/resources/doc_document/{open}/acl",
            new[] { new { subjectType = "user", subjectId = t["con"].Id, permission = Permissions.DocView } })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await dee.GetAsync($"/api/v1/documents/{open}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["con"].Client.GetAsync($"/api/v1/documents/{open}")).StatusCode);
        await OkAsync(lm.PostAsync($"/api/v1/documents/{open}/permissions/inherit", null));
        Assert.Equal(HttpStatusCode.OK, (await dee.GetAsync($"/api/v1/documents/{open}")).StatusCode);
    }

    [Fact]
    public async Task Without_the_document_capability_everything_is_refused_and_the_storage_limit_refuses_uploads_with_its_own_code()
    {
        async Task<TestTenant> OnVersionAsync(Dictionary<string, object> extra)
        {
            var tenant = await world.CreateTenantAsync("dl-p-" + Guid.NewGuid().ToString("N")[..6], "workplace");
            var capabilities = new Dictionary<string, object>
            {
                [Capabilities.AiAssistant] = new { enabled = true },
                [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
                [Capabilities.OrgHierarchy] = new { enabled = true },
            };
            foreach (var (k, v) in extra)
            {
                capabilities[k] = v;
            }
            await world.PinPackageVersionAsync(tenant, "workplace", capabilities);
            return tenant;
        }

        var off = await OnVersionAsync([]);
        foreach (var call in new[] { off["admin"].Client.GetAsync(Libraries), off["admin"].Client.PostAsJsonAsync(Libraries, new { names = Names("X"), scope = "tenant" }) })
        {
            var response = await call;
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("entitlement.not_in_package", await ErrorKeysAsync(response));
        }

        var full = await OnVersionAsync(new() { [Capabilities.DocsCore] = new { enabled = true }, [Capabilities.DocsStorageGb] = new { enabled = true, limit = 0 } });
        var library = Id(await LibraryAsync(full["admin"].Client, "Files", "tenant", null));
        var refused = await UploadAsync(full["admin"].Client, library, "a.pdf", "content");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("entitlement.quota_exhausted", body.GetProperty("code").GetString());
        Assert.Equal(Capabilities.DocsStorageGb, body.GetProperty("capability").GetString());
        Assert.Equal(0L, await world.Database.CountInTenantAsync(full.Id, "select count(*) from documents.document_version"));
    }

    [Fact]
    public async Task Library_and_type_changes_need_the_current_version_keep_the_scope_and_are_audited()
    {
        var o = await FreshAsync();
        var t = o.T;
        var lm = t["lm"].Client;
        var created = await LibraryAsync(lm, "Quality", "org_unit", o.Dubai);
        var library = Id(created);
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.True(created.GetProperty("rights").GetProperty("manage").GetBoolean());

        var changed = await OkAsync(lm.PutAsJsonAsync($"{Libraries}/{library}", new
        {
            names = Names("Quality and safety"), scope = "org_unit", scopeId = o.Dubai, allowedExtensions = new[] { "pdf" }, classificationDefault = "confidential", version = 1,
        }));
        Assert.Equal(2, changed.GetProperty("version").GetInt32());
        Assert.Equal(["pdf"], changed.GetProperty("effectiveExtensions").EnumerateArray().Select(e => e.GetString()!).ToArray());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocLibraryChanged, library.ToString());

        var stale = await lm.PutAsJsonAsync($"{Libraries}/{library}", new { names = Names("Old"), version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("conflict.documents.stale", await ErrorKeysAsync(stale));
        Assert.Contains("validation.documents.scope_fixed", await ErrorKeysAsync(await lm.PutAsJsonAsync($"{Libraries}/{library}",
            new { names = Names("Moved"), scope = "org_unit", scopeId = o.Cairo, version = 2 })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dee"].Client.PutAsJsonAsync($"{Libraries}/{library}", new { names = Names("Mine"), version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["cm"].Client.PutAsJsonAsync($"{Libraries}/{library}", new { names = Names("Mine"), version = 2 })).StatusCode);

        // A retired type takes no new uploads; its fields stay on the documents that have them.
        var type = await OkAsync(lm.PostAsJsonAsync($"{Libraries}/{library}/types", new { names = Names("Form"), fields = Array.Empty<object>() }), HttpStatusCode.Created);
        Assert.Contains("validation.documents.field_key", await ErrorKeysAsync(await lm.PostAsJsonAsync($"{Libraries}/{library}/types",
            new { names = Names("Bad"), fields = new[] { new { key = "Bad Key", names = Names("Bad"), kind = "text" } } })));
        var retired = await OkAsync(lm.PutAsJsonAsync($"/api/v1/documents/types/{Id(type)}", new { names = Names("Form"), fields = Array.Empty<object>(), status = "retired", version = 1 }));
        Assert.Equal("retired", retired.GetProperty("status").GetString());
        Assert.Contains("validation.documents.type_unknown", await ErrorKeysAsync(await UploadAsync(t["con"].Client, library, "form.pdf", "form", type: Id(type))));
    }
}
