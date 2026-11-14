using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents;
using TeamBeats.Persistence;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R2 US9 (FR-025 to FR-027, FR-030, FR-031): SharePoint Online as connected libraries. The integration
/// administrator allows sites the customer granted to the application; a library manager connects a document library
/// at a scope they manage; people in its audience see only what SharePoint lets them open, listed and opened live with
/// their own access. TeamBeats-only actions are refused, an outage changes nothing for TeamBeats libraries, and other
/// tenants, other units and plans without Microsoft 365 are covered. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SharePointLibrariesTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";

    private FakeMicrosoftGraph Graph => world.Factory.Graph;

    private sealed record Org(TestTenant T, Guid Dubai, Guid Connection, Guid Directory, string HrSite, string FinanceSite, string Drive, string Policies,
        string Handbook, string Salaries);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sp-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var (name, unit) in new[] { ("lm", "Dubai"), ("reader", "Dubai"), ("outsider", "Cairo") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        if (package == "workplace_plus")
        {
            await world.AssignAsync(t, "lm", BuiltInRoles.OrganizationAdministrator, "org_subtree", dubai);
        }
        var directory = Guid.NewGuid();
        var connection = Guid.Empty;
        if (package == "workplace_plus")
        {
            var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
            {
                kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "sharepoint_documents" },
            }), HttpStatusCode.Created);
            connection = created.GetProperty("id").GetGuid();
            await AdminConsentAsync(t, connection, directory, "sharepoint_documents");
            Graph.Consent(directory, ["Sites.Selected"]);
            Graph.ConsentDelegated(directory, "User.Read", "Files.Read.All");
        }
        var hr = Graph.AddSharePointSite(directory, "contoso.sharepoint.com", "/sites/hr", "Human Resources");
        var finance = Graph.AddSharePointSite(directory, "contoso.sharepoint.com", "/sites/finance", "Finance", granted: false);
        var drive = Graph.AddSharePointDrive(hr, "Documents");
        var policies = Graph.AddSharePointItem(drive, null, "Policies", folder: true);
        var handbook = Graph.AddSharePointItem(drive, policies, "Handbook.pdf", content: Encoding.UTF8.GetBytes("%PDF-1.7 Employee handbook"));
        var salaries = Graph.AddSharePointItem(drive, null, "Salaries.xlsx", content: Encoding.UTF8.GetBytes("salary bands"), readers: t["lm"].Email);
        return new Org(t, dubai, connection, directory, hr, finance, drive, policies, handbook, salaries);
    }

    private string Sites(Org o) => $"/api/v1/integrations/connections/{o.Connection}/sharepoint/sites";

    private async Task<Guid> ConnectedLibraryAsync(Org o)
    {
        Assert.Equal(HttpStatusCode.Created, (await o.T["admin"].Client.PostAsJsonAsync(Sites(o), new { url = "https://contoso.sharepoint.com/sites/hr" })).StatusCode);
        var library = await OkAsync(o.T["lm"].Client.PostAsJsonAsync($"{Libraries}/connected", new
        {
            names = new Dictionary<string, string> { ["en"] = "HR policies" }, scope = "org_unit", scopeId = o.Dubai, connectionId = o.Connection, containerId = o.Drive,
        }), HttpStatusCode.Created);
        return library.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ItemsAsync(HttpClient client, Guid library, string? folder = null) =>
        await OkAsync(client.GetAsync($"{Libraries}/{library}/connected/items" + (folder is null ? string.Empty : $"?folder={Uri.EscapeDataString(folder)}")));

    private static string[] Names(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task Allowed_sites_connect_a_library_whose_files_each_person_sees_as_SharePoint_lets_them()
    {
        var o = await FreshAsync();
        var t = o.T;

        // Allowed sites: only SharePoint Online addresses, only sites granted to the application, and only the integration administrator.
        foreach (var url in new[]
                 {
                     "https://example.com/sites/hr", "http://contoso.sharepoint.com/sites/hr", "https://user@contoso.sharepoint.com/sites/hr",
                     "https://contoso.sharepoint.com:8443/sites/hr", "https://contoso.sharepoint.com/personal/someone", "https://contoso.sharepoint.com/sites/h%2Fr",
                 })
        {
            Assert.Equal("validation.integrations.sharepoint_url", await RefusedAsync(t["admin"].Client.PostAsJsonAsync(Sites(o), new { url }), HttpStatusCode.BadRequest));
        }
        Assert.Equal("validation.integrations.sharepoint_site_not_granted",
            await RefusedAsync(t["admin"].Client.PostAsJsonAsync(Sites(o), new { url = "https://contoso.sharepoint.com/sites/finance" }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["lm"].Client.PostAsJsonAsync(Sites(o), new { url = "https://contoso.sharepoint.com/sites/hr" })).StatusCode);
        var site = await OkAsync(t["admin"].Client.PostAsJsonAsync(Sites(o), new { url = "https://contoso.sharepoint.com/sites/hr/SitePages/Home.aspx" }),
            HttpStatusCode.Created);
        Assert.Equal(o.HrSite, site.GetProperty("siteId").GetString());
        Assert.Equal("Human Resources", Assert.Single((await OkAsync(t["admin"].Client.GetAsync(Sites(o)))).EnumerateArray()).GetProperty("name").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionSiteAllowed, o.Connection.ToString());

        // A manager sees the document libraries of allowed sites; a reader may not list them.
        var sources = await OkAsync(t["lm"].Client.GetAsync("/api/v1/documents/connected-sources"));
        var container = Assert.Single(Assert.Single(sources.EnumerateArray()).GetProperty("containers").EnumerateArray());
        Assert.Equal(o.Drive, container.GetProperty("id").GetString());
        Assert.Equal("Human Resources / Documents", container.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.GetAsync("/api/v1/documents/connected-sources")).StatusCode);

        // A document library the connection does not allow is refused; a reader cannot connect one.
        Assert.Equal("validation.documents.connected_container", await RefusedAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/connected", new
        {
            names = new Dictionary<string, string> { ["en"] = "Other" }, scope = "org_unit", scopeId = o.Dubai, connectionId = o.Connection, containerId = "b!other",
        }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.PostAsJsonAsync($"{Libraries}/connected", new
        {
            names = new Dictionary<string, string> { ["en"] = "Mine" }, scope = "org_unit", scopeId = o.Dubai, connectionId = o.Connection, containerId = o.Drive,
        })).StatusCode);
        var created = await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/connected", new
        {
            names = new Dictionary<string, string> { ["en"] = "HR policies" }, scope = "org_unit", scopeId = o.Dubai, connectionId = o.Connection, containerId = o.Drive,
        }), HttpStatusCode.Created);
        var library = created.GetProperty("id").GetGuid();
        Assert.Equal("Human Resources / Documents", created.GetProperty("connected").GetProperty("containerName").GetString());
        Assert.Contains((await OkAsync(t["reader"].Client.GetAsync(Libraries))).EnumerateArray(),
            l => l.GetProperty("id").GetGuid() == library && l.GetProperty("connected").GetBoolean());

        // Before connecting their own account, a person is asked to; afterwards they see only what SharePoint lets them open.
        Assert.Equal("not_connected", (await ItemsAsync(t["reader"].Client, library)).GetProperty("state").GetString());
        await ConnectAsync(t, "reader");
        await ConnectAsync(t, "lm");
        var readerRoot = await ItemsAsync(t["reader"].Client, library);
        Assert.Equal("ok", readerRoot.GetProperty("state").GetString());
        Assert.Equal(["Policies"], Names(readerRoot));
        Assert.Equal(["Policies", "Salaries.xlsx"], Names(await ItemsAsync(t["lm"].Client, library)));
        var folder = readerRoot.GetProperty("items")[0];
        Assert.True(folder.GetProperty("folder").GetBoolean());
        var inside = await ItemsAsync(t["reader"].Client, library, folder.GetProperty("id").GetString());
        Assert.Equal(["Handbook.pdf"], Names(inside));

        // Opening reads live as the person; a file SharePoint keeps from them is not found, and nothing is kept.
        var handbookId = inside.GetProperty("items")[0].GetProperty("id").GetString()!;
        var download = await t["reader"].Client.GetAsync($"{Libraries}/{library}/connected/content?item={Uri.EscapeDataString(handbookId)}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("%PDF-1.7 Employee handbook", await download.Content.ReadAsStringAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocConnectedOpened, library.ToString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["reader"].Client.GetAsync($"{Libraries}/{library}/connected/content?item={Uri.EscapeDataString($"{o.Drive}~{o.Salaries}")}")).StatusCode);
        Graph.SetSharePointReaders(o.Handbook, t["lm"].Email);
        Assert.Equal(HttpStatusCode.NotFound, (await t["reader"].Client.GetAsync($"{Libraries}/{library}/connected/content?item={Uri.EscapeDataString(handbookId)}")).StatusCode);
        Assert.Empty(Names(await ItemsAsync(t["reader"].Client, library, folder.GetProperty("id").GetString())));

        // The TeamBeats audience still applies: another unit's member and another tenant do not find the library.
        Assert.Equal(HttpStatusCode.NotFound, (await t["outsider"].Client.GetAsync($"{Libraries}/{library}/connected/items")).StatusCode);
        var other = await world.CreateTenantAsync("sp-other-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Libraries}/{library}/connected/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(Sites(o))).StatusCode);

        // TeamBeats-only actions are refused with the reason (FR-030).
        Assert.Equal("conflict.documents.connected_library", await RefusedAsync(t["lm"].Client.PostAsync($"{Libraries}/{library}/files?name=a.txt",
            new ByteArrayContent(Encoding.UTF8.GetBytes("text"))), HttpStatusCode.Conflict));
        Assert.Equal("conflict.documents.connected_library", await RefusedAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/{library}/folders", new { name = "New" }),
            HttpStatusCode.Conflict));
        Assert.Equal("validation.documents.connected_library", await RefusedAsync(t["lm"].Client.PutAsJsonAsync($"{Libraries}/{library}", new
        {
            names = new Dictionary<string, string> { ["en"] = "HR policies" }, viewOnly = true, version = created.GetProperty("version").GetInt32(),
        }), HttpStatusCode.BadRequest));

        // A site the customer stops granting fails the connection test; a removed site closes its libraries.
        Graph.GrantSharePointSite(o.HrSite, false);
        var test = await OkAsync(t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{o.Connection}:test", null));
        var sharepoint = test.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("feature").GetString() == "sharepoint_documents");
        Assert.Equal("integrations.test.site_missing", sharepoint.GetProperty("resultKey").GetString());
        Graph.GrantSharePointSite(o.HrSite, true);
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"{Sites(o)}/{Uri.EscapeDataString(o.HrSite)}")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ConnectionSiteRemoved, o.Connection.ToString());
        Assert.Equal("not_found", (await ItemsAsync(t["reader"].Client, library)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task An_outage_a_missing_consent_and_a_plan_without_Microsoft_365_are_handled()
    {
        var o = await FreshAsync();
        var library = await ConnectedLibraryAsync(o);
        await ConnectAsync(o.T, "reader");

        // SharePoint down: the library says so and TeamBeats libraries keep working.
        Graph.DelegatedFaults.Enqueue(503);
        Graph.DelegatedFaults.Enqueue(503);
        Graph.DelegatedFaults.Enqueue(503);
        Assert.Equal("unavailable", (await ItemsAsync(o.T["reader"].Client, library)).GetProperty("state").GetString());
        Graph.DelegatedFaults.Clear();
        var own = await OkAsync(o.T["lm"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Quality" }, scope = "org_unit", scopeId = o.Dubai,
        }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.OK, (await o.T["reader"].Client.GetAsync($"{Libraries}/{own.GetProperty("id").GetGuid()}/items")).StatusCode);
        Assert.Equal("ok", (await ItemsAsync(o.T["reader"].Client, library)).GetProperty("state").GetString());

        // Sites need the feature turned on and consented.
        Assert.Equal(HttpStatusCode.NoContent, (await o.T["admin"].Client.PostAsync($"/api/v1/integrations/connections/{o.Connection}/features/sharepoint_documents:disable", null)).StatusCode);
        Assert.Equal("conflict.integrations.sharepoint_consent", await RefusedAsync(o.T["admin"].Client.PostAsJsonAsync(Sites(o), new
        {
            url = "https://contoso.sharepoint.com/sites/hr",
        }), HttpStatusCode.Conflict));
        Assert.Equal("unavailable", (await ItemsAsync(o.T["reader"].Client, library)).GetProperty("state").GetString());
        Assert.Empty((await OkAsync(o.T["lm"].Client.GetAsync("/api/v1/documents/connected-sources"))).EnumerateArray());

        // A plan without Microsoft 365 offers no sources at all.
        var plain = await FreshAsync("workplace");
        Assert.Empty((await OkAsync(plain.T["admin"].Client.GetAsync("/api/v1/documents/connected-sources"))).EnumerateArray());
    }

    private async Task SyncAsync(Org o, Guid library, bool full = false)
    {
        await world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            await sp.GetRequiredService<ConnectedSync>().HandleAsync(new ConnectedLibrarySyncRequested(library, full), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
            return 0;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static async Task<string[]> FoundAsync(HttpClient client, string q) =>
        (await OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}"))).GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!).ToArray();

    private static async Task<Guid> ResultAsync(HttpClient client, string q) =>
        (await OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}"))).GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("itemId").GetGuid()).First();

    [Fact]
    public async Task Sync_mirrors_readers_for_search_and_keeps_a_last_synced_listing_for_outages()
    {
        var o = await FreshAsync();
        var t = o.T;
        var library = await ConnectedLibraryAsync(o);
        await ConnectAsync(t, "reader");
        await SyncAsync(o, library);

        // Search filters by the mirrored readers and the library's audience; inherited site group access is not mirrored, so it only narrows.
        Assert.Contains("Salaries.xlsx", await FoundAsync(t["lm"].Client, "salaries"));
        Assert.DoesNotContain("Salaries.xlsx", await FoundAsync(t["reader"].Client, "salaries"));
        Assert.DoesNotContain("Handbook.pdf", await FoundAsync(t["reader"].Client, "handbook"));

        // A permission change in SharePoint reaches search with the next sync, and the result leads to SharePoint.
        Graph.SetSharePointReaders(o.Handbook, t["reader"].Email);
        await SyncAsync(o, library);
        Assert.Contains("Handbook.pdf", await FoundAsync(t["reader"].Client, "handbook"));
        var handbook = await ResultAsync(t["reader"].Client, "handbook");
        var link = await OkAsync(t["reader"].Client.GetAsync($"{Libraries}/{library}/connected/links/{handbook}"));
        Assert.StartsWith("https://fake-sharepoint.example/", link.GetProperty("webLink").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["outsider"].Client.GetAsync($"{Libraries}/{library}/connected/links/{handbook}")).StatusCode);
        Assert.DoesNotContain("Handbook.pdf", await FoundAsync(t["outsider"].Client, "handbook"));
        var other = await world.CreateTenantAsync("sp-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Empty(await FoundAsync(other["admin"].Client, "handbook"));

        // SharePoint down: the library lists the last synced copy, filtered by the mirrored readers, and refuses to open files.
        for (var i = 0; i < 3; i++)
        {
            Graph.DelegatedFaults.Enqueue(503);
        }
        var degraded = await ItemsAsync(t["reader"].Client, library);
        Assert.Equal("degraded", degraded.GetProperty("state").GetString());
        Assert.Equal(["Policies"], Names(degraded));
        Assert.NotEqual(JsonValueKind.Null, degraded.GetProperty("syncedAt").ValueKind);
        Graph.DelegatedFaults.Clear();

        // A failing sync marks the library degraded and tells the integration administrators once.
        for (var i = 0; i < 3; i++)
        {
            Graph.DelegatedFaults.Enqueue(503);
        }
        await SyncAsync(o, library);
        Graph.DelegatedFaults.Clear();
        var since = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<bool>(
            "select degraded_since is not null from documents.connected_sync where library_id = @library", new { library }, CancellationToken.None));
        Assert.True(since);
        var inbox = await OkAsync(t["admin"].Client.GetAsync("/api/v1/me/notifications"));
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), n => n.GetProperty("link").GetString() == $"/documents/libraries/{library}");

        // Deleted in SharePoint: gone from search after the next sync, which also clears the degraded mark.
        Graph.DeleteSharePointItem(o.Handbook);
        await SyncAsync(o, library);
        Assert.DoesNotContain("Handbook.pdf", await FoundAsync(t["reader"].Client, "handbook"));
        Assert.False(await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().ScalarAsync<bool>(
            "select degraded_since is not null from documents.connected_sync where library_id = @library", new { library }, CancellationToken.None)));
    }

    [Fact]
    public async Task Uploads_run_as_the_person_labels_map_to_classifications_and_removing_the_site_retires_the_library()
    {
        var o = await FreshAsync();
        var t = o.T;
        // Uploads are their own feature, so a tenant that only reads never grants write access.
        Assert.True((await t["admin"].Client.PostAsync($"/api/v1/integrations/connections/{o.Connection}/features/sharepoint_upload:enable", null)).IsSuccessStatusCode);
        await AdminConsentAsync(t, o.Connection, o.Directory, "sharepoint_upload");
        Graph.ConsentDelegated(o.Directory, "User.Read", "Files.Read.All", "Files.ReadWrite.All");
        var library = await ConnectedLibraryAsync(o);
        await ConnectAsync(t, "lm");
        await ConnectAsync(t, "reader");
        var files = $"{Libraries}/{library}/connected/files";
        static ByteArrayContent Body(string text) => new(Encoding.UTF8.GetBytes(text));

        // Off until a manager turns uploads on; only managers may.
        Assert.Equal("conflict.documents.connected_read_only", await RefusedAsync(t["lm"].Client.PostAsync($"{files}?name=Plan.txt", Body("plan")), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t["reader"].Client.PutAsJsonAsync($"{Libraries}/{library}/connected/settings", new { uploadEnabled = true })).StatusCode);
        var settings = await OkAsync(t["lm"].Client.PutAsJsonAsync($"{Libraries}/{library}/connected/settings", new { uploadEnabled = true }));
        Assert.True(settings.GetProperty("connected").GetProperty("uploadEnabled").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocConnectedSettings, library.ToString());

        // A contributor uploads as themselves; the same name again is a new version of the same file.
        var uploaded = await OkAsync(t["lm"].Client.PostAsync($"{files}?name=Plan.txt", Body("plan v1")));
        Assert.Equal("ok", uploaded.GetProperty("state").GetString());
        var plan = uploaded.GetProperty("item").GetProperty("id").GetString()!;
        Assert.StartsWith(o.Drive + "~", plan);
        var again = await OkAsync(t["lm"].Client.PostAsync($"{files}?name=Plan.txt", Body("plan v2")));
        Assert.Equal(plan, again.GetProperty("item").GetProperty("id").GetString());
        Assert.Equal("plan v2", Encoding.UTF8.GetString(Assert.Single(Graph.SharePointItems(o.Drive), i => i.Name == "Plan.txt").Content));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocConnectedUploaded, library.ToString());

        // SharePoint decides too: a folder only some may read is not theirs to write in.
        var board = Graph.AddSharePointItem(o.Drive, null, "Board", folder: true, readers: t["lm"].Email);
        Assert.Equal("forbidden", (await OkAsync(t["lm"].Client.PostAsync($"{files}?name=Minutes.txt&folder={Uri.EscapeDataString($"{o.Drive}~{board}")}",
            Body("minutes")))).GetProperty("state").GetString());
        // A reader without contribute rights in TeamBeats, and anyone outside the audience, are refused before SharePoint is asked.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.PostAsync($"{files}?name=Mine.txt", Body("mine"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["outsider"].Client.PostAsync($"{files}?name=Mine.txt", Body("mine"))).StatusCode);
        Assert.Equal("validation.documents.file_empty", await RefusedAsync(t["lm"].Client.PostAsync($"{files}?name=Empty.txt", Body(string.Empty)),
            HttpStatusCode.BadRequest));

        // Renames run as the person too; a taken name is a conflict.
        var rename = $"{Libraries}/{library}/connected/rename";
        var renamed = await OkAsync(t["lm"].Client.PostAsJsonAsync(rename, new { item = plan, name = "Plan 2026.txt" }));
        Assert.Equal("Plan 2026.txt", renamed.GetProperty("item").GetProperty("name").GetString());
        Assert.Equal("conflict", (await OkAsync(t["lm"].Client.PostAsJsonAsync(rename, new { item = plan, name = "Policies" }))).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.PostAsJsonAsync(rename, new { item = plan, name = "Mine.txt" })).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocConnectedRenamed, library.ToString());

        // Purview labels: unlabelled files take the library default, an unmapped label counts as confidential, and a mapping applies on the next index.
        var label = Guid.NewGuid().ToString("D");
        Graph.SetSharePointLabel(o.Salaries, label, "Highly Confidential");
        Graph.SetSharePointReaders(plan.Split('~')[1], t["lm"].Email);
        await SyncAsync(o, library, full: true);
        Assert.Equal("internal", (await SearchAsync(t["lm"].Client, "plan")).GetProperty("classification").GetString());
        Assert.Equal("confidential", await IndexedAsync(t, "Salaries.xlsx"));
        var labels = $"/api/v1/documents/sensitivity-labels/{o.Connection}";
        var seen = Assert.Single((await OkAsync(t["admin"].Client.GetAsync(labels))).EnumerateArray());
        Assert.Equal("Highly Confidential", seen.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("classification").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["lm"].Client.GetAsync(labels)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["lm"].Client.PutAsJsonAsync($"{labels}/{label}", new { classification = "public" })).StatusCode);
        Assert.Equal("validation.documents.classification",
            await RefusedAsync(t["admin"].Client.PutAsJsonAsync($"{labels}/{label}", new { classification = "secret" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.sensitivity_label",
            await RefusedAsync(t["admin"].Client.PutAsJsonAsync($"{labels}/not-a-label", new { classification = "public" }), HttpStatusCode.BadRequest));
        var mapped = await OkAsync(t["admin"].Client.PutAsJsonAsync($"{labels}/{label}", new { classification = "restricted" }));
        Assert.Equal("restricted", mapped.GetProperty("classification").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocLabelMapped, o.Connection.ToString());
        await DispatchAsync();
        Assert.Equal("restricted", await IndexedAsync(t, "Salaries.xlsx"));
        // Another tenant sees none of this tenant's labels.
        var other = await world.CreateTenantAsync("sp-l-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(labels)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PutAsJsonAsync($"{labels}/{label}", new { classification = "public" })).StatusCode);

        // Removing the site retires the library: archived, its synced copy deleted and its files gone from search.
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"{Sites(o)}/{Uri.EscapeDataString(o.HrSite)}")).StatusCode);
        await DispatchAsync();
        Assert.Empty(await FoundAsync(t["lm"].Client, "plan"));
        Assert.Null(await IndexedAsync(t, "Salaries.xlsx"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from documents.connected_item where library_id = @l", ("l", library)));
        Assert.Equal("archived", await world.Database.TenantScalarAsync<string>(t.Id, "select status from documents.library where id = @l", ("l", library)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocConnectedRemoved, library.ToString());
    }

    [Fact]
    public async Task Import_from_SharePoint_brings_versions_columns_and_authors_and_reports_narrower_permissions()
    {
        var o = await FreshAsync();
        var t = o.T;
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(Sites(o), new { url = "https://contoso.sharepoint.com/sites/hr" })).StatusCode);
        var library = (await OkAsync(admin.PostAsJsonAsync(Libraries, new { names = new Dictionary<string, string> { ["en"] = "HR archive" }, scope = "tenant" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var type = (await OkAsync(admin.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "Policy" },
            fields = new object[]
            {
                new { key = "department", names = new Dictionary<string, string> { ["en"] = "Department" }, kind = "text", required = true },
                new { key = "reviewed", names = new Dictionary<string, string> { ["en"] = "Reviewed" }, kind = "boolean" },
            },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // The source: library columns, a handbook with two versions by two people, and salaries SharePoint opens only to the manager.
        Graph.SetSharePointColumns(o.Drive, ("Department", "Department"), ("Reviewed", "Reviewed"));
        var created = new DateTimeOffset(2024, 3, 1, 8, 0, 0, TimeSpan.Zero);
        Graph.SetSharePointDetails(o.Handbook, t["lm"].Email, created, new Dictionary<string, object> { ["Department"] = "HR", ["Reviewed"] = true });
        Graph.EditSharePointItem(o.Handbook, content: Encoding.UTF8.GetBytes("%PDF-1.7 Employee handbook 2025"), editor: t["reader"].Email);
        Graph.SetSharePointDetails(o.Salaries, "nobody@elsewhere.example", created, new Dictionary<string, object> { ["Department"] = "Finance" });

        // Managers see the columns to map; others do not.
        var columns = $"/api/v1/documents/connected-sources/{o.Connection}/columns?container={Uri.EscapeDataString(o.Drive)}";
        var names = (await OkAsync(admin.GetAsync(columns))).EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["Department", "Reviewed"], names);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.GetAsync(columns)).StatusCode);
        var folders = $"/api/v1/documents/connected-sources/{o.Connection}/folders?container={Uri.EscapeDataString(o.Drive)}";
        Assert.Equal(["Policies"], (await OkAsync(admin.GetAsync(folders))).EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.GetAsync(folders)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/v1/documents/connected-sources/{o.Connection}/folders?container=not-a-drive")).StatusCode);

        var start = $"{Libraries}/{library}/imports/sharepoint";
        object Request(object? map = null) => new
        {
            connectionId = o.Connection, containerId = o.Drive, allVersions = true, typeId = type,
            columns = map ?? new Dictionary<string, string> { ["Department"] = "department", ["Reviewed"] = "reviewed" },
        };
        Assert.Equal("validation.documents.import_columns_map",
            await RefusedAsync(admin.PostAsJsonAsync(start, Request(new Dictionary<string, string> { ["Department"] = "nothing" })), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.PostAsJsonAsync(start, Request())).StatusCode);
        var other = await world.CreateTenantAsync("sp-i-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync(start, Request())).StatusCode);

        var import = (await OkAsync(admin.PostAsJsonAsync(start, Request()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await DispatchAsync();
        var view = await OkAsync(admin.GetAsync($"/api/v1/documents/imports/{import}"));
        Assert.Equal("completed", view.GetProperty("status").GetString());
        Assert.Equal("sharepoint", view.GetProperty("source").GetString());
        Assert.Equal(2, view.GetProperty("total").GetInt32());
        Assert.Equal(2, view.GetProperty("imported").GetInt32());
        var rows = (await OkAsync(admin.GetAsync($"/api/v1/documents/imports/{import}/rows"))).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("path").GetString()!);
        var handbook = rows["Policies/Handbook.pdf"];
        Assert.Equal(2, handbook.GetProperty("versions").GetInt32());
        Assert.True(handbook.GetProperty("authorMatched").GetBoolean());
        Assert.False(handbook.GetProperty("permissionsDiffer").GetBoolean());
        var salaries = rows["Salaries.xlsx"];
        Assert.False(salaries.GetProperty("authorMatched").GetBoolean());
        Assert.True(salaries.GetProperty("permissionsDiffer").GetBoolean());
        Assert.Contains("permissions_differ", await admin.GetStringAsync($"/api/v1/documents/imports/{import}/report"));

        // The handbook arrives with both versions, the newest content, its columns as fields and its original dates.
        var doc = handbook.GetProperty("documentId").GetGuid();
        Assert.Equal(2, (await OkAsync(admin.GetAsync($"/api/v1/documents/{doc}/versions"))).GetArrayLength());
        var document = await OkAsync(admin.GetAsync($"/api/v1/documents/{doc}"));
        Assert.Equal("HR", document.GetProperty("metadata").GetProperty("department").GetString());
        Assert.True(document.GetProperty("metadata").GetProperty("reviewed").GetBoolean());
        Assert.Equal(2024, await world.Database.TenantScalarAsync<int>(t.Id, "select extract(year from created_at)::int from documents.document where id = @d", ("d", doc)));
        Assert.True(await world.Database.TenantScalarAsync<bool>(t.Id,
            "select exists (select 1 from documents.folder where library_id = @l and name = 'Policies')", ("l", library)));

        // The source is only read.
        var source = Graph.SharePointItems(o.Drive);
        Assert.Equal("%PDF-1.7 Employee handbook 2025", Encoding.UTF8.GetString(source.Single(i => i.Name == "Handbook.pdf").Content));
        Assert.Equal(3, source.Count);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    /// <summary>The classification a connected file is indexed with, or null when search no longer holds it.</summary>
    private async Task<string?> IndexedAsync(TestTenant t, string title) =>
        await world.Database.TenantScalarAsync<string?>(t.Id, "select min(classification) from search.unit where source = 'connected_documents' and title = @title",
            ("title", title));

    private static async Task<JsonElement> SearchAsync(HttpClient client, string q) =>
        (await OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}"))).GetProperty("results").EnumerateArray().First();

    private async Task AdminConsentAsync(TestTenant t, Guid connection, Guid directory, string feature)
    {
        var start = await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{connection}/features/{feature}/consent");
        var state = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query)["state"];
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(state)}");
        var done = await Client(t.Key, t["admin"].Token, "tb_consent", Cookie(start, "tb_consent")!).GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.EndsWith("consent=granted", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    private async Task ConnectAsync(TestTenant t, string user)
    {
        var start = await t[user].Client.GetAsync("/api/v1/me/outlook/connect");
        var answer = Graph.Authorize(start.Headers.Location!);
        var relay = await world.Factory.PlatformClient().GetAsync("/api/v1/integrations/outlook/callback" + answer);
        var done = await Client(t.Key, t[user].Token, "tb_outlook", Cookie(start, "tb_outlook")!).GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.EndsWith("outlook=connected", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    private HttpClient Client(string tenantKey, string session, string cookie, string value)
    {
        var client = world.Factory.ClientFor($"{tenantKey}.{ApiFactory.BaseDomain}");
        client.DefaultRequestHeaders.Add("Cookie", $"tb_session={session}; {cookie}={value}");
        return client;
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..]).FirstOrDefault()
            : null;

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> RefusedAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }
}
