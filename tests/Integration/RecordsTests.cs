using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Audit;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R2 US6 and US7 (FR-013 to FR-015, SC-003): retention labels applied by library, type or by hand, with the
/// longer label winning; a label in its period refuses deletion; disposition review, automatic deletion and archiving
/// when the period ends, with a content-free disposition record; legal holds by custodian, library, folder or document
/// that block user deletion, the recycle bin purge, retention and the audit purge until released, after which held
/// documents go to review; records managers only; packages without the capability refused; other tenants' targets refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RecordsTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";
    private const string Records = "/api/v1/records";

    private sealed record Org(TestTenant T, Guid Library);

    private async Task<Org> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("rec-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "rm", "con", "omar", "dee" })
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
        await world.AssignAsync(t, "omar", "doc_contributor", "tenant", null);
        var library = await OkAsync(t["admin"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Quality records", ["ar"] = "سجلات الجودة" }, scope = "tenant",
        }), HttpStatusCode.Created);
        return new Org(t, Id(library));
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

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en };

    private static async Task<Guid> PublishedAsync(HttpClient client, Guid library, string name, Guid? type = null)
    {
        var url = $"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true" + (type is { } t ? $"&type={t}" : "");
        return Id(await OkAsync(client.PostAsync(url, new ByteArrayContent(Encoding.UTF8.GetBytes("content of " + name))), HttpStatusCode.Created));
    }

    private static async Task<Guid> LabelAsync(HttpClient rm, string name, int months, string action, string trigger = "created") =>
        Id(await OkAsync(rm.PostAsJsonAsync($"{Records}/labels", new { names = Names(name), periodMonths = months, trigger, action }), HttpStatusCode.Created));

    private static async Task<Guid?> LabelOfAsync(HttpClient rm, Guid document)
    {
        var view = await OkAsync(rm.GetAsync($"{Records}/documents/{document}"));
        return view.GetProperty("labelId").ValueKind == JsonValueKind.Null ? null : view.GetProperty("labelId").GetGuid();
    }

    private Task<int> RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var done = await sp.GetRequiredService<Records>().RunAsync(now, 100, CancellationToken.None)
                + await sp.GetRequiredService<DocumentVersions>().PurgeAsync(now, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return done;
        });

    private static async Task<Guid[]> QueueAsync(HttpClient rm) =>
        (await OkAsync(rm.GetAsync($"{Records}/disposition"))).EnumerateArray().Select(e => e.GetProperty("documentId").GetGuid()).ToArray();

    [Fact]
    public async Task Labels_apply_by_library_type_or_hand_the_longer_wins_and_a_label_in_its_period_refuses_deletion()
    {
        var o = await FreshAsync("workplace_plus");
        var t = o.T;
        var rm = t["rm"].Client;
        var con = t["con"].Client;

        // Only records managers, and only with records.retention in the package.
        Assert.Equal(HttpStatusCode.Forbidden, (await con.GetAsync($"{Records}/labels")).StatusCode);
        var small = await world.CreateTenantAsync("rec-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await small["admin"].Client.GetAsync($"{Records}/labels")).StatusCode);
        Assert.Contains("validation.records.trigger_field",
            await ErrorKeysAsync(await rm.PostAsJsonAsync($"{Records}/labels", new { names = Names("Bad"), periodMonths = 12, trigger = "field", action = "review" })));
        Assert.Contains("validation.records.period",
            await ErrorKeysAsync(await rm.PostAsJsonAsync($"{Records}/labels", new { names = Names("Bad"), periodMonths = 0, trigger = "created", action = "review" })));

        var shortLabel = await LabelAsync(rm, "Working file, 1 year", 12, "review");
        var longLabel = await LabelAsync(rm, "Quality record, 10 years", 120, "review");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsLabelCreated, shortLabel.ToString());

        var before = await PublishedAsync(con, o.Library, "before.txt");
        await OkAsync(rm.PutAsJsonAsync($"{Records}/libraries/{o.Library}/label", new { labelId = shortLabel }), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsLabelApplied, o.Library.ToString());
        Assert.Equal(shortLabel, await LabelOfAsync(rm, before));
        var after = await PublishedAsync(con, o.Library, "after.txt");
        Assert.Equal(shortLabel, await LabelOfAsync(rm, after));

        // A type with the longer label: its documents carry the longer one.
        var type = Id(await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Libraries}/{o.Library}/types", new { names = Names("SOP"), fields = Array.Empty<object>() }),
            HttpStatusCode.Created));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/types/{type}/label", new { labelId = longLabel }), HttpStatusCode.NoContent);
        var sop = await PublishedAsync(con, o.Library, "sop.txt", type);
        Assert.Equal(longLabel, await LabelOfAsync(rm, sop));

        // By hand, then back to what the library gives.
        var manual = await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{after}/label", new { labelId = longLabel }));
        Assert.Equal("manual", manual.GetProperty("source").GetString());
        await OkAsync(rm.PutAsJsonAsync($"{Records}/libraries/{o.Library}/label", new { labelId = (Guid?)null }), HttpStatusCode.NoContent);
        Assert.Null(await LabelOfAsync(rm, before));
        Assert.Equal(longLabel, await LabelOfAsync(rm, after));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{after}/label", new { labelId = (Guid?)null }));
        Assert.Null(await LabelOfAsync(rm, after));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/libraries/{o.Library}/label", new { labelId = shortLabel }), HttpStatusCode.NoContent);

        // A document in its period cannot be deleted; the message names a retention rule.
        var refused = await con.DeleteAsync($"{Docs}/{before}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("conflict.documents.retention_rule", await ErrorKeysAsync(refused));

        // A shorter period reaches documents labelled before only when the records manager reapplies it.
        var label = (await OkAsync(rm.GetAsync($"{Records}/labels"))).EnumerateArray().Single(l => Id(l) == longLabel);
        Assert.Equal(1, label.GetProperty("documents").GetInt32());
        await OkAsync(rm.PutAsJsonAsync($"{Records}/labels/{longLabel}", new
        {
            names = Names("Quality record, 2 years"), periodMonths = 24, trigger = "created", action = "review", version = label.GetProperty("version").GetInt32(),
        }));
        var now = DateTimeOffset.UtcNow;
        await RunAsync(t, now.AddMonths(30));
        Assert.DoesNotContain(sop, await QueueAsync(rm));
        Assert.Contains(before, await QueueAsync(rm));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsLabelChanged, longLabel.ToString());
        await OkAsync(rm.PutAsJsonAsync($"{Records}/labels/{longLabel}", new
        {
            names = Names("Quality record, 2 years"), periodMonths = 24, trigger = "created", action = "review", version = label.GetProperty("version").GetInt32() + 1, reapply = true,
        }));
        await RunAsync(t, now.AddMonths(30));
        Assert.Contains(sop, await QueueAsync(rm));
    }

    [Fact]
    public async Task When_the_period_ends_documents_go_to_review_are_deleted_or_archived_and_a_disposition_record_stays()
    {
        var o = await FreshAsync("workplace_plus");
        var t = o.T;
        var rm = t["rm"].Client;
        var con = t["con"].Client;
        var review = await LabelAsync(rm, "Review after a year", 12, "review");
        var delete = await LabelAsync(rm, "Delete after a year", 12, "delete");
        var archive = await LabelAsync(rm, "Archive after a year", 12, "archive");
        var a = await PublishedAsync(con, o.Library, "reviewed.txt");
        var b = await PublishedAsync(con, o.Library, "deleted.txt");
        var c = await PublishedAsync(con, o.Library, "archived.txt");
        var kept = await PublishedAsync(con, o.Library, "kept.txt");
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{a}/label", new { labelId = review }));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{b}/label", new { labelId = delete }));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{c}/label", new { labelId = archive }));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{kept}/label", new { labelId = review }));

        // Nothing happens inside the period.
        await RunAsync(t, DateTimeOffset.UtcNow.AddMonths(6));
        Assert.Empty(await QueueAsync(rm));

        var later = DateTimeOffset.UtcNow.AddMonths(13);
        await RunAsync(t, later);
        var queue = await QueueAsync(rm);
        Assert.Contains(a, queue);
        Assert.Contains(kept, queue);

        // Automatic deletion: gone with its versions, audited with counts, search told.
        Assert.Equal(HttpStatusCode.NotFound, (await con.GetAsync($"{Docs}/{b}")).StatusCode);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document_version where document_id = @d", ("d", b)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsDispositionApproved, b.ToString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DocumentDisposed", t.Id));

        // Archived: contributors still see it read-only, readers and search no longer do.
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsArchived, c.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{c}")).StatusCode);
        await OkAsync(con.GetAsync($"{Docs}/{c}"));
        Assert.Contains("conflict.documents.archived", await ErrorKeysAsync(await con.PostAsync($"{Docs}/{c}/checkout", null)));

        // The records manager disposes of one and keeps the other for six more months.
        Assert.Equal(HttpStatusCode.Forbidden, (await con.PostAsJsonAsync($"{Records}/disposition/{a}", new { decision = "dispose" })).StatusCode);
        Assert.Contains("validation.records.decision", await ErrorKeysAsync(await rm.PostAsJsonAsync($"{Records}/disposition/{a}", new { decision = "keep", keepMonths = 0 })));
        await OkAsync(rm.PostAsJsonAsync($"{Records}/disposition/{a}", new { decision = "dispose" }), HttpStatusCode.NoContent);
        await OkAsync(rm.PostAsJsonAsync($"{Records}/disposition/{kept}", new { decision = "keep", keepMonths = 6 }), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsDispositionKept, kept.ToString());
        Assert.Empty(await QueueAsync(rm));
        Assert.Equal(HttpStatusCode.NotFound, (await con.GetAsync($"{Docs}/{a}")).StatusCode);
        await OkAsync(con.GetAsync($"{Docs}/{kept}"));

        // The disposition records name the label and mode, never the document's name.
        var records = (await OkAsync(rm.GetAsync($"{Records}/dispositions"))).EnumerateArray().ToArray();
        Assert.Equal(2, records.Length);
        Assert.Contains(records, r => r.GetProperty("documentId").GetGuid() == a && r.GetProperty("mode").GetString() == "reviewed");
        Assert.Contains(records, r => r.GetProperty("documentId").GetGuid() == b && r.GetProperty("mode").GetString() == "automatic");
        Assert.DoesNotContain("reviewed.txt", records.Select(r => r.GetRawText()).First(r => r.Contains(a.ToString(), StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_legal_hold_blocks_every_deletion_path_until_released_and_then_held_documents_go_to_review()
    {
        var o = await FreshAsync();
        var t = o.T;
        var rm = t["rm"].Client;
        var con = t["con"].Client;
        var omar = t["omar"];
        var deleteLabel = await LabelAsync(rm, "Delete after a year", 12, "delete");
        var byOmar = await PublishedAsync(omar.Client, o.Library, "omar-notes.txt");
        var byCon = await PublishedAsync(con, o.Library, "con-notes.txt");
        var binned = await PublishedAsync(con, o.Library, "binned.txt");
        await OkAsync(con.DeleteAsync($"{Docs}/{binned}"), HttpStatusCode.NoContent);

        // Holds need records.legal_hold and records.manage; their targets must be of this tenant.
        var plus = await FreshAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.Forbidden, (await plus.T["rm"].Client.GetAsync($"{Records}/holds")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await con.GetAsync($"{Records}/holds")).StatusCode);
        Assert.Contains("validation.records.hold_custodian", await ErrorKeysAsync(await rm.PostAsJsonAsync($"{Records}/holds",
            new { name = "Other", reason = "Test", custodianId = plus.T["omar"].Id })));
        Assert.Contains("validation.records.hold_library", await ErrorKeysAsync(await rm.PostAsJsonAsync($"{Records}/holds",
            new { name = "Other", reason = "Test", libraryId = plus.Library })));
        Assert.Contains("validation.records.hold_target", await ErrorKeysAsync(await rm.PostAsJsonAsync($"{Records}/holds",
            new { name = "Two", reason = "Test", custodianId = omar.Id, libraryId = o.Library })));

        var custodian = await OkAsync(rm.PostAsJsonAsync($"{Records}/holds", new
        {
            name = "Supplier dispute", reason = "Litigation hold for the supplier dispute", custodianId = omar.Id,
            to = DateTimeOffset.UtcNow.AddDays(1), includeAudit = true,
        }), HttpStatusCode.Created);
        var custodianHold = Id(custodian.GetProperty("hold"));
        Assert.Equal(new[] { byOmar }, custodian.GetProperty("documents").EnumerateArray().Select(Id).ToArray());
        var item = Id((await OkAsync(rm.PostAsJsonAsync($"{Records}/holds", new { name = "Bin item", reason = "Keep the deleted file", documentId = binned }),
            HttpStatusCode.Created)).GetProperty("hold"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsHoldApplied, custodianHold.ToString());
        Assert.Equal(2L, await world.Database.OutboxCountAsync("LegalHoldApplied", t.Id));

        // Path 1, user deletion: refused with the retention message, and the attempt is audited.
        var refused = await omar.Client.DeleteAsync($"{Docs}/{byOmar}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("conflict.documents.retention_rule", await ErrorKeysAsync(refused));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsDeletionBlocked, byOmar.ToString());
        await OkAsync(con.DeleteAsync($"{Docs}/{byCon}"), HttpStatusCode.NoContent);
        await OkAsync(con.PostAsync($"{Docs}/{byCon}/undelete", null));

        // Path 2, the recycle bin purge: the held file stays in the bin past the period, audited once.
        var far = DateTimeOffset.UtcNow.AddMonths(14);
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{byOmar}/label", new { labelId = deleteLabel }));
        await OkAsync(rm.PutAsJsonAsync($"{Records}/documents/{byCon}/label", new { labelId = deleteLabel }));
        await RunAsync(t, far);
        await RunAsync(t, far);
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document where id = @d", ("d", binned)));
        Assert.Equal(1L, await world.Database.AuditCountAsync(t.Id, AuditActions.RecordsDeletionBlocked, binned.ToString()));

        // Path 3, retention: the held document is not deleted (audited once), the free one is.
        Assert.Equal(2L, await world.Database.AuditCountAsync(t.Id, AuditActions.RecordsDeletionBlocked, byOmar.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, (await con.GetAsync($"{Docs}/{byCon}")).StatusCode);
        await OkAsync(omar.Client.GetAsync($"{Docs}/{byOmar}"));

        // Path 4, the audit purge keeps the custodian's records.
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into audit.audit_event (tenant_id, id, occurred_at, actor_id, actor_type, action, target_type, outcome)
            values (@t, gen_random_uuid(), now() - interval '400 days', null, 'system', 'test.ancient_other', 'test', 'success'),
                   (@t, gen_random_uuid(), now() - interval '399 days', @omar, 'user', 'test.ancient_omar', 'test', 'success')
            """, ("t", t.Id), ("omar", omar.Id));
        await PurgeAuditAsync(t);
        Assert.Equal(0L, await world.Database.AuditCountAsync(t.Id, "test.ancient_other"));
        Assert.Equal(1L, await world.Database.AuditCountAsync(t.Id, "test.ancient_omar"));

        // Hold details are for records managers only; everyone else sees nothing of them.
        Assert.Equal(HttpStatusCode.Forbidden, (await omar.Client.GetAsync($"{Records}/holds/{custodianHold}")).StatusCode);
        Assert.Equal(2, (await OkAsync(rm.GetAsync($"{Records}/holds"))).GetArrayLength());

        // Released: the period passed while held, so the document goes to review rather than being deleted.
        await OkAsync(rm.PostAsync($"{Records}/holds/{custodianHold}/release", null));
        Assert.Contains("conflict.records.hold_released", await ErrorKeysAsync(await rm.PostAsync($"{Records}/holds/{custodianHold}/release", null)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RecordsHoldReleased, custodianHold.ToString());
        await RunAsync(t, far);
        Assert.Contains(byOmar, await QueueAsync(rm));
        await OkAsync(omar.Client.GetAsync($"{Docs}/{byOmar}"));
        await PurgeAuditAsync(t);
        Assert.Equal(0L, await world.Database.AuditCountAsync(t.Id, "test.ancient_omar"));

        // A library hold catches a disposition decision too.
        var library = Id((await OkAsync(rm.PostAsJsonAsync($"{Records}/holds", new { name = "Library", reason = "Audit", libraryId = o.Library }), HttpStatusCode.Created))
            .GetProperty("hold"));
        var blocked = await rm.PostAsJsonAsync($"{Records}/disposition/{byOmar}", new { decision = "dispose" });
        Assert.Contains("conflict.records.held", await ErrorKeysAsync(blocked));
        await OkAsync(rm.PostAsync($"{Records}/holds/{library}/release", null));
        await OkAsync(rm.PostAsync($"{Records}/holds/{item}/release", null));
        await OkAsync(rm.PostAsJsonAsync($"{Records}/disposition/{byOmar}", new { decision = "dispose" }), HttpStatusCode.NoContent);
        await RunAsync(t, far);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document where id = @d", ("d", binned)));
    }

    private Task PurgeAuditAsync(TestTenant t) =>
        world.Factory.InScopeAsync<long>(t.Id, async sp =>
        {
            await sp.GetRequiredService<AuditMaintenance>().SealAsync(10_000, CancellationToken.None);
            var n = await sp.GetRequiredService<AuditMaintenance>().PurgeAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });
}
