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
/// Spec 011 T006 and T007 (US2, FR-004, FR-005, FR-018): exclusive check-out with the holder shown; working copies and
/// minor versions that readers never see; publishing the next major version; a manager's forced check-in that keeps the
/// holder's changes as a draft; comparing text versions; restoring an old version as a new one; the recycle bin with
/// restore and the purge after the tenant's period; check-out reminders; other tenants and units get not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentVersionTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private sealed record Org(TestTenant T, Guid Library);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("dv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "lm", "con", "con2", "dee", "cai" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "dee", "con", "con2" })
        {
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        await world.AddMembershipAsync(t, "cai", "Cairo");
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
            names = new Dictionary<string, string> { ["en"] = "Quality", ["ar"] = "الجودة" }, scope = "org_unit", scopeId = dubai,
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

    private static string? Label(JsonElement doc, string which) =>
        doc.GetProperty(which).ValueKind == JsonValueKind.Null ? null : doc.GetProperty(which).GetProperty("label").GetString();

    private static ByteArrayContent Body(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<Guid> PublishedAsync(HttpClient client, Guid library, string name, string content)
    {
        var doc = await OkAsync(client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true", Body(content)), HttpStatusCode.Created);
        Assert.Equal("1.0", Label(doc, "effective"));
        return Id(doc);
    }

    private static Task<HttpResponseMessage> CheckInAsync(HttpClient client, Guid doc, string? name, string? content, bool publish = false, string? note = null)
    {
        var query = new List<string> { "publish=" + (publish ? "true" : "false") };
        if (name is not null) query.Add("name=" + Uri.EscapeDataString(name));
        if (note is not null) query.Add("note=" + Uri.EscapeDataString(note));
        return client.PostAsync($"{Docs}/{doc}/checkin?{string.Join('&', query)}", content is null ? null : Body(content));
    }

    private static async Task<string> DownloadAsync(HttpClient client, Guid document, Guid? version = null)
    {
        var token = await OkAsync(client.GetAsync($"{Docs}/{document}/download-token" + (version is { } v ? $"?version={v}" : "")));
        var file = await client.GetAsync(token.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        return await file.Content.ReadAsStringAsync();
    }

    private Task<int> MaintainAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var versions = sp.GetRequiredService<DocumentVersions>();
            var done = await versions.PurgeAsync(now, 100, CancellationToken.None) + await versions.RemindCheckOutsAsync(now, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return done;
        });

    [Fact]
    public async Task Check_out_is_exclusive_and_minor_versions_stay_hidden_from_readers_until_the_next_major_is_published()
    {
        var o = await FreshAsync();
        var t = o.T;
        var con = t["con"].Client;
        var doc = await PublishedAsync(con, o.Library, "procedure.txt", "Wear a harness.");

        var held = await OkAsync(con.PostAsync($"{Docs}/{doc}/checkout", null));
        Assert.Equal(t["con"].Id, held.GetProperty("checkedOutBy").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocCheckedOut, doc.ToString());

        // Someone else sees who holds it and cannot take it, save over it, check in or delete it.
        var shown = await OkAsync(t["con2"].Client.GetAsync($"{Docs}/{doc}"));
        Assert.Equal(t["con"].Id, shown.GetProperty("checkedOutBy").GetGuid());
        foreach (var call in new[]
        {
            t["con2"].Client.PostAsync($"{Docs}/{doc}/checkout", null),
            CheckInAsync(t["con2"].Client, doc, "procedure.txt", "Mine."),
            t["con2"].Client.DeleteAsync($"{Docs}/{doc}"),
        })
        {
            var refused = await call;
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("conflict.documents.checked_out", await ErrorKeysAsync(refused));
        }
        // Readers do not check out.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dee"].Client.PostAsync($"{Docs}/{doc}/checkout", null)).StatusCode);
        // A working copy is not a version; nobody reads it but its holder, and checking in uses it.
        var saved = await OkAsync(con.PutAsync($"{Docs}/{doc}/working?name=procedure.txt", Body("Wear a harness and a helmet.")));
        Assert.Equal("0.0", Label(saved, "pending"));
        Assert.Equal("Wear a harness.", await DownloadAsync(t["dee"].Client, doc));
        Assert.Contains("validation.documents.note", await ErrorKeysAsync(await CheckInAsync(con, doc, null, null, note: new string('n', 1001))));

        var minor = await OkAsync(CheckInAsync(con, doc, null, null, note: "Helmet added"));
        Assert.Equal("1.1", Label(minor, "current"));
        Assert.Equal("1.0", Label(minor, "effective"));
        Assert.Equal(JsonValueKind.Null, minor.GetProperty("checkedOutBy").ValueKind);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocCheckedIn, doc.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocVersionCreated, doc.ToString());
        // Readers still get 1.0 and only 1.0; contributors see the whole history.
        Assert.Equal("Wear a harness.", await DownloadAsync(t["dee"].Client, doc));
        Assert.Equal(["1.0"], (await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{doc}/versions"))).EnumerateArray().Select(v => v.GetProperty("label").GetString()));
        var history = await OkAsync(con.GetAsync($"{Docs}/{doc}/versions"));
        Assert.Equal(["1.1", "1.0"], history.EnumerateArray().Select(v => v.GetProperty("label").GetString()));
        Assert.Equal("Helmet added", history[0].GetProperty("note").GetString());
        var draftVersion = Id(history[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{doc}/download-token?version={draftVersion}")).StatusCode);

        // Checking in without a check-out works when nobody holds it; publishing makes 2.0 the version readers get.
        Assert.Contains("validation.documents.checkin_file", await ErrorKeysAsync(await CheckInAsync(t["con2"].Client, doc, null, null)));
        var major = await OkAsync(CheckInAsync(t["con2"].Client, doc, "procedure.txt", "Wear a harness, a helmet and gloves.", publish: true));
        Assert.Equal("2.0", Label(major, "effective"));
        Assert.Equal("Wear a harness, a helmet and gloves.", await DownloadAsync(t["dee"].Client, doc));
        Assert.Equal(2L, await world.Database.OutboxCountAsync("DocumentVersionPublished", t.Id));
        var states = (await OkAsync(con.GetAsync($"{Docs}/{doc}/versions"))).EnumerateArray().Select(v => v.GetProperty("state").GetString()).ToList();
        Assert.Equal(["effective", "draft", "superseded"], states);

        // Discarding drops the working copy and leaves the history as it was.
        await OkAsync(con.PostAsync($"{Docs}/{doc}/checkout", null));
        await OkAsync(con.PutAsync($"{Docs}/{doc}/working?name=procedure.txt", Body("scratch")));
        var discarded = await OkAsync(con.PostAsync($"{Docs}/{doc}/discard", null));
        Assert.Equal(JsonValueKind.Null, discarded.GetProperty("pending").ValueKind);
        Assert.Equal(JsonValueKind.Null, discarded.GetProperty("checkedOutBy").ValueKind);
        Assert.Equal(3, (await OkAsync(con.GetAsync($"{Docs}/{doc}/versions"))).GetArrayLength());
        Assert.Contains("conflict.documents.check_out_first", await ErrorKeysAsync(await con.PostAsync($"{Docs}/{doc}/discard", null)));
    }

    [Fact]
    public async Task A_manager_forces_a_check_in_with_a_reason_and_the_holders_changes_are_kept_as_their_own_draft()
    {
        var o = await FreshAsync();
        var t = o.T;
        var con = t["con"].Client;
        var doc = await PublishedAsync(con, o.Library, "plan.txt", "Original plan.");
        await OkAsync(con.PostAsync($"{Docs}/{doc}/checkout", null));
        await OkAsync(con.PutAsync($"{Docs}/{doc}/working?name=plan-edit.txt", Body("Half-finished edit.")));

        // Contributors cannot force; a manager needs a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["con2"].Client.PostAsJsonAsync($"{Docs}/{doc}/force-checkin", new { reason = "away" })).StatusCode);
        Assert.Contains("validation.documents.reason", await ErrorKeysAsync(await t["lm"].Client.PostAsJsonAsync($"{Docs}/{doc}/force-checkin", new { reason = " " })));
        var released = await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Docs}/{doc}/force-checkin", new { reason = "Holder is on leave" }));
        Assert.Equal(JsonValueKind.Null, released.GetProperty("checkedOutBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, released.GetProperty("pending").ValueKind);
        Assert.Equal("1.0", Label(released, "current"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocForcedCheckIn, doc.ToString());
        Assert.Contains("conflict.documents.not_checked_out",
            await ErrorKeysAsync(await t["lm"].Client.PostAsJsonAsync($"{Docs}/{doc}/force-checkin", new { reason = "again" })));

        // The holder's edit lives on as a draft only they see, and the original is free for others.
        var draftId = await world.Database.ScalarInTenantAsync<Guid>(t.Id, 
            "select id from documents.document where tenant_id = @t and id <> @d and name = 'plan-edit.txt'", ("t", t.Id), ("d", doc));
        var draft = await OkAsync(con.GetAsync($"{Docs}/{draftId}"));
        Assert.False(draft.GetProperty("checkedIn").GetBoolean());
        Assert.Equal("Half-finished edit.", await DownloadAsync(con, draftId));
        Assert.Equal(HttpStatusCode.NotFound, (await t["con2"].Client.GetAsync($"{Docs}/{draftId}")).StatusCode);
        await OkAsync(t["con2"].Client.PostAsync($"{Docs}/{doc}/checkout", null));
    }

    [Fact]
    public async Task Contributors_compare_text_versions_and_restore_an_old_one_as_a_new_minor_version()
    {
        var o = await FreshAsync();
        var t = o.T;
        var con = t["con"].Client;
        var doc = await PublishedAsync(con, o.Library, "guide.txt", "Lift with your legs.");
        var second = await OkAsync(CheckInAsync(con, doc, "guide.txt", "Lift with your legs and keep your back straight."));
        var versions = (await OkAsync(con.GetAsync($"{Docs}/{doc}/versions"))).EnumerateArray().ToList();
        var (v11, v10) = (Id(versions[0]), Id(versions[1]));
        Assert.Equal("1.1", Label(second, "current"));

        var diff = await OkAsync(con.GetAsync($"{Docs}/{doc}/compare?from={v10}&to={v11}"));
        Assert.Equal("1.0", diff.GetProperty("from").GetString());
        Assert.Equal("1.1", diff.GetProperty("to").GetString());
        Assert.Contains(diff.GetProperty("segments").EnumerateArray(), s => s.GetProperty("op").GetString() == "added");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dee"].Client.GetAsync($"{Docs}/{doc}/compare?from={v10}&to={v11}")).StatusCode);
        // Formats that are not text are refused.
        var pdf = await PublishedAsync(con, o.Library, "form.pdf", "%PDF-1.4 form");
        var pdfVersion = Id((await OkAsync(con.GetAsync($"{Docs}/{pdf}/versions")))[0]);
        Assert.Contains("validation.documents.compare_format", await ErrorKeysAsync(await con.GetAsync($"{Docs}/{pdf}/compare?from={pdfVersion}&to={pdfVersion}")));

        // Restoring 1.0 makes 1.2 with 1.0's content; nothing earlier changes.
        Assert.Contains("validation.documents.version_unknown",
            await ErrorKeysAsync(await con.PostAsJsonAsync($"{Docs}/{doc}/restore", new { versionId = pdfVersion })));
        var restored = await OkAsync(con.PostAsJsonAsync($"{Docs}/{doc}/restore", new { versionId = v10 }));
        Assert.Equal("1.2", Label(restored, "current"));
        Assert.Equal("Lift with your legs.", await DownloadAsync(con, doc, Id(restored.GetProperty("current"))));
        Assert.Equal("Lift with your legs and keep your back straight.", await DownloadAsync(con, doc, v11));
        Assert.Equal(3, (await OkAsync(con.GetAsync($"{Docs}/{doc}/versions"))).GetArrayLength());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocRestored, doc.ToString());

        // A new file that fails the scan is refused, not stored as a version.
        await OkAsync(con.PostAsync($"{Docs}/{doc}/checkout", null));
        Assert.Contains("validation.documents.file_infected", await ErrorKeysAsync(await CheckInAsync(con, doc, "guide.txt", TestSignatureScanner.Eicar)));
        Assert.Equal(3, (await OkAsync(con.GetAsync($"{Docs}/{doc}/versions"))).GetArrayLength());
    }

    [Fact]
    public async Task Deleted_documents_wait_in_the_recycle_bin_until_restored_or_purged_after_the_tenants_period()
    {
        var o = await FreshAsync();
        var t = o.T;
        var con = t["con"].Client;
        var keep = await PublishedAsync(con, o.Library, "keep.txt", "keep me");
        var drop = await PublishedAsync(con, o.Library, "drop.txt", "drop me");
        await OkAsync(CheckInAsync(con, drop, "drop.txt", "drop me later"));

        // Readers cannot delete; contributors can.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dee"].Client.DeleteAsync($"{Docs}/{keep}")).StatusCode);
        await OkAsync(con.DeleteAsync($"{Docs}/{keep}"), HttpStatusCode.NoContent);
        await OkAsync(con.DeleteAsync($"{Docs}/{drop}"), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocDeleted, drop.ToString());
        Assert.Equal(2L, await world.Database.OutboxCountAsync("DocumentDeleted", t.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{keep}")).StatusCode);
        Assert.Empty((await OkAsync(t["dee"].Client.GetAsync($"{Libraries}/{o.Library}/items"))).GetProperty("items").EnumerateArray());

        // The deleter and the manager see the bin entries; another contributor sees none of them.
        Assert.Equal(2, (await OkAsync(con.GetAsync($"{Libraries}/{o.Library}/recycle-bin"))).GetArrayLength());
        Assert.Equal(2, (await OkAsync(t["lm"].Client.GetAsync($"{Libraries}/{o.Library}/recycle-bin"))).GetArrayLength());
        Assert.Equal(0, (await OkAsync(t["con2"].Client.GetAsync($"{Libraries}/{o.Library}/recycle-bin"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["con2"].Client.PostAsync($"{Docs}/{keep}/undelete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.PostAsync($"{Docs}/{keep}/undelete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.GetAsync($"{Libraries}/{o.Library}/recycle-bin")).StatusCode);

        var back = await OkAsync(t["lm"].Client.PostAsync($"{Docs}/{keep}/undelete", null));
        Assert.Equal("1.0", Label(back, "effective"));
        Assert.Equal("keep me", await DownloadAsync(t["dee"].Client, keep));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocRestoredFromBin, keep.ToString());

        // Nothing is purged before the period ends; after it, the document, its versions and its tokens are gone.
        var deletedAt = (DateTimeOffset)await world.Database.ScalarInTenantAsync<DateTime>(t.Id, "select deleted_at from documents.document where id = @d", ("d", drop));
        Assert.Equal(0, await MaintainAsync(t, deletedAt.AddDays(TeamBeats.Modules.Documents.Libraries.DefaultRecycleDays - 1)));
        Assert.Equal(1, await MaintainAsync(t, deletedAt.AddDays(TeamBeats.Modules.Documents.Libraries.DefaultRecycleDays + 1)));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document where id = @d", ("d", drop)));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document_version where document_id = @d", ("d", drop)));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocPurged, drop.ToString());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document where id = @d", ("d", keep)));

        // The period is the tenant's to set, within 30 to 180 days.
        Assert.Contains("validation.documents.recycle_days",
            await ErrorKeysAsync(await t["admin"].Client.PutAsJsonAsync($"{Docs}/settings", new { allowedExtensions = (string[]?)null, recycleDays = 10 })));
        var settings = await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Docs}/settings", new { allowedExtensions = (string[]?)null, recycleDays = 30 }));
        Assert.Equal(30, settings.GetProperty("recycleDays").GetInt32());
    }

    [Fact]
    public async Task Long_check_outs_remind_the_holder_and_owner_once_a_day_and_other_tenants_reach_nothing()
    {
        var o = await FreshAsync();
        var t = o.T;
        var doc = await PublishedAsync(t["con"].Client, o.Library, "rota.txt", "Week 1");
        await OkAsync(t["con2"].Client.PostAsync($"{Docs}/{doc}/checkout", null));
        var since = (DateTimeOffset)await world.Database.ScalarInTenantAsync<DateTime>(t.Id, "select checked_out_at from documents.document where id = @d", ("d", doc));

        Assert.Equal(0, await MaintainAsync(t, since.AddDays(13)));
        Assert.Equal(1, await MaintainAsync(t, since.AddDays(15)));
        Assert.Equal(0, await MaintainAsync(t, since.AddDays(15).AddHours(2)));
        foreach (var name in new[] { "con", "con2" })
        {
            Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
                "select count(*) from notifications.notification where user_id = @u and template_key = 'documents.checkout_reminder'", ("u", t[name].Id)));
        }
        Assert.Equal(1, await MaintainAsync(t, since.AddDays(16).AddHours(1)));

        // Another tenant gets not found for every action on this document and library.
        var other = await world.CreateTenantAsync("dv-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var admin = other["admin"].Client;
        foreach (var call in new[]
        {
            admin.PostAsync($"{Docs}/{doc}/checkout", null),
            admin.GetAsync($"{Docs}/{doc}/versions"),
            admin.PostAsJsonAsync($"{Docs}/{doc}/force-checkin", new { reason = "x" }),
            admin.DeleteAsync($"{Docs}/{doc}"),
            admin.PostAsync($"{Docs}/{doc}/undelete", null),
            admin.GetAsync($"{Libraries}/{o.Library}/recycle-bin"),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await call).StatusCode);
        }
        // Their maintenance run leaves this tenant alone.
        Assert.Equal(0, await MaintainAsync(other, since.AddDays(400)));
        Assert.Equal(t["con2"].Id, (await OkAsync(t["con"].Client.GetAsync($"{Docs}/{doc}"))).GetProperty("checkedOutBy").GetGuid());
    }
}
