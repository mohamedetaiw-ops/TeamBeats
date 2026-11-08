using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 slice 1: sites, items, versions and the lifecycle. Drafts stay hidden from readers; publish now or on a
/// schedule in the site's time zone; editing a live item starts the next version while the live one stays; expiry keeps
/// a labelled item readable through the grace period, then archives it; authors edit only their own drafts; sibling
/// units, other tenants and readers without permission get not found; the site quota of the plan holds; the body
/// refuses unsafe links and embeds off the tenant allowlist; every transition is audited.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentLifecycleTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Sites = "/api/v1/content/sites";

    private sealed record Org(TestTenant T, Guid Dubai, Guid Cairo);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ct-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        var cairo = await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "pia", "ed", "ann", "bob", "cara", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "emp", "Dubai");
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        await world.AssignAsync(t, "cara", BuiltInRoles.ContentEditor, "org_subtree", cairo);
        // Ann and Bob only author: their own drafts, nothing else.
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "news_author", names = new Dictionary<string, string> { ["en"] = "News author" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentAuthor }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "ann", "news_author", "org_subtree", dubai);
        await world.AssignAsync(t, "bob", "news_author", "org_subtree", dubai);
        return new Org(t, dubai, cairo);
    }

    private static object Body(string text, string? link = null) =>
        new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text, link } } } } };

    private static object Variants(string en, string? ar = null) => ar is null
        ? new Dictionary<string, object> { ["en"] = new { title = en, body = Body(en) } }
        : new Dictionary<string, object> { ["en"] = new { title = en, body = Body(en) }, ["ar"] = new { title = ar, body = Body(ar) } };

    private static async Task<JsonElement> CreateAsync(HttpClient client, object request)
    {
        var response = await client.PostAsJsonAsync(Items, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static Task<JsonElement> NewsAsync(HttpClient client, string en, string? ar = null, Guid? siteId = null) =>
        CreateAsync(client, new { siteId, type = "news", variants = Variants(en, ar) });

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Guid id, object? body = null) =>
        client.PostAsJsonAsync($"{Items}/{id}/publish", body ?? new { });

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private static async Task<string?> ProblemKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : problem.GetProperty("code").GetString();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static string Status(JsonElement e) => e.GetProperty("status").GetString()!;

    private Task<int> RunScheduleAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var moved = await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return moved;
        });

    /// <summary>A local wall time in the site's zone (the tenant zone, Asia/Dubai), as the editor would pick it.</summary>
    private static DateTime Local(DateTimeOffset utc) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai")), DateTimeKind.Unspecified);

    [Fact]
    public async Task A_draft_is_hidden_until_published_and_the_next_version_waits_while_the_live_one_stays()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var emp = o.T["emp"].Client;
        var item = await NewsAsync(pia, "Quarterly results", "النتائج الفصلية");
        var id = Id(item);
        Assert.Equal("draft", Status(item));
        Assert.Equal(1, item.GetProperty("draftVersion").GetInt32());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCreated, id.ToString());

        // Readers do not see drafts, and cannot open the editing view at all.
        Assert.Equal(HttpStatusCode.NotFound, (await emp.GetAsync($"{Items}/{id}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await emp.GetAsync($"{Items}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(o.T["ed"].Client, id)).StatusCode);

        var live = await OkAsync(PublishAsync(pia, id));
        Assert.Equal("published", Status(live));
        Assert.Equal(1, live.GetProperty("publishedVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, live.GetProperty("draftVersion").ValueKind);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentPublished, id.ToString());

        var read = await OkAsync(emp.GetAsync($"{Items}/{id}/view"));
        Assert.Equal("Quarterly results", read.GetProperty("title").GetString());
        Assert.False(read.GetProperty("fallback").GetBoolean());
        (await emp.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        Assert.Equal("النتائج الفصلية", (await OkAsync(emp.GetAsync($"{Items}/{id}/view"))).GetProperty("title").GetString());

        // Editing the live item starts version 2; readers keep version 1 until it is published.
        var edited = await OkAsync(pia.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Quarterly results, revised", "النتائج الفصلية"), changeNote = "Fix figures" }));
        Assert.Equal(2, edited.GetProperty("draftVersion").GetInt32());
        Assert.Equal(1, edited.GetProperty("publishedVersion").GetInt32());
        Assert.True(edited.GetProperty("variants").GetProperty("ar").GetProperty("outOfDate").GetBoolean());
        var readV1 = await OkAsync(emp.GetAsync($"{Items}/{id}/view"));
        Assert.Equal(1, readV1.GetProperty("version").GetInt32());

        Assert.Equal(2, (await OkAsync(PublishAsync(pia, id))).GetProperty("publishedVersion").GetInt32());
        Assert.Equal(2, (await OkAsync(emp.GetAsync($"{Items}/{id}/view"))).GetProperty("version").GetInt32());
        Assert.Equal("conflict.content.nothing_to_publish", await ProblemKeyAsync(await PublishAsync(pia, id), HttpStatusCode.Conflict));

        var versions = (await OkAsync(pia.GetAsync($"{Items}/{id}/versions"))).EnumerateArray().ToArray();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("number").GetInt32()));
        Assert.Equal("Fix figures", versions[0].GetProperty("changeNote").GetString());
        Assert.True(versions[0].GetProperty("current").GetBoolean());
        Assert.Equal(o.T["pia"].Id, versions[0].GetProperty("approver").GetProperty("id").GetGuid());
        // News does not show history to readers by default (spec 009 section 6).
        Assert.Equal(HttpStatusCode.NotFound, (await emp.GetAsync($"{Items}/{id}/versions")).StatusCode);

        // A published version never changes, even by a direct update.
        await Assert.ThrowsAnyAsync<Exception>(() => world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            await sp.GetRequiredService<IDbSession>().ExecAsync(
                "update content.content_version set change_note = 'rewritten' where item_id = @id and number = 1", new { id });
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        }));
    }

    [Fact]
    public async Task A_reader_gets_the_tenant_default_language_with_a_fallback_note_when_their_language_is_missing()
    {
        var o = await FreshAsync();
        var id = Id(await NewsAsync(o.T["pia"].Client, "Only in English"));
        await OkAsync(PublishAsync(o.T["pia"].Client, id));
        var emp = o.T["emp"].Client;
        (await emp.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var read = await OkAsync(emp.GetAsync($"{Items}/{id}/view"));
        Assert.Equal("en", read.GetProperty("locale").GetString());
        Assert.True(read.GetProperty("fallback").GetBoolean());
        Assert.Equal(["en"], read.GetProperty("locales").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task Confidential_items_need_a_covering_grant_while_internal_items_are_open_to_every_member()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var open = Id(await NewsAsync(pia, "Open to all"));
        var closed = Id(await CreateAsync(pia, new { type = "news", classification = "confidential", variants = Variants("Board only") }));
        await OkAsync(PublishAsync(pia, open));
        await OkAsync(PublishAsync(pia, closed));
        Assert.Equal(HttpStatusCode.OK, (await o.T["emp"].Client.GetAsync($"{Items}/{open}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["emp"].Client.GetAsync($"{Items}/{closed}/view")).StatusCode);
        // An editor holds content.view across the tenant through their role.
        Assert.Equal(HttpStatusCode.OK, (await o.T["ed"].Client.GetAsync($"{Items}/{closed}/view")).StatusCode);
        // Cara's role covers only Cairo, so the tenant-wide confidential item stays closed to her.
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["cara"].Client.GetAsync($"{Items}/{closed}/view")).StatusCode);
    }

    [Fact]
    public async Task A_page_needs_an_owner_to_go_live_and_gets_a_review_date()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var page = await CreateAsync(pia, new { type = "page", variants = Variants("Travel policy") });
        Assert.True(page.GetProperty("reviewDueAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMonths(11));
        Assert.Contains("validation.content.owner_required", await ErrorKeysAsync(await PublishAsync(pia, Id(page))));

        Assert.Contains("validation.content.owner", await ErrorKeysAsync(await pia.PutAsJsonAsync($"{Items}/{Id(page)}",
            new { variants = Variants("Travel policy"), ownerId = Guid.NewGuid() })));
        await OkAsync(pia.PutAsJsonAsync($"{Items}/{Id(page)}", new { variants = Variants("Travel policy"), ownerId = o.T["ed"].Id }));
        Assert.Equal("published", Status(await OkAsync(PublishAsync(pia, Id(page)))));
    }

    [Fact]
    public async Task Scheduled_items_go_live_in_the_site_time_zone_then_expire_stay_readable_and_archive_after_the_grace_period()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var emp = o.T["emp"].Client;
        var id = Id(await NewsAsync(pia, "Town hall on Thursday"));
        var at = DateTimeOffset.UtcNow.AddHours(2);
        var until = DateTimeOffset.UtcNow.AddDays(1);

        Assert.Contains("validation.content.expiry", await ErrorKeysAsync(await PublishAsync(pia, id, new { publishAt = Local(at), expiresAt = Local(at.AddMinutes(-5)) })));
        var scheduled = await OkAsync(PublishAsync(pia, id, new { publishAt = Local(at), expiresAt = Local(until) }));
        Assert.Equal("scheduled", Status(scheduled));
        // The local time was read in Asia/Dubai: the stored instant is the same moment, to the second.
        Assert.InRange((scheduled.GetProperty("publishAt").GetDateTimeOffset() - at).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(1));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentScheduled, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await emp.GetAsync($"{Items}/{id}/view")).StatusCode);

        Assert.Equal(0, await RunScheduleAsync(o.T, at.AddMinutes(-1)));
        Assert.Equal(1, await RunScheduleAsync(o.T, at.AddMinutes(1)));
        Assert.Equal("published", Status(await OkAsync(pia.GetAsync($"{Items}/{id}"))));
        Assert.False((await OkAsync(emp.GetAsync($"{Items}/{id}/view"))).GetProperty("expired").GetBoolean());

        Assert.Equal(1, await RunScheduleAsync(o.T, until.AddMinutes(1)));
        Assert.Equal("expired", Status(await OkAsync(pia.GetAsync($"{Items}/{id}"))));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentExpired, id.ToString());
        // Still readable by link with the expired label inside the grace period.
        Assert.True((await OkAsync(emp.GetAsync($"{Items}/{id}/view"))).GetProperty("expired").GetBoolean());

        Assert.Equal(0, await RunScheduleAsync(o.T, DateTimeOffset.UtcNow.AddDays(20)));
        Assert.Equal(1, await RunScheduleAsync(o.T, DateTimeOffset.UtcNow.AddDays(32)));
        Assert.Equal("archived", Status(await OkAsync(pia.GetAsync($"{Items}/{id}"))));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentArchived, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await emp.GetAsync($"{Items}/{id}/view")).StatusCode);
        Assert.Equal("conflict.content.archived", await ProblemKeyAsync(await pia.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Again") }), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_scheduled_next_version_keeps_the_live_one_readable_until_it_goes_live()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var id = Id(await NewsAsync(pia, "Canteen menu"));
        await OkAsync(PublishAsync(pia, id));
        await OkAsync(pia.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Canteen menu, week 2") }));
        var at = DateTimeOffset.UtcNow.AddHours(3);
        Assert.Equal("published", Status(await OkAsync(PublishAsync(pia, id, new { publishAt = Local(at) }))));
        Assert.Equal("Canteen menu", (await OkAsync(o.T["emp"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("title").GetString());
        Assert.Equal(1, await RunScheduleAsync(o.T, at.AddMinutes(1)));
        Assert.Equal("Canteen menu, week 2", (await OkAsync(o.T["emp"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unpublish_takes_an_item_down_and_it_can_go_live_again_and_archive_is_manual_too()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var id = Id(await NewsAsync(pia, "Parking closed"));
        await OkAsync(PublishAsync(pia, id));
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["ed"].Client.PostAsJsonAsync($"{Items}/{id}/unpublish", new { })).StatusCode);
        Assert.Equal("draft", Status(await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/unpublish", new { }))));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentUnpublished, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["emp"].Client.GetAsync($"{Items}/{id}/view")).StatusCode);
        Assert.Equal("conflict.content.not_published", await ProblemKeyAsync(await pia.PostAsJsonAsync($"{Items}/{id}/unpublish", new { }), HttpStatusCode.Conflict));

        Assert.Equal("published", Status(await OkAsync(PublishAsync(pia, id))));
        Assert.Equal("archived", Status(await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/archive", new { }))));
        Assert.Equal("conflict.content.archived", await ProblemKeyAsync(await PublishAsync(pia, id), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Authors_edit_only_their_own_drafts_editors_any_and_sibling_units_see_nothing()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var created = await admin.PostAsJsonAsync(Sites, new { names = new Dictionary<string, string> { ["en"] = "Dubai news", ["ar"] = "أخبار دبي" }, orgUnitId = o.Dubai });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var site = Id(await created.ReadAsync<JsonElement>());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.SiteCreated, site.ToString());

        var ann = o.T["ann"].Client;
        var item = await NewsAsync(ann, "Dubai office move", siteId: site);
        var id = Id(item);
        Assert.Equal(o.T["ann"].Id, item.GetProperty("createdBy").GetProperty("id").GetGuid());
        await OkAsync(ann.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Dubai office move, update") }));

        // Bob may author in Dubai and open the item, but not change Ann's draft or publish anything.
        Assert.Equal(HttpStatusCode.OK, (await o.T["bob"].Client.GetAsync($"{Items}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["bob"].Client.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Hijacked") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(ann, id)).StatusCode);
        Assert.DoesNotContain(id, (await OkAsync(o.T["bob"].Client.GetAsync($"{Items}?site={site}"))).EnumerateArray().Select(Id));
        Assert.Contains(id, (await OkAsync(ann.GetAsync($"{Items}?site={site}"))).EnumerateArray().Select(Id));

        // Authors cannot write to the default site: their role is scoped to Dubai.
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.PostAsJsonAsync(Items, new { type = "news", variants = Variants("Everyone") })).StatusCode);

        // Cara edits in Cairo, a sibling: the Dubai item and site do not exist for her.
        var cara = o.T["cara"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await cara.GetAsync($"{Items}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cara.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Cairo") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cara.GetAsync($"{Sites}/{site}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cara.PostAsJsonAsync(Items, new { siteId = site, type = "news", variants = Variants("Cairo") })).StatusCode);

        // A tenant-wide editor may change anyone's draft; a stale version is refused.
        var ed = o.T["ed"].Client;
        var current = await OkAsync(ed.GetAsync($"{Items}/{id}"));
        Assert.Equal("conflict.content.changed", await ProblemKeyAsync(await ed.PutAsJsonAsync($"{Items}/{id}",
            new { variants = Variants("Edited"), version = current.GetProperty("version").GetInt32() - 1 }), HttpStatusCode.Conflict));
        await OkAsync(ed.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("Edited"), version = current.GetProperty("version").GetInt32() }));

        // Site settings change only with content.admin.
        Assert.Equal(HttpStatusCode.Forbidden, (await ed.PutAsJsonAsync($"{Sites}/{site}", new { commentsMode = "off" })).StatusCode);
        var changed = await OkAsync(admin.PutAsJsonAsync($"{Sites}/{site}", new { commentsMode = "off" }));
        Assert.Equal("off", changed.GetProperty("commentsMode").GetString());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.SiteChanged, site.ToString());
    }

    [Fact]
    public async Task The_body_refuses_unsafe_links_unknown_blocks_and_embeds_off_the_allowlist()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        object Item(object body) => new { type = "news", variants = new Dictionary<string, object> { ["en"] = new { title = "Body", body } } };

        Assert.Contains("validation.content.body_link", await ErrorKeysAsync(await pia.PostAsJsonAsync(Items, Item(Body("Click", "javascript:alert(1)")))));
        Assert.Contains("validation.content.body_block", await ErrorKeysAsync(await pia.PostAsJsonAsync(Items,
            Item(new { blocks = new object[] { new { type = "html", text = "<script>" } } }))));
        var embed = new { blocks = new object[] { new { type = "embed", url = "https://www.youtube.com/embed/abc" } } };
        Assert.Contains("validation.content.body_embed", await ErrorKeysAsync(await pia.PostAsJsonAsync(Items, Item(embed))));

        Assert.Equal(HttpStatusCode.Forbidden, (await pia.PutAsJsonAsync("/api/v1/content/settings", new { embedHosts = new[] { "youtube.com" } })).StatusCode);
        Assert.Contains("validation.content.embed_host", await ErrorKeysAsync(await o.T["admin"].Client.PutAsJsonAsync("/api/v1/content/settings",
            new { embedHosts = new[] { "https://youtube.com/x" } })));
        var settings = await OkAsync(o.T["admin"].Client.PutAsJsonAsync("/api/v1/content/settings", new { embedHosts = new[] { "youtube.com" } }));
        Assert.Equal(["youtube.com"], settings.GetProperty("embedHosts").EnumerateArray().Select(h => h.GetString()));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentSettingsChanged);
        Assert.Equal(HttpStatusCode.Created, (await pia.PostAsJsonAsync(Items, Item(embed))).StatusCode);

        Assert.Contains("validation.content.variant_locale", await ErrorKeysAsync(await pia.PostAsJsonAsync(Items,
            new { type = "news", variants = new Dictionary<string, object> { ["fr"] = new { title = "Bonjour" } } })));
        Assert.Contains("validation.content.audience_not_supported", await ErrorKeysAsync(await pia.PostAsJsonAsync(Items,
            new { type = "news", variants = Variants("Targeted"), audience = new { anyOf = new[] { new { channels = new[] { "teams" } } } } })));
        Assert.Contains("validation.content.type", await ErrorKeysAsync(await pia.PostAsJsonAsync(Items, new { type = "blog", variants = Variants("Blog") })));
    }

    [Fact]
    public async Task The_plan_limits_the_number_of_sites()
    {
        // Workplace Plus allows 25 sites, the default site included. (Other tests publish new Workplace versions, so the
        // limit is read from a package they leave alone.)
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        object Site(string name) => new { names = new Dictionary<string, string> { ["en"] = name } };
        for (var i = 1; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(Sites, Site($"Site {i}"))).StatusCode);
        }
        var over = await admin.PostAsJsonAsync(Sites, Site("Finance"));
        Assert.Equal(HttpStatusCode.Forbidden, over.StatusCode);
        var problem = await over.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement.quota_exhausted", problem.GetProperty("code").GetString());
        Assert.Equal("content.max_sites", problem.GetProperty("capability").GetString());
        Assert.Equal(25, (await OkAsync(admin.GetAsync(Sites))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["pia"].Client.PostAsJsonAsync(Sites, Site("Mine"))).StatusCode);
    }

    [Fact]
    public async Task Items_and_sites_of_another_tenant_are_not_found()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var id = Id(await NewsAsync(a.T["pia"].Client, "Tenant A news"));
        await OkAsync(PublishAsync(a.T["pia"].Client, id));
        var site = (await OkAsync(a.T["pia"].Client.GetAsync($"{Items}/{id}"))).GetProperty("siteId").GetGuid();
        var bAdmin = b.T["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.GetAsync($"{Items}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.GetAsync($"{Items}/{id}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.GetAsync($"{Items}/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.PostAsJsonAsync($"{Items}/{id}/unpublish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants("B") })).StatusCode);
        Assert.Equal(0, await RunScheduleAsync(b.T, DateTimeOffset.UtcNow.AddDays(400)));
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.GetAsync($"{Sites}/{site}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.PutAsJsonAsync($"{Sites}/{site}", new { commentsMode = "off" })).StatusCode);
        Assert.DoesNotContain(site, (await OkAsync(bAdmin.GetAsync(Sites))).EnumerateArray().Select(Id));
        Assert.Equal("published", Status(await OkAsync(a.T["pia"].Client.GetAsync($"{Items}/{id}"))));
    }
}
