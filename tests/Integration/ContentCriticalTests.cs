using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 slice 3 (US2, FR-006, FR-007): critical announcements as a modal or a banner; confirmation recorded with
/// version, channel and a hash of the text shown; "remind me later" up to the allowed count; material changes reset
/// confirmations and minor ones keep them; <c>content.publish_critical</c> is required and a refusal is audited; the
/// audience follows membership; the deadline reminds people and their managers; the completion report is built in the
/// background and audited. Tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentCriticalTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Pending = "/api/v1/content/critical/pending";

    private sealed record Org(TestTenant T, Guid PlantA, Guid PlantB, Guid Cairo);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("cc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var plantA = await world.CreateUnitAsync(t, "PlantA", holding, "department");
        var plantB = await world.CreateUnitAsync(t, "PlantB", holding, "department");
        var cairo = await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "hs", "pia", "ali", "bea", "omar", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "ali", "PlantA");
        await world.AddMembershipAsync(t, "bea", "PlantB");
        await world.AddMembershipAsync(t, "omar", "Cairo");
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "safety_lead", names = new Dictionary<string, string> { ["en"] = "Safety lead" },
            permissions = new[]
            {
                Permissions.ContentView, Permissions.ContentAuthor, Permissions.ContentEdit, Permissions.ContentPublish, Permissions.ContentPublishCritical,
            },
            delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "hs", "safety_lead", "tenant", null);
        await DispatchAsync();
        return new Org(t, plantA, plantB, cairo);
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static object Variants(string en, string? ar = null) => ar is null
        ? new Dictionary<string, object> { ["en"] = new { title = en, body = Body(en + " body") } }
        : new Dictionary<string, object> { ["en"] = new { title = en, body = Body(en + " body") }, ["ar"] = new { title = ar, body = Body(ar + " body") } };

    private static async Task<Guid> CreateAsync(HttpClient client, string title, object critical, object? audience = null, string type = "announcement")
    {
        var response = await client.PostAsJsonAsync(Items, new { type, variants = Variants(title), audience, critical });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static object Plants(params Guid[] units) => new { anyOf = new[] { new { orgUnits = units.Select(u => new { id = u }).ToArray() } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string?> DetailKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : problem.GetProperty("code").GetString();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors").EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(k => k.GetProperty("key").GetString()!)).ToArray();
    }

    private async Task PublishAsync(HttpClient client, Guid id, bool material = true)
    {
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"{Items}/{id}/publish", new { material })).StatusCode);
        await DispatchAsync();
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static async Task<(JsonElement? Modal, JsonElement[] Banners)> PendingAsync(HttpClient client)
    {
        var pending = await OkAsync(client.GetAsync(Pending));
        var modal = pending.GetProperty("modal");
        return (modal.ValueKind == JsonValueKind.Null ? null : modal, pending.GetProperty("banners").EnumerateArray().ToArray());
    }

    private static async Task<Guid?> ModalAsync(HttpClient client) => (await PendingAsync(client)).Modal?.GetProperty("id").GetGuid();

    private Task<int> RunScheduleAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    private static DateTime Local(DateTimeOffset utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai")), DateTimeKind.Unspecified);

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    [Fact]
    public async Task A_modal_shows_to_its_audience_until_confirmed_and_records_version_channel_and_text_hash()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var id = await CreateAsync(hs, "New evacuation procedure", new { presentation = "modal" }, Plants(o.PlantA, o.PlantB));
        var created = await OkAsync(hs.GetAsync($"{Items}/{id}"));
        Assert.Equal("modal", created.GetProperty("critical").GetProperty("presentation").GetString());
        Assert.True(created.GetProperty("critical").GetProperty("requireAck").GetBoolean());
        Assert.Equal(3, created.GetProperty("critical").GetProperty("remindLaterMax").GetInt32());
        Assert.Null(await ModalAsync(o.T["ali"].Client)); // Not live yet.

        await PublishAsync(hs, id);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCriticalSent, id.ToString());
        var ali = o.T["ali"].Client;
        Assert.Equal(id, await ModalAsync(ali));
        Assert.Equal(id, await ModalAsync(o.T["bea"].Client));
        Assert.Null(await ModalAsync(o.T["omar"].Client)); // Cairo is not targeted.
        Assert.Contains(CriticalAnnouncements.ReadLink(id), await NotificationLinksAsync(ali));

        // Omar may read the item by link, but there is nothing to confirm for him to be counted.
        var ack = await OkAsync(ali.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { locale = "en", channel = "mobile" }));
        Assert.Equal(1, ack.GetProperty("version").GetInt32());
        Assert.Equal("mobile", ack.GetProperty("channel").GetString());
        Assert.Null(await ModalAsync(ali));
        Assert.Equal(id, await ModalAsync(o.T["bea"].Client));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentAcknowledged, id.ToString());

        // A second confirmation returns the first and records nothing new.
        var again = await OkAsync(ali.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { locale = "en", channel = "web" }));
        Assert.Equal("mobile", again.GetProperty("channel").GetString());
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("New evacuation procedure\n\nNew evacuation procedure body")));
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from content.acknowledgement where item_id = @id and user_id = @u and version_number = 1 and channel = 'mobile' and text_hash = @h",
            ("id", id), ("u", o.T["ali"].Id), ("h", expected)));

        // Confirmations are insert only for the application (FR-007).
        Assert.Equal(0, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from information_schema.role_table_grants where table_schema = 'content' and table_name = 'acknowledgement' and grantee = 'tb_app' and privilege_type in ('UPDATE', 'DELETE')"));

        var summary = await OkAsync(hs.GetAsync($"{Items}/{id}/acknowledgements"));
        Assert.Equal(2, summary.GetProperty("audience").GetInt32());
        Assert.Equal(1, summary.GetProperty("confirmed").GetInt32());
        Assert.Equal(1, summary.GetProperty("byChannel").GetProperty("mobile").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["pia"].Client.GetAsync($"{Items}/{id}/acknowledgements")).StatusCode);
    }

    [Fact]
    public async Task Remind_me_later_puts_the_modal_off_up_to_the_allowed_count()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var id = await CreateAsync(hs, "Badge rules", new { presentation = "modal", remindLaterMax = 2 }, Plants(o.PlantA));
        await PublishAsync(hs, id);
        var ali = o.T["ali"].Client;

        var first = await OkAsync(ali.PostAsync($"{Items}/{id}/remind-later", null));
        Assert.Equal(1, first.GetProperty("remindLaterLeft").GetInt32());
        Assert.Null(await ModalAsync(ali));

        // Hours later it shows again, with one put-off left.
        await world.Database.OwnerExecInTenantAsync(o.T.Id, "update content.audience_member set remind_until = now() - interval '1 minute' where item_id = @id",
            ("id", id));
        var shown = (await PendingAsync(ali)).Modal!.Value;
        Assert.Equal(1, shown.GetProperty("remindLaterLeft").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await ali.PostAsync($"{Items}/{id}/remind-later", null)).StatusCode);
        await world.Database.OwnerExecInTenantAsync(o.T.Id, "update content.audience_member set remind_until = now() - interval '1 minute' where item_id = @id",
            ("id", id));
        Assert.Equal("conflict.content.remind_limit", await DetailKeyAsync(await ali.PostAsync($"{Items}/{id}/remind-later", null), HttpStatusCode.Conflict));
        Assert.Equal(id, await ModalAsync(ali));

        // Someone outside the audience has nothing to put off.
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["omar"].Client.PostAsync($"{Items}/{id}/remind-later", null)).StatusCode);
    }

    [Fact]
    public async Task A_material_change_asks_again_and_a_minor_correction_keeps_confirmations()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var id = await CreateAsync(hs, "Shift times", new { presentation = "modal" }, Plants(o.PlantA));
        await PublishAsync(hs, id);
        var ali = o.T["ali"].Client;
        await OkAsync(ali.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { channel = "web" }));
        Assert.Null(await ModalAsync(ali));

        Assert.Equal(HttpStatusCode.OK, (await hs.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Shift times (typo fixed)") })).StatusCode);
        await PublishAsync(hs, id, material: false);
        Assert.Null(await ModalAsync(ali));
        Assert.Equal(1, (await OkAsync(hs.GetAsync($"{Items}/{id}/acknowledgements"))).GetProperty("confirmed").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await hs.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Shift times changed") })).StatusCode);
        await PublishAsync(hs, id, material: true);
        var modal = (await PendingAsync(ali)).Modal!.Value;
        Assert.Equal(3, modal.GetProperty("version").GetInt32());
        Assert.Equal("Shift times changed", modal.GetProperty("title").GetString());
        var summary = await OkAsync(hs.GetAsync($"{Items}/{id}/acknowledgements"));
        Assert.Equal(3, summary.GetProperty("countedFrom").GetInt32());
        Assert.Equal(0, summary.GetProperty("confirmed").GetInt32());
        // The earlier confirmation stays on record.
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from content.acknowledgement where item_id = @id", ("id", id)));
    }

    [Fact]
    public async Task Sending_critical_needs_publish_critical_and_a_refusal_is_audited()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var id = await CreateAsync(pia, "Fake alarm", new { presentation = "modal" });
        Assert.Equal(HttpStatusCode.Forbidden, (await pia.PostAsJsonAsync($"{Items}/{id}/publish", new { })).StatusCode);
        Assert.Equal("draft", (await OkAsync(pia.GetAsync($"{Items}/{id}"))).GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id and outcome = 'denied'",
            ("a", AuditActions.ContentCriticalDenied), ("id", id.ToString())));

        // Holding the permission only at a plant's unit does not reach an audience past the site (section 6).
        var role = await o.T["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "plant_lead", names = new Dictionary<string, string> { ["en"] = "Plant lead" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentPublishCritical }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(o.T, "pia", "plant_lead", "org_subtree", o.PlantA);
        Assert.Equal(HttpStatusCode.Forbidden, (await pia.PostAsJsonAsync($"{Items}/{id}/publish", new { })).StatusCode);

        // Clearing the critical settings makes it an ordinary announcement she may publish.
        Assert.Equal(HttpStatusCode.OK, (await pia.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Drill on Monday"), critical = new { presentation = "none" } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pia.PostAsJsonAsync($"{Items}/{id}/publish", new { })).StatusCode);
    }

    [Fact]
    public async Task Critical_settings_are_validated()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        Assert.Contains("validation.content.critical_type", await ErrorKeysAsync(await hs.PostAsJsonAsync(Items,
            new { type = "news", variants = Variants("x"), critical = new { presentation = "modal" } })));
        Assert.Contains("validation.content.critical_presentation", await ErrorKeysAsync(await hs.PostAsJsonAsync(Items,
            new { type = "announcement", variants = Variants("x"), critical = new { presentation = "popup" } })));
        Assert.Contains("validation.content.critical_remind", await ErrorKeysAsync(await hs.PostAsJsonAsync(Items,
            new { type = "announcement", variants = Variants("x"), critical = new { presentation = "modal", remindLaterMax = 11 } })));
        Assert.Contains("validation.content.critical_deadline", await ErrorKeysAsync(await hs.PostAsJsonAsync(Items,
            new { type = "announcement", variants = Variants("x"), critical = new { presentation = "modal", deadline = Local(DateTimeOffset.UtcNow.AddHours(-1)) } })));
    }

    [Fact]
    public async Task A_banner_shows_in_the_readers_language_until_dismissed_when_dismissible()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var response = await hs.PostAsJsonAsync(Items, new
        {
            type = "announcement", variants = Variants("Road closed", "الطريق مغلق"), critical = new { presentation = "banner", dismissible = true },
        });
        var id = (await response.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var fixedId = await CreateAsync(hs, "Visitors sign in", new { presentation = "banner" });
        await PublishAsync(hs, id);
        await PublishAsync(hs, fixedId);

        var omar = o.T["omar"].Client;
        var (modal, banners) = await PendingAsync(omar);
        Assert.Null(modal);
        Assert.Equal([id, fixedId], banners.Select(b => b.GetProperty("id").GetGuid()).ToArray());
        Assert.False(banners[0].GetProperty("requireAck").GetBoolean());

        (await omar.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var inArabic = (await PendingAsync(omar)).Banners;
        Assert.Equal("الطريق مغلق", inArabic[0].GetProperty("title").GetString());
        Assert.Equal("ar", inArabic[0].GetProperty("locale").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await omar.PostAsync($"{Items}/{id}/dismiss", null)).StatusCode);
        Assert.Equal([fixedId], (await PendingAsync(omar)).Banners.Select(b => b.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal("conflict.content.not_dismissible", await DetailKeyAsync(await omar.PostAsync($"{Items}/{fixedId}/dismiss", null), HttpStatusCode.Conflict));
        Assert.Equal("conflict.content.no_acknowledgement",
            await DetailKeyAsync(await omar.PostAsJsonAsync($"{Items}/{fixedId}/acknowledge", new { }), HttpStatusCode.Conflict));

        // It goes once the item is taken down.
        Assert.Equal(HttpStatusCode.OK, (await hs.PostAsync($"{Items}/{fixedId}/unpublish", null)).StatusCode);
        Assert.Empty((await PendingAsync(omar)).Banners);
    }

    [Fact]
    public async Task The_audience_follows_membership_and_keeps_confirmations()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var id = await CreateAsync(hs, "Plant A notice", new { presentation = "modal" }, Plants(o.PlantA));
        await PublishAsync(hs, id);
        var omar = o.T["omar"].Client;
        Assert.Null(await ModalAsync(omar));

        await world.AddMembershipAsync(o.T, "omar", "PlantA", "secondary");
        await DispatchAsync();
        Assert.Equal(id, await ModalAsync(omar));
        await OkAsync(omar.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { channel = "web" }));
        Assert.Equal(2, (await OkAsync(hs.GetAsync($"{Items}/{id}/acknowledgements"))).GetProperty("audience").GetInt32());

        // Ali leaves Plant A: he drops out of the count; Omar's confirmation stays.
        var membership = (await OkAsync(o.T["admin"].Client.GetAsync($"/api/v1/org/units/{o.PlantA}/memberships"))).EnumerateArray()
            .First(m => m.GetProperty("userId").GetGuid() == o.T["ali"].Id).GetProperty("id").GetGuid();
        Assert.True((await o.T["admin"].Client.PostAsync($"/api/v1/org/memberships/{membership}:end", null)).IsSuccessStatusCode);
        await DispatchAsync();
        Assert.Null(await ModalAsync(o.T["ali"].Client));
        var summary = await OkAsync(hs.GetAsync($"{Items}/{id}/acknowledgements"));
        Assert.Equal(1, summary.GetProperty("audience").GetInt32());
        Assert.Equal(1, summary.GetProperty("confirmed").GetInt32());
    }

    [Fact]
    public async Task After_the_deadline_people_and_their_managers_are_reminded_and_the_report_is_built_in_the_background()
    {
        var o = await FreshAsync();
        foreach (var person in new[] { "ali", "bea" })
        {
            Assert.Equal(HttpStatusCode.OK, (await o.T["admin"].Client.PutAsJsonAsync($"/api/v1/people/{o.T[person].Id}/manager",
                new { managerId = o.T["mona"].Id })).StatusCode);
        }
        var hs = o.T["hs"].Client;
        var deadline = DateTimeOffset.UtcNow.AddDays(5);
        var id = await CreateAsync(hs, "Evacuation drill", new { presentation = "modal", deadline = Local(deadline) }, Plants(o.PlantA, o.PlantB));
        await PublishAsync(hs, id);
        await OkAsync(o.T["ali"].Client.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { channel = "web" }));

        Assert.Equal(0, await RunScheduleAsync(o.T, DateTimeOffset.UtcNow));
        Assert.Equal(1, await RunScheduleAsync(o.T, deadline.AddMinutes(1)));
        Assert.Equal(0, await RunScheduleAsync(o.T, deadline.AddMinutes(2))); // Once per deadline.
        await DispatchAsync();
        Assert.Equal(2, (await NotificationLinksAsync(o.T["bea"].Client)).Count(l => l == CriticalAnnouncements.ReadLink(id)));
        Assert.Equal(1, (await NotificationLinksAsync(o.T["ali"].Client)).Count(l => l == CriticalAnnouncements.ReadLink(id)));
        Assert.Contains($"/content/items/{id}/team", await NotificationLinksAsync(o.T["mona"].Client));

        var team = (await OkAsync(o.T["mona"].Client.GetAsync($"{Items}/{id}/acknowledgements/team"))).EnumerateArray().ToArray();
        Assert.Equal([false, true], team.Select(m => m.GetProperty("confirmed").GetBoolean()).ToArray());
        Assert.Equal(o.T["bea"].Id, team[0].GetProperty("person").GetProperty("id").GetGuid());
        Assert.Empty((await OkAsync(o.T["omar"].Client.GetAsync($"{Items}/{id}/acknowledgements/team"))).EnumerateArray());

        // The completion report: personal data, so publish_critical or content.admin, audited, built by the worker.
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["pia"].Client.PostAsync($"{Items}/{id}/ack-report", null)).StatusCode);
        var requested = await hs.PostAsync($"{Items}/{id}/ack-report", null);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var report = (await requested.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentAckReportExported, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await hs.GetAsync($"/api/v1/content/ack-reports/{report}/file")).StatusCode);
        await DispatchAsync();
        var status = await OkAsync(hs.GetAsync($"/api/v1/content/ack-reports/{report}"));
        Assert.Equal("completed", status.GetProperty("status").GetString());
        Assert.Equal(2, status.GetProperty("rowCount").GetInt32());
        var csv = await (await hs.GetAsync($"/api/v1/content/ack-reports/{report}/file")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("user_id,display_name,email,status,acknowledged_at,channel,version", lines[0]);
        Assert.Contains(lines, l => l.StartsWith(o.T["ali"].Id.ToString(), StringComparison.Ordinal) && l.Contains(",confirmed,", StringComparison.Ordinal) && l.Contains(",web,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith(o.T["bea"].Id.ToString(), StringComparison.Ordinal) && l.Contains(",pending,", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await o.T["admin"].Client.GetAsync($"/api/v1/content/ack-reports/{report}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_confirm_or_report_on_an_announcement()
    {
        var o = await FreshAsync();
        var hs = o.T["hs"].Client;
        var id = await CreateAsync(hs, "Plant notice", new { presentation = "modal" });
        await PublishAsync(hs, id);
        var report = (await (await hs.PostAsync($"{Items}/{id}/ack-report", null)).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        await DispatchAsync();

        var other = await FreshAsync();
        var admin = other.T["admin"].Client;
        Assert.Null(await ModalAsync(admin));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{Items}/{id}/remind-later", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Items}/{id}/acknowledgements")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{Items}/{id}/ack-report", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/content/ack-reports/{report}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/content/ack-reports/{report}/file")).StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.T.Id, "select count(*) from content.audience_member where item_id = @id", ("id", id)));
    }
}
