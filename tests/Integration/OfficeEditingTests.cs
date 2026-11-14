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
/// Spec 011 R2 FR-010 (US2 scenario 6): a contributor edits an Office document in the browser through the Microsoft 365
/// connection. The copy goes to their own OneDrive, check-in stores the edited file as a TeamBeats version with them as
/// author and removes the copy; normal check-in waits for the Office edit; others cannot take it over; discard, a removed
/// copy, a forced check-in, other formats, readers, tenants without the feature and other tenants are covered.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OfficeEditingTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private FakeMicrosoftGraph Graph => world.Factory.Graph;

    private sealed record Org(TestTenant T, Guid Library, Guid Directory);

    private async Task<Org> FreshAsync(bool connection = true)
    {
        var t = await world.CreateTenantAsync("oe-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "lm", "con", "con2", "reader" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        await world.AssignAsync(t, "lm", BuiltInRoles.OrganizationAdministrator, "org_subtree", dubai);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "doc_contributor", names = new Dictionary<string, string> { ["en"] = "Contributor" },
            permissions = new[] { Permissions.DocView, Permissions.DocContribute }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "con", "doc_contributor", "org_unit", dubai);
        await world.AssignAsync(t, "con2", "doc_contributor", "org_unit", dubai);
        var library = await OkAsync(t["lm"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Quality" }, scope = "org_unit", scopeId = dubai,
        }), HttpStatusCode.Created);
        var directory = Guid.NewGuid();
        if (connection)
        {
            var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
            {
                kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "office_editing" },
            }), HttpStatusCode.Created);
            await AdminConsentAsync(t, created.GetProperty("id").GetGuid(), directory, "office_editing");
            Graph.ConsentDelegated(directory, "User.Read", "Files.ReadWrite");
        }
        return new Org(t, library.GetProperty("id").GetGuid(), directory);
    }

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

    private static async Task<Guid> PublishedAsync(HttpClient client, Guid library, string name, string content)
    {
        var doc = await OkAsync(client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes(content))), HttpStatusCode.Created);
        return doc.GetProperty("id").GetGuid();
    }

    private static async Task<string> StateAsync(HttpClient client, Guid doc) =>
        (await OkAsync(client.GetAsync($"{Docs}/{doc}/office"))).GetProperty("state").GetString()!;

    private static async Task<string> DownloadAsync(HttpClient client, Guid document)
    {
        var token = await OkAsync(client.GetAsync($"{Docs}/{document}/download-token"));
        var file = await client.GetAsync(token.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        return await file.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_contributor_edits_in_the_browser_and_checks_in_a_TeamBeats_version()
    {
        var (t, library, _) = await FreshAsync();
        var con = t["con"].Client;
        var doc = await PublishedAsync(con, library, "plan.docx", "first draft");

        // Before connecting their Microsoft 365 account the page offers to connect.
        Assert.Equal("not_connected", await StateAsync(con, doc));
        await ConnectAsync(t, "con");
        Assert.Equal("ready", await StateAsync(con, doc));

        var started = await OkAsync(con.PostAsync($"{Docs}/{doc}/office/start", null));
        Assert.Equal("editing", started.GetProperty("state").GetString());
        Assert.StartsWith("https://", started.GetProperty("editUrl").GetString(), StringComparison.Ordinal);
        var copy = Assert.Single(Graph.DriveOf(t["con"].Email));
        Assert.Equal("TeamBeats editing/plan.docx", copy.Name);
        Assert.Equal("first draft", Encoding.UTF8.GetString(copy.Content));
        // Starting again returns the same edit.
        Assert.Equal(started.GetProperty("editUrl").GetString(), (await OkAsync(con.PostAsync($"{Docs}/{doc}/office/start", null))).GetProperty("editUrl").GetString());

        // The document is checked out to the editor; another contributor sees who holds it and cannot take over.
        var held = await OkAsync(con.GetAsync($"{Docs}/{doc}"));
        Assert.Equal(t["con"].Id, held.GetProperty("checkedOutBy").GetGuid());
        Assert.Equal("unavailable", await StateAsync(t["con2"].Client, doc));
        Assert.Equal("conflict.documents.checked_out", await RefusedAsync(t["con2"].Client.PostAsync($"{Docs}/{doc}/office/start", null), HttpStatusCode.Conflict));
        Assert.Equal("conflict.documents.office_not_editing", await RefusedAsync(t["con2"].Client.PostAsJsonAsync($"{Docs}/{doc}/office/checkin", new { publish = false }), HttpStatusCode.Conflict));

        // While the Office edit is open, the normal check-in, discard, working copy and delete wait for it.
        Assert.Equal("conflict.documents.office_editing", await RefusedAsync(con.PostAsync($"{Docs}/{doc}/checkin?publish=false&name=plan.docx",
            new ByteArrayContent(Encoding.UTF8.GetBytes("uploaded instead"))), HttpStatusCode.Conflict));
        Assert.Equal("conflict.documents.office_editing", await RefusedAsync(con.PostAsync($"{Docs}/{doc}/discard", null), HttpStatusCode.Conflict));
        Assert.Equal("conflict.documents.office_editing", await RefusedAsync(con.PutAsync($"{Docs}/{doc}/working?name=plan.docx",
            new ByteArrayContent(Encoding.UTF8.GetBytes("working"))), HttpStatusCode.Conflict));
        Assert.Equal("conflict.documents.office_editing", await RefusedAsync(con.DeleteAsync($"{Docs}/{doc}"), HttpStatusCode.Conflict));

        // Office for the web saves the change; check-in makes it a TeamBeats version by the contributor and removes the copy.
        Graph.EditDriveItem(copy.Id, Encoding.UTF8.GetBytes("edited in Word"));
        var checkedIn = await OkAsync(con.PostAsJsonAsync($"{Docs}/{doc}/office/checkin", new { note = "Edited in the browser", publish = true }));
        Assert.Equal(JsonValueKind.Null, checkedIn.GetProperty("checkedOutBy").ValueKind);
        Assert.Equal("2.0", checkedIn.GetProperty("effective").GetProperty("label").GetString());
        Assert.Equal(t["con"].Id, checkedIn.GetProperty("effective").GetProperty("authorUserId").GetGuid());
        Assert.Equal("Edited in the browser", checkedIn.GetProperty("effective").GetProperty("note").GetString());
        Assert.Empty(Graph.DriveOf(t["con"].Email));
        Assert.Equal("edited in Word", await DownloadAsync(t["reader"].Client, doc));
        Assert.Equal("ready", await StateAsync(con, doc));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocOfficeEditStarted, doc.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocOfficeEditEnded, doc.ToString());
    }

    [Fact]
    public async Task Discard_a_removed_copy_forced_check_in_formats_readers_and_tenants()
    {
        var (t, library, _) = await FreshAsync();
        var con = t["con"].Client;
        await ConnectAsync(t, "con");
        var doc = await PublishedAsync(con, library, "budget.xlsx", "numbers");

        // The copy was removed from OneDrive: check-in says so, and discard releases the document.
        await OkAsync(con.PostAsync($"{Docs}/{doc}/office/start", null));
        Graph.RemoveDriveItem(Assert.Single(Graph.DriveOf(t["con"].Email)).Id);
        Assert.Equal("conflict.documents.office_copy_gone", await RefusedAsync(con.PostAsJsonAsync($"{Docs}/{doc}/office/checkin", new { publish = false }), HttpStatusCode.Conflict));
        var discarded = await OkAsync(con.PostAsync($"{Docs}/{doc}/office/discard", null));
        Assert.Equal(JsonValueKind.Null, discarded.GetProperty("checkedOutBy").ValueKind);
        Assert.Equal("1.0", discarded.GetProperty("current").GetProperty("label").GetString());

        // Discard removes the copy too.
        await OkAsync(con.PostAsync($"{Docs}/{doc}/office/start", null));
        Assert.Single(Graph.DriveOf(t["con"].Email));
        await OkAsync(con.PostAsync($"{Docs}/{doc}/office/discard", null));
        Assert.Empty(Graph.DriveOf(t["con"].Email));

        // A manager's forced check-in ends the edit; the holder's copy stays in their OneDrive so nothing is lost.
        await OkAsync(con.PostAsync($"{Docs}/{doc}/office/start", null));
        await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Docs}/{doc}/force-checkin", new { reason = "Needed for the audit" }));
        Assert.Single(Graph.DriveOf(t["con"].Email));
        Assert.Equal("ready", await StateAsync(con, doc));
        Assert.Equal("conflict.documents.office_not_editing", await RefusedAsync(con.PostAsync($"{Docs}/{doc}/office/discard", null), HttpStatusCode.Conflict));

        // Only Word, Excel and PowerPoint files; readers are not offered it and are refused.
        var text = await PublishedAsync(con, library, "notes.txt", "plain");
        Assert.Equal("unavailable", await StateAsync(con, text));
        Assert.Equal("conflict.documents.office_format", await RefusedAsync(con.PostAsync($"{Docs}/{text}/office/start", null), HttpStatusCode.Conflict));
        Assert.Equal("unavailable", await StateAsync(t["reader"].Client, doc));
        await RefusedAsync(t["reader"].Client.PostAsync($"{Docs}/{doc}/office/start", null), HttpStatusCode.Forbidden);

        // Without the connection feature the button is not offered; another tenant gets not found.
        var (other, otherLibrary, _) = await FreshAsync(connection: false);
        var plain = await PublishedAsync(other["con"].Client, otherLibrary, "memo.docx", "memo");
        Assert.Equal("unavailable", await StateAsync(other["con"].Client, plain));
        Assert.Equal(HttpStatusCode.NotFound, (await other["con"].Client.GetAsync($"{Docs}/{doc}/office")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["con"].Client.PostAsync($"{Docs}/{doc}/office/start", null)).StatusCode);
    }
}
