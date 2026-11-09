using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 T009 and T010 (US3, FR-007, FR-008): numbering; the release route with inbox tasks and no self-approval;
/// the next major version effective at once or on its date with the notice before it; the superseded banner; read
/// confirmation on each major version; the owner's review task with confirm and withdraw; the controlled copy footer;
/// refusal without <c>docs.controlled</c>; other tenants get not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentControlledTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private sealed record Org(TestTenant T, Guid Library, Guid Type);

    private async Task<Org> FreshAsync(Func<TestTenant, object[]>? steps = null, bool confirm = true)
    {
        var t = await world.CreateTenantAsync("dc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "lm", "hs", "pm", "qa", "dee", "cai" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "hs", "pm", "qa", "dee" })
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
        await world.AssignAsync(t, "hs", "doc_contributor", "org_unit", dubai);
        var library = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Safety", ["ar"] = "السلامة" }, scope = "org_unit", scopeId = dubai,
        }), HttpStatusCode.Created));
        var type = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "Procedure", ["ar"] = "إجراء" },
            fields = Array.Empty<object>(),
            controlled = new
            {
                numberPrefix = "hs-sop-", numberDigits = 4, readConfirmation = confirm, reviewMonths = 12,
                steps = steps?.Invoke(t) ?? [new { approvers = new[] { t["pm"].Id } }, new { approvers = new[] { t["qa"].Id } }],
            },
        }), HttpStatusCode.Created));
        return new Org(t, library, type);
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

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Org o, string name, string content, bool publish = false) =>
        client.PostAsync($"{Libraries}/{o.Library}/files?name={Uri.EscapeDataString(name)}&type={o.Type}&publish={(publish ? "true" : "false")}", Body(content));

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(t => t.GetProperty("source").GetString()!.StartsWith("document_", StringComparison.Ordinal)).ToArray();

    private static async Task DecideAsync(HttpClient client, string outcome, string? comment = null)
    {
        var task = Assert.Single(await InboxAsync(client)).GetProperty("taskId").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment }));
    }

    private static async Task<string> DownloadAsync(HttpClient client, Guid document)
    {
        var token = await OkAsync(client.GetAsync($"{Docs}/{document}/download-token"));
        return await (await client.GetAsync(token.GetProperty("url").GetString())).Content.ReadAsStringAsync();
    }

    /// <summary>Dispatches the shared outbox until the condition holds; other tests' messages may come first.</summary>
    private async Task DispatchUntilAsync(Func<Task<bool>> done)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 50 && !await done(); i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
        Assert.True(await done());
    }

    private Task<int> RunDueAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<DocumentReleases>().RunDueAsync(now, 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    [Fact]
    public async Task A_release_runs_its_route_in_the_inbox_and_the_next_major_version_takes_effect_then_supersedes_the_last()
    {
        var o = await FreshAsync();
        var t = o.T;
        var hs = t["hs"].Client;
        Assert.Contains("conflict.documents.controlled_release", await ErrorKeysAsync(await UploadAsync(hs, o, "height.txt", "v1", publish: true)));
        var doc = await OkAsync(UploadAsync(hs, o, "height.txt", "Use a harness."), HttpStatusCode.Created);
        var id = Id(doc);
        Assert.Equal("HS-SOP-0001", doc.GetProperty("number").GetString());
        Assert.Equal("0.1", Label(doc, "current"));
        Assert.Contains("conflict.documents.controlled_release", await ErrorKeysAsync(await hs.PostAsync($"{Docs}/{id}/checkin?publish=true&name=height.txt", Body("x"))));
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{id}")).StatusCode);

        var submitted = await OkAsync(hs.PostAsJsonAsync($"{Docs}/{id}/release", new { note = "First issue" }));
        Assert.Equal("in_approval", submitted.GetProperty("controlled").GetProperty("release").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocReleaseSubmitted, id.ToString());
        Assert.Contains("conflict.documents.in_release", await ErrorKeysAsync(await hs.PostAsync($"{Docs}/{id}/checkout", null)));
        Assert.Contains("conflict.documents.in_release", await ErrorKeysAsync(await hs.PostAsJsonAsync($"{Docs}/{id}/release", new { })));

        // Step one is the plant manager's, step two Quality's; the author never gets a task.
        Assert.Empty(await InboxAsync(hs));
        Assert.Empty(await InboxAsync(t["qa"].Client));
        var task = Assert.Single(await InboxAsync(t["pm"].Client));
        Assert.Equal("document_release", task.GetProperty("source").GetString());
        Assert.Equal($"/documents/{id}", task.GetProperty("link").GetString());
        Assert.Equal("HS-SOP-0001 height.txt", task.GetProperty("title").GetProperty("en").GetString());
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "pm", DocumentReleases.ReleaseWaiting) == 1);
        await DecideAsync(t["pm"].Client, "approve");
        Assert.Empty(await InboxAsync(t["pm"].Client));
        await DecideAsync(t["qa"].Client, "approve");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocReleaseApproved, id.ToString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocVersionEffective, id.ToString());

        // 1.0 is in force for readers, and they are asked to confirm they read it.
        var read = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{id}"));
        Assert.Equal("1.0", Label(read, "effective"));
        Assert.True(read.GetProperty("controlled").GetProperty("readConfirmationRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("controlled").GetProperty("readConfirmedAt").ValueKind);
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "dee", DocumentReadConfirmations.Requested) == 1);
        Assert.Equal(0L, await NotificationsAsync(t, "cai", DocumentReadConfirmations.Requested));
        var confirmed = await OkAsync(t["dee"].Client.PostAsync($"{Docs}/{id}/read-confirmation", null));
        Assert.NotEqual(JsonValueKind.Null, confirmed.GetProperty("controlled").GetProperty("readConfirmedAt").ValueKind);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocReadConfirmed, id.ToString());
        var report = await OkAsync(t["lm"].Client.GetAsync($"{Docs}/{id}/read-confirmations"));
        Assert.Equal(1, report.GetProperty("confirmed").GetInt32());
        Assert.True(report.GetProperty("requested").GetInt32() >= 4);
        Assert.DoesNotContain(report.GetProperty("pending").EnumerateArray(), p => p.GetGuid() == t["dee"].Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await hs.GetAsync($"{Docs}/{id}/read-confirmations")).StatusCode);

        // The next revision is approved for a date a week ahead: readers keep 1.0 and see the notice until then.
        await OkAsync(hs.PostAsync($"{Docs}/{id}/checkout", null));
        await OkAsync(hs.PostAsync($"{Docs}/{id}/checkin?name=height.txt", Body("Use a harness and a helmet.")));
        var on = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        Assert.Contains("validation.documents.effective_on",
            await ErrorKeysAsync(await hs.PostAsJsonAsync($"{Docs}/{id}/release", new { effectiveOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3)) })));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{id}/release", new { effectiveOn = on }));
        await DecideAsync(t["pm"].Client, "approve");
        await DecideAsync(t["qa"].Client, "approve");
        var waiting = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{id}"));
        Assert.Equal("1.0", Label(waiting, "effective"));
        Assert.Equal("scheduled", waiting.GetProperty("controlled").GetProperty("release").GetString());
        Assert.Equal("2.0", waiting.GetProperty("controlled").GetProperty("upcoming").GetProperty("label").GetString());
        Assert.Equal("Use a harness.", await DownloadAsync(t["dee"].Client, id));

        var upcoming = waiting.GetProperty("controlled").GetProperty("upcoming");
        var from = upcoming.GetProperty("id").GetGuid();
        var at = (DateTimeOffset)(await world.Database.ScalarInTenantAsync<DateTime>(t.Id, "select effective_from from documents.document_version where id = @v", ("v", from)));
        Assert.Equal(0, await RunDueAsync(t, at.AddMinutes(-1)));
        Assert.True(await RunDueAsync(t, at.AddMinutes(1)) >= 1);
        var now = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{id}"));
        Assert.Equal("2.0", Label(now, "effective"));
        Assert.Equal("Use a harness and a helmet.", await DownloadAsync(t["dee"].Client, id));
        // 2.0 asks again; the reader's confirmation of 1.0 does not count for it.
        Assert.Equal(JsonValueKind.Null, now.GetProperty("controlled").GetProperty("readConfirmedAt").ValueKind);

        // An old link to 1.0 shows the superseded banner with the version in force.
        var old = (await OkAsync(hs.GetAsync($"{Docs}/{id}/versions"))).EnumerateArray().Single(v => v.GetProperty("label").GetString() == "1.0");
        var banner = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{id}/versions/{Id(old)}"));
        Assert.True(banner.GetProperty("superseded").GetBoolean());
        Assert.Equal("2.0", banner.GetProperty("effective").GetProperty("label").GetString());
        Assert.Equal("HS-SOP-0001", banner.GetProperty("number").GetString());
        await OkAsync(hs.PostAsync($"{Docs}/{id}/checkin?name=height.txt", Body("Draft for 3.0")));
        var draft = (await OkAsync(hs.GetAsync($"{Docs}/{id}/versions"))).EnumerateArray().First(v => v.GetProperty("state").GetString() == "draft");
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{id}/versions/{Id(draft)}")).StatusCode);

        // The next document gets the next number.
        Assert.Equal("HS-SOP-0002", (await OkAsync(UploadAsync(hs, o, "ladders.txt", "Ladders."), HttpStatusCode.Created)).GetProperty("number").GetString());
    }

    [Fact]
    public async Task Authors_never_approve_their_own_revision_and_a_returned_or_withdrawn_release_is_a_draft_again()
    {
        var o = await FreshAsync(t => [new { approvers = new[] { t["hs"].Id, t["pm"].Id } }]);
        var t = o.T;
        var hs = t["hs"].Client;
        var id = Id(await OkAsync(UploadAsync(hs, o, "scaffold.txt", "Scaffold rules."), HttpStatusCode.Created));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{id}/release", new { }));
        // The author is a named approver but gets no task; the other approver decides.
        Assert.Empty(await InboxAsync(hs));
        await DecideAsync(t["pm"].Client, "return", "Add the inspection interval");
        var back = await OkAsync(hs.GetAsync($"{Docs}/{id}"));
        Assert.Equal("none", back.GetProperty("controlled").GetProperty("release").GetString());
        Assert.Equal("draft", back.GetProperty("current").GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocReleaseReturned, id.ToString());
        Assert.Equal(1L, await NotificationsAsync(t, "hs", DocumentReleases.ReleaseReturned));

        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{id}/release", new { }));
        // Readers do not see a document before its first release.
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.PostAsync($"{Docs}/{id}/release/withdraw", null)).StatusCode);
        var withdrawn = await OkAsync(hs.PostAsync($"{Docs}/{id}/release/withdraw", null));
        Assert.Equal("none", withdrawn.GetProperty("controlled").GetProperty("release").GetString());
        Assert.Empty(await InboxAsync(t["pm"].Client));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocReleaseWithdrawn, id.ToString());
        Assert.Contains("conflict.documents.no_release", await ErrorKeysAsync(await hs.PostAsync($"{Docs}/{id}/release/withdraw", null)));

        // A library managers step works through doc.manage at the library's unit.
        var settings = new { numberPrefix = "QA-", readConfirmation = false, steps = new[] { new { libraryManagers = true } } };
        var type = Id(await OkAsync(t["lm"].Client.PostAsJsonAsync($"{Libraries}/{o.Library}/types",
            new { names = new Dictionary<string, string> { ["en"] = "Form" }, fields = Array.Empty<object>(), controlled = settings }), HttpStatusCode.Created));
        var form = Id(await OkAsync(hs.PostAsync($"{Libraries}/{o.Library}/files?name=form.txt&type={type}", Body("Form A")), HttpStatusCode.Created));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{form}/release", new { }));
        await DecideAsync(t["lm"].Client, "approve");
        Assert.Equal("1.0", Label(await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{form}")), "effective"));
    }

    [Fact]
    public async Task The_owner_gets_a_review_task_before_the_review_date_and_can_confirm_or_withdraw_the_document()
    {
        var o = await FreshAsync(t => [new { approvers = new[] { t["pm"].Id } }], confirm: false);
        var t = o.T;
        var hs = t["hs"].Client;
        var id = Id(await OkAsync(UploadAsync(hs, o, "fire.txt", "Fire drill."), HttpStatusCode.Created));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{id}/release", new { }));
        await DecideAsync(t["pm"].Client, "approve");
        var due = (await OkAsync(hs.GetAsync($"{Docs}/{id}"))).GetProperty("controlled").GetProperty("reviewDueAt").GetDateTimeOffset();
        Assert.True(due > DateTimeOffset.UtcNow.AddMonths(11));

        Assert.Equal(0, await RunDueAsync(t, due.AddDays(-31)));
        Assert.Equal(1, await RunDueAsync(t, due.AddDays(-29)));
        Assert.Equal(1L, await NotificationsAsync(t, "hs", DocumentReleases.ReviewWaiting));
        var task = Assert.Single(await InboxAsync(hs));
        Assert.Equal("document_review", task.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["lm"].Client.PostAsJsonAsync($"{Docs}/{id}/review", new { action = "confirm" })).StatusCode);
        Assert.Contains("validation.documents.review_action", await ErrorKeysAsync(await hs.PostAsJsonAsync($"{Docs}/{id}/review", new { action = "keep" })));
        var next = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6));
        var reviewed = await OkAsync(hs.PostAsJsonAsync($"{Docs}/{id}/review", new { action = "confirm", nextReviewOn = next }));
        Assert.True(reviewed.GetProperty("controlled").GetProperty("reviewDueAt").GetDateTimeOffset() < due);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocReviewed, id.ToString());
        Assert.Empty(await InboxAsync(hs));

        // Left past the date, the review is overdue and the owner and the library managers hear it once.
        var due2 = reviewed.GetProperty("controlled").GetProperty("reviewDueAt").GetDateTimeOffset();
        Assert.True(await RunDueAsync(t, due2.AddDays(1)) >= 2);
        Assert.Equal(0, await RunDueAsync(t, due2.AddDays(2)));
        Assert.Equal(1L, await NotificationsAsync(t, "hs", DocumentReleases.ReviewOverdue));
        Assert.Equal(1L, await NotificationsAsync(t, "admin", DocumentReleases.ReviewOverdue));
        var list = await OkAsync(t["lm"].Client.GetAsync($"{Libraries}/{o.Library}/reviews"));
        Assert.True(Assert.Single(list.EnumerateArray()).GetProperty("taskOpen").GetBoolean());

        // Withdrawn through the inbox: readers no longer get it, contributors keep the history.
        await DecideAsync(hs, "reject", "Replaced by the new emergency plan");
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{id}")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await OkAsync(hs.GetAsync($"{Docs}/{id}"))).GetProperty("effective").ValueKind);
        Assert.Equal(1L, await world.Database.OutboxCountAsync("DocumentWithdrawn", t.Id));
    }

    [Fact]
    public async Task A_controlled_copy_carries_the_number_version_effective_date_and_uncontrolled_when_printed()
    {
        var o = await FreshAsync(t => [new { approvers = new[] { t["pm"].Id } }], confirm: false);
        var t = o.T;
        var hs = t["hs"].Client;
        var pdf = Id(await OkAsync(UploadAsync(hs, o, "permit.pdf", "%PDF-1.4 permit"), HttpStatusCode.Created));
        Assert.Contains("conflict.documents.not_released", await ErrorKeysAsync(await hs.GetAsync($"{Docs}/{pdf}/controlled-copy")));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{pdf}/release", new { }));
        await DecideAsync(t["pm"].Client, "approve");

        var before = world.Factory.Gotenberg.Stamps;
        var token = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{pdf}/controlled-copy"));
        var file = await t["dee"].Client.GetAsync(token.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        var text = await file.Content.ReadAsStringAsync();
        Assert.StartsWith("%PDF-1.4 permit", text);
        Assert.Matches(@"% stamped: HS-SOP-0001 \| Version 1\.0 \| Effective \d{4}-\d{2}-\d{2} \| Uncontrolled when printed$", text);
        Assert.Equal("HS-SOP-0001 1.0.pdf", file.Content.Headers.ContentDisposition!.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        // Made once per version; another person, and only that person, opens their own token.
        await OkAsync(hs.GetAsync($"{Docs}/{pdf}/controlled-copy"));
        Assert.Equal(before + 1, world.Factory.Gotenberg.Stamps);
        Assert.Equal(HttpStatusCode.NotFound, (await hs.GetAsync(token.GetProperty("url").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hs.GetAsync(token.GetProperty("url").GetString()!.Replace("/controlled/", "/files/"))).StatusCode);

        // Office files are converted first; files that are not documents have no controlled copy.
        var docx = Id(await OkAsync(UploadAsync(hs, o, "plan.docx", "word body"), HttpStatusCode.Created));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{docx}/release", new { }));
        await DecideAsync(t["pm"].Client, "approve");
        var office = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{docx}/controlled-copy"));
        Assert.Contains("% stamped: HS-SOP-0002 | Version 1.0", await (await t["dee"].Client.GetAsync(office.GetProperty("url").GetString())).Content.ReadAsStringAsync());
        var txt = Id(await OkAsync(UploadAsync(hs, o, "notes.txt", "notes"), HttpStatusCode.Created));
        await OkAsync(hs.PostAsJsonAsync($"{Docs}/{txt}/release", new { }));
        await DecideAsync(t["pm"].Client, "approve");
        Assert.Contains("conflict.documents.no_pdf", await ErrorKeysAsync(await hs.GetAsync($"{Docs}/{txt}/controlled-copy")));
    }

    [Fact]
    public async Task Controlled_types_need_the_capability_and_other_tenants_reach_nothing()
    {
        var plain = await world.CreateTenantAsync("dc-w-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var library = Id(await OkAsync(plain["admin"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Files" }, scope = "tenant",
        }), HttpStatusCode.Created));
        var refused = await plain["admin"].Client.PostAsJsonAsync($"{Libraries}/{library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "SOP" }, fields = Array.Empty<object>(),
            controlled = new { numberPrefix = "SOP-", steps = new[] { new { libraryManagers = true } } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement", await refused.Content.ReadAsStringAsync());
        await OkAsync(plain["admin"].Client.PostAsJsonAsync($"{Libraries}/{library}/types",
            new { names = new Dictionary<string, string> { ["en"] = "Note" }, fields = Array.Empty<object>() }), HttpStatusCode.Created);

        var o = await FreshAsync(t => [new { approvers = new[] { t["pm"].Id } }]);
        var t = o.T;
        // Settings are validated: prefix, steps, approvers who are members here.
        var bad = await t["lm"].Client.PostAsJsonAsync($"{Libraries}/{o.Library}/types", new
        {
            names = new Dictionary<string, string> { ["en"] = "Bad" }, fields = Array.Empty<object>(),
            controlled = new { numberPrefix = "bad prefix!", numberDigits = 12, steps = new[] { new { approvers = new[] { plain["admin"].Id } } } },
        });
        var keys = await ErrorKeysAsync(bad);
        Assert.Contains("validation.documents.number_prefix", keys);
        Assert.Contains("validation.documents.number_digits", keys);
        Assert.Contains("validation.documents.approver", keys);

        var id = Id(await OkAsync(UploadAsync(t["hs"].Client, o, "lockout.txt", "Lockout."), HttpStatusCode.Created));
        await OkAsync(t["hs"].Client.PostAsJsonAsync($"{Docs}/{id}/release", new { }));
        await DecideAsync(t["pm"].Client, "approve");
        var version = Id(Assert.Single((await OkAsync(t["hs"].Client.GetAsync($"{Docs}/{id}/versions"))).EnumerateArray()));
        var admin = plain["admin"].Client;
        foreach (var call in new[]
        {
            admin.PostAsJsonAsync($"{Docs}/{id}/release", new { }),
            admin.PostAsync($"{Docs}/{id}/release/withdraw", null),
            admin.PostAsJsonAsync($"{Docs}/{id}/review", new { action = "confirm" }),
            admin.PostAsync($"{Docs}/{id}/read-confirmation", null),
            admin.GetAsync($"{Docs}/{id}/read-confirmations"),
            admin.GetAsync($"{Docs}/{id}/versions/{version}"),
            admin.GetAsync($"{Docs}/{id}/controlled-copy"),
            admin.GetAsync($"{Libraries}/{o.Library}/reviews"),
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await call).StatusCode);
        }
        // Another unit in the same tenant is not part of the audience either.
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.PostAsync($"{Docs}/{id}/read-confirmation", null)).StatusCode);
    }
}
