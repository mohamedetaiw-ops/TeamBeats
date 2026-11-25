using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R3: records declared by a records manager or by a label that declares records refuse every change but
/// disposition; undeclaring needs a reason and is refused while a declaring label applies; a declaring label keeps
/// declaring once used. Disposition certificates list a fixed set of disposition records with a digest that verify
/// recomputes, and print as PDF. Records managers only; packages without retention refused; other tenants not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RecordsDeclarationTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";
    private const string Records = "/api/v1/records";

    private sealed record Org(TestTenant T, Guid Library);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("rcd-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "rm", "con", "dee" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "rm", BuiltInRoles.RecordsManager, "tenant", null);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "doc_contributor", names = new Dictionary<string, string> { ["en"] = "Contributor" },
            permissions = new[] { Permissions.DocView, Permissions.DocContribute, Permissions.DocManage }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "con", "doc_contributor", "tenant", null);
        var library = await OkAsync(t["admin"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Contracts", ["ar"] = "العقود" }, scope = "tenant",
        }), HttpStatusCode.Created);
        return new Org(t, library.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).First();
        }
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static async Task<Guid> PublishedAsync(HttpClient client, Guid library, string name) =>
        (await OkAsync(client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes("content of " + name))), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<JsonElement> LabelAsync(HttpClient rm, string name, int months, string action, bool declares) =>
        await OkAsync(rm.PostAsJsonAsync($"{Records}/labels", new
        {
            names = new Dictionary<string, string> { ["en"] = name }, periodMonths = months, trigger = "created", action, declaresRecord = declares,
        }), HttpStatusCode.Created);

    private Task RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<Records>().RunAsync(now, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private static async Task AssertLockedAsync(HttpClient con, Guid document)
    {
        var view = await OkAsync(con.GetAsync($"{Docs}/{document}"));
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("recordAt").ValueKind);
        Assert.Equal("conflict.documents.record", await KeyAsync(await con.PostAsync($"{Docs}/{document}/checkout", null)));
        Assert.Equal("conflict.documents.record", await KeyAsync(await con.PutAsJsonAsync($"{Docs}/{document}/metadata", new
        {
            metadata = new Dictionary<string, object>(), version = view.GetProperty("version").GetInt32(),
        })));
        Assert.Equal("conflict.documents.record", await KeyAsync(await con.DeleteAsync($"{Docs}/{document}")));
        Assert.Equal("conflict.documents.record", await KeyAsync(await con.PostAsJsonAsync($"{Docs}/{document}/restore", new
        {
            versionId = view.GetProperty("effective").GetProperty("id").GetGuid(),
        })));
        // Reading and downloading still work.
        await OkAsync(con.GetAsync($"{Docs}/{document}/download-token"));
    }

    [Fact]
    public async Task Records_declared_by_hand_or_by_label_refuse_every_change_and_undeclaring_needs_a_reason()
    {
        var o = await FreshAsync();
        var t = o.T;
        var rm = t["rm"].Client;
        var con = t["con"].Client;

        // By hand.
        var contract = await PublishedAsync(con, o.Library, "contract.txt");
        Assert.Equal(HttpStatusCode.Forbidden, (await con.PostAsync($"{Records}/documents/{contract}/declare", null)).StatusCode);
        var declared = await OkAsync(rm.PostAsync($"{Records}/documents/{contract}/declare", null));
        Assert.Equal("manual", declared.GetProperty("recordSource").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsDeclared, contract.ToString());
        await AssertLockedAsync(con, contract);
        var listed = await OkAsync(rm.GetAsync($"{Records}/records"));
        Assert.Contains(listed.EnumerateArray(), r => r.GetProperty("documentId").GetGuid() == contract);

        // Undeclaring needs a reason, and then the document can change again.
        Assert.Equal("validation.records.undeclare_reason", await KeyAsync(await rm.PostAsJsonAsync($"{Records}/documents/{contract}/undeclare", new { reason = " " })));
        var undeclared = await OkAsync(rm.PostAsJsonAsync($"{Records}/documents/{contract}/undeclare", new { reason = "Declared by mistake" }));
        Assert.Equal(JsonValueKind.Null, undeclared.GetProperty("recordAt").ValueKind);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsUndeclared, contract.ToString());
        Assert.Equal("conflict.records.not_record", await KeyAsync(await rm.PostAsJsonAsync($"{Records}/documents/{contract}/undeclare", new { reason = "Again" })));
        await OkAsync(con.PostAsync($"{Docs}/{contract}/checkout", null));

        // A checked out document cannot be declared until it is checked in.
        Assert.Equal("conflict.records.checked_out", await KeyAsync(await rm.PostAsync($"{Records}/documents/{contract}/declare", null)));

        // By a label on the library: published documents become records at once, new ones when the maintenance run sees them published.
        var label = await LabelAsync(rm, "Contract records", 120, "review", declares: true);
        var labelId = label.GetProperty("id").GetGuid();
        Assert.True(label.GetProperty("declaresRecord").GetBoolean());
        var before = await PublishedAsync(con, o.Library, "signed.txt");
        await OkAsync(rm.PutAsJsonAsync($"{Records}/libraries/{o.Library}/label", new { labelId }), HttpStatusCode.NoContent);
        var retention = await OkAsync(rm.GetAsync($"{Records}/documents/{before}"));
        Assert.Equal("label", retention.GetProperty("recordSource").GetString());
        await AssertLockedAsync(con, before);
        var after = await PublishedAsync(con, o.Library, "renewal.txt");
        await RunAsync(t, DateTimeOffset.UtcNow);
        await AssertLockedAsync(con, after);

        // While the label declares records, undeclaring is refused, and the label cannot stop declaring.
        Assert.Equal("conflict.records.declared_by_label", await KeyAsync(await rm.PostAsJsonAsync($"{Records}/documents/{before}/undeclare", new { reason = "Not needed" })));
        Assert.Equal("conflict.records.label_declared", await KeyAsync(await rm.PutAsJsonAsync($"{Records}/labels/{labelId}", new
        {
            names = new Dictionary<string, string> { ["en"] = "Contract records" }, periodMonths = 120, trigger = "created", action = "review", declaresRecord = false,
            version = label.GetProperty("version").GetInt32(),
        })));

        // Another tenant's document is not found; a package without retention is refused.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["rm"].Client.PostAsync($"{Records}/documents/{before}/declare", null)).StatusCode);
        var small = await world.CreateTenantAsync("rcd-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await small["admin"].Client.GetAsync($"{Records}/records")).StatusCode);
    }

    [Fact]
    public async Task A_record_is_disposed_of_only_through_disposition_and_a_held_record_is_kept()
    {
        var o = await FreshAsync("enterprise_gov");
        var t = o.T;
        var rm = t["rm"].Client;
        var con = t["con"].Client;
        var label = (await LabelAsync(rm, "Review after a year", 12, "review", declares: false)).GetProperty("id").GetGuid();
        var free = await PublishedAsync(con, o.Library, "free.txt");
        var held = await PublishedAsync(con, o.Library, "held.txt");
        foreach (var d in new[] { free, held })
        {
            await OkAsync(rm.PostAsync($"{Records}/documents/{d}/declare", null));
            await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{d}/label", new { labelId = label }));
        }
        // A label change keeps the record declared.
        Assert.Equal("manual", (await OkAsync(rm.GetAsync($"{Records}/documents/{free}"))).GetProperty("recordSource").GetString());
        await OkAsync(rm.PostAsJsonAsync($"{Records}/holds", new { name = "Audit", reason = "External audit", documentId = held }), HttpStatusCode.Created);
        await RunAsync(t, DateTimeOffset.UtcNow.AddMonths(13));
        // The held record stays out of the queue; the other waits for the records manager.
        var queue = (await OkAsync(rm.GetAsync($"{Records}/disposition"))).EnumerateArray().Select(e => e.GetProperty("documentId").GetGuid()).ToArray();
        Assert.Equal([free], queue);
        Assert.Equal(HttpStatusCode.NotFound, (await rm.PostAsJsonAsync($"{Records}/disposition/{held}", new { decision = "dispose" })).StatusCode);
        await OkAsync(rm.PostAsJsonAsync($"{Records}/disposition/{free}", new { decision = "dispose" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await con.GetAsync($"{Docs}/{free}")).StatusCode);
        await OkAsync(con.GetAsync($"{Docs}/{held}"));
    }

    [Fact]
    public async Task A_disposition_certificate_lists_a_fixed_set_with_a_digest_that_verify_checks_and_prints_as_pdf()
    {
        var o = await FreshAsync();
        var t = o.T;
        var rm = t["rm"].Client;
        var con = t["con"].Client;
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var label = (await LabelAsync(rm, "Delete after a year", 12, "delete", declares: false)).GetProperty("id").GetGuid();
        var first = await PublishedAsync(con, o.Library, "invoice-1.txt");
        var second = await PublishedAsync(con, o.Library, "invoice-2.txt");
        foreach (var d in new[] { first, second })
        {
            await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{d}/label", new { labelId = label }));
        }
        await RunAsync(t, DateTimeOffset.UtcNow.AddMonths(13));

        // Only records managers; ranges must end by now, span at most a year and hold something.
        Assert.Equal(HttpStatusCode.Forbidden, (await con.GetAsync($"{Records}/certificates")).StatusCode);
        Assert.Equal("validation.records.certificate_range", await KeyAsync(await rm.PostAsJsonAsync($"{Records}/certificates",
            new { from = start, to = DateTimeOffset.UtcNow.AddDays(2) })));
        Assert.Equal("validation.records.certificate_range", await KeyAsync(await rm.PostAsJsonAsync($"{Records}/certificates",
            new { from = start.AddDays(-400), to = DateTimeOffset.UtcNow })));
        Assert.Equal("validation.records.certificate_empty", await KeyAsync(await rm.PostAsJsonAsync($"{Records}/certificates",
            new { from = start.AddDays(-30), to = start })));

        var end = DateTimeOffset.UtcNow;
        var issued = await OkAsync(rm.PostAsJsonAsync($"{Records}/certificates", new { from = start, to = end }), HttpStatusCode.Created);
        var certificate = issued.GetProperty("certificate");
        var id = certificate.GetProperty("id").GetGuid();
        var code = certificate.GetProperty("code").GetString()!;
        Assert.Equal(2, certificate.GetProperty("records").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", certificate.GetProperty("digest").GetString()!);
        Assert.True(issued.GetProperty("intact").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsCertificateIssued, id.ToString());

        // A later disposition in the same range is not added to the issued certificate.
        var third = await PublishedAsync(con, o.Library, "invoice-3.txt");
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{third}/label", new { labelId = label }));
        await RunAsync(t, DateTimeOffset.UtcNow.AddMonths(13));
        var again = await OkAsync(rm.GetAsync($"{Records}/certificates/{id}"));
        Assert.Equal(2, again.GetProperty("items").GetArrayLength());
        Assert.True(again.GetProperty("intact").GetBoolean());

        // Verify by the typed ID; a changed disposition record no longer matches the digest.
        var verified = await OkAsync(rm.GetAsync($"{Records}/certificates/verify?code={code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()}"));
        Assert.Equal(id, verified.GetProperty("certificate").GetProperty("id").GetGuid());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with changed as (update documents.disposition_record set versions = versions + 1 where document_id = @d returning 1) select count(*) from changed", ("d", first)));
        Assert.False((await OkAsync(rm.GetAsync($"{Records}/certificates/verify?code={code}"))).GetProperty("intact").GetBoolean());

        // The PDF lists the records and the ID, never the document names.
        var pdf = await rm.GetAsync($"{Records}/certificates/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        var printed = await pdf.Content.ReadAsStringAsync();
        Assert.StartsWith("%PDF", printed, StringComparison.Ordinal);
        Assert.Contains(code, printed, StringComparison.Ordinal);
        Assert.Contains("Delete after a year", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("invoice-1.txt", printed, StringComparison.Ordinal);
        world.Factory.Gotenberg.HtmlDown = true;
        try
        {
            Assert.Equal("conflict.records.certificate_unavailable", await KeyAsync(await rm.GetAsync($"{Records}/certificates/{id}/pdf")));
        }
        finally
        {
            world.Factory.Gotenberg.HtmlDown = false;
        }

        // Another tenant's records manager finds nothing.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["rm"].Client.GetAsync($"{Records}/certificates/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["rm"].Client.GetAsync($"{Records}/certificates/verify?code={code}")).StatusCode);
    }
}
