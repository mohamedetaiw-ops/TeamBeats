using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 slice 5 (US7, US9 R1 part): the content components reach the builder through their manifests; reactions are
/// one per person; comments notify the owner, wait for a moderator when moderation is on, are hidden after enough
/// reports and can be turned off per site; moderation is audited; tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentSocialTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Comments = "/api/v1/content/comments";

    private sealed record Org(TestTenant T, Guid Site, Guid Item);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("cs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "pia", "mod", "ali", "bea", "omar", "mona" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        await world.AssignAsync(t, "mod", BuiltInRoles.ContentEditor, "tenant", null);
        var sites = await (await t["admin"].Client.GetAsync("/api/v1/content/sites")).ReadAsync<JsonElement>();
        var site = sites[0].GetProperty("id").GetGuid();
        var created = await t["pia"].Client.PostAsJsonAsync(Items, new
        {
            type = "news",
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "Canteen opens", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "Hello" } } } } } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["pia"].Client.PostAsJsonAsync($"{Items}/{item}/publish", new { material = true })).StatusCode);
        await DispatchAsync();
        return new Org(t, site, item);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.Equal(status, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string?> DetailKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : problem.GetProperty("code").GetString();
    }

    private static Task<JsonElement> CommentAsync(HttpClient client, Guid item, string body) =>
        OkAsync(client.PostAsJsonAsync($"{Items}/{item}/comments", new { body }), HttpStatusCode.Created);

    private static async Task<JsonElement[]> CommentsAsync(HttpClient client, Guid item) =>
        (await OkAsync(client.GetAsync($"{Items}/{item}/comments"))).GetProperty("items").EnumerateArray().ToArray();

    private static async Task SiteAsync(Org o, object change) =>
        Assert.Equal(HttpStatusCode.OK, (await o.T["admin"].Client.PutAsJsonAsync($"/api/v1/content/sites/{o.Site}", change)).StatusCode);

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    [Fact]
    public async Task The_builder_offers_the_content_components_with_their_names()
    {
        var o = await FreshAsync();
        var page = await OkAsync(o.T["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages",
            new { templateId = "T01", slug = "news-" + Guid.NewGuid().ToString("N")[..6] }), HttpStatusCode.Created);
        var items = await OkAsync(o.T["admin"].Client.GetAsync($"/api/v1/experience/pages/{page.GetProperty("id").GetString()}/components"));
        var byRef = items.EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);
        foreach (var reference in new[] { "content.news_feed@1", "content.rich_text@1", "content.links@1", "content.call_to_action@1", "content.announcement_banner@1" })
        {
            Assert.True(byRef.ContainsKey(reference), reference);
            Assert.False(string.IsNullOrWhiteSpace(byRef[reference].GetProperty("name").GetString()));
        }
        Assert.Equal("News feed", byRef["content.news_feed@1"].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Without_the_content_capability_the_builder_refuses_the_components_and_the_content_apis_refuse_too()
    {
        // A Workplace version with the page builder but no content, for this tenant only.
        var t = await world.CreateTenantAsync("cs-off-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        await world.PinPackageVersionAsync(t, "workplace", new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
            [Capabilities.OrgHierarchy] = new { enabled = true },
            [Capabilities.ExperiencePageBuilder] = new { enabled = true },
        });

        var page = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "off-news" }), HttpStatusCode.Created);
        var items = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/experience/pages/{page.GetProperty("id").GetString()}/components"));
        var byRef = items.EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);
        foreach (var reference in new[] { "content.news_feed@1", "content.rich_text@1", "content.links@1", "content.call_to_action@1", "content.announcement_banner@1" })
        {
            Assert.False(byRef[reference].GetProperty("eligible").GetBoolean(), reference);
            Assert.Equal("validation.page.component_not_entitled", byRef[reference].GetProperty("reasonKey").GetString());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await t["admin"].Client.GetAsync("/api/v1/content/feed")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["admin"].Client.GetAsync($"{Items}/{Guid.NewGuid()}/comments")).StatusCode);
    }

    [Fact]
    public async Task Reactions_are_one_per_person_and_can_be_changed_or_taken_back()
    {
        var o = await FreshAsync();
        var url = $"{Items}/{o.Item}/reactions";
        var ali = o.T["ali"].Client;
        var first = await OkAsync(ali.PostAsJsonAsync(url, new { kind = "like" }));
        Assert.Equal(1, first.GetProperty("counts").GetProperty("like").GetInt32());
        Assert.Equal("like", first.GetProperty("mine").GetString());
        await OkAsync(o.T["bea"].Client.PostAsJsonAsync(url, new { kind = "like" }));
        var changed = await OkAsync(ali.PostAsJsonAsync(url, new { kind = "celebrate" }));
        Assert.Equal(1, changed.GetProperty("counts").GetProperty("like").GetInt32());
        Assert.Equal(1, changed.GetProperty("counts").GetProperty("celebrate").GetInt32());
        var back = await OkAsync(ali.PostAsJsonAsync(url, new { kind = "none" }));
        Assert.Equal(0, back.GetProperty("counts").GetProperty("celebrate").GetInt32());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("mine").ValueKind);
        var bea = await OkAsync(o.T["bea"].Client.GetAsync(url));
        Assert.Equal("like", bea.GetProperty("mine").GetString());

        var bad = await ali.PostAsJsonAsync(url, new { kind = "angry" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_comment_notifies_the_owner_but_not_its_own_author()
    {
        var o = await FreshAsync();
        var comment = await CommentAsync(o.T["ali"].Client, o.Item, "  Great news  ");
        Assert.Equal("Great news", comment.GetProperty("body").GetString());
        Assert.Equal("visible", comment.GetProperty("status").GetString());
        Assert.True(comment.GetProperty("mine").GetBoolean());
        await CommentAsync(o.T["pia"].Client, o.Item, "Thanks");
        await DispatchAsync();

        var links = await NotificationLinksAsync(o.T["pia"].Client);
        Assert.Single(links, l => l.StartsWith($"/content/read/{o.Item}#comment-", StringComparison.Ordinal));
        Assert.Contains($"#comment-{comment.GetProperty("id").GetString()}", links.Single(l => l.Contains("#comment-", StringComparison.Ordinal)));

        var seen = await CommentsAsync(o.T["bea"].Client, o.Item);
        Assert.Equal(["Great news", "Thanks"], seen.Select(c => c.GetProperty("body").GetString()));
        Assert.All(seen, c => Assert.False(c.GetProperty("mine").GetBoolean()));

        Assert.Equal(HttpStatusCode.BadRequest, (await o.T["ali"].Client.PostAsJsonAsync($"{Items}/{o.Item}/comments", new { body = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await o.T["ali"].Client.PostAsJsonAsync($"{Items}/{o.Item}/comments", new { body = new string('x', ContentSocial.BodyMax + 1) })).StatusCode);
    }

    [Fact]
    public async Task With_moderation_on_comments_wait_for_a_moderator_and_each_decision_is_audited()
    {
        var o = await FreshAsync();
        await SiteAsync(o, new { moderation = true });
        var held = await CommentAsync(o.T["ali"].Client, o.Item, "First");
        Assert.Equal("held", held.GetProperty("status").GetString());
        var heldId = held.GetProperty("id").GetGuid();
        // A moderator's own comment is shown straight away.
        Assert.Equal("visible", (await CommentAsync(o.T["mod"].Client, o.Item, "From the team")).GetProperty("status").GetString());
        await DispatchAsync();
        Assert.DoesNotContain(await NotificationLinksAsync(o.T["pia"].Client), l => l.Contains(heldId.ToString(), StringComparison.Ordinal));

        Assert.DoesNotContain(await CommentsAsync(o.T["bea"].Client, o.Item), c => c.GetProperty("id").GetGuid() == heldId);
        Assert.Contains(await CommentsAsync(o.T["ali"].Client, o.Item), c => c.GetProperty("id").GetGuid() == heldId);
        var page = await OkAsync(o.T["mod"].Client.GetAsync($"{Items}/{o.Item}/comments"));
        Assert.True(page.GetProperty("moderator").GetBoolean());
        Assert.Contains(page.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetGuid() == heldId);

        // Readers cannot moderate.
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["bea"].Client.PostAsync($"{Comments}/{heldId}/approve", null)).StatusCode);

        var approved = await OkAsync(o.T["mod"].Client.PostAsync($"{Comments}/{heldId}/approve", null));
        Assert.Equal("visible", approved.GetProperty("status").GetString());
        Assert.Equal("conflict.content.comment_state", await DetailKeyAsync(await o.T["mod"].Client.PostAsync($"{Comments}/{heldId}/approve", null), HttpStatusCode.Conflict));
        await DispatchAsync();
        Assert.Contains(await NotificationLinksAsync(o.T["pia"].Client), l => l.EndsWith($"#comment-{heldId}", StringComparison.Ordinal));
        Assert.Contains(await CommentsAsync(o.T["bea"].Client, o.Item), c => c.GetProperty("id").GetGuid() == heldId);

        Assert.Equal("hidden", (await OkAsync(o.T["mod"].Client.PostAsync($"{Comments}/{heldId}/hide", null))).GetProperty("status").GetString());
        Assert.DoesNotContain(await CommentsAsync(o.T["bea"].Client, o.Item), c => c.GetProperty("id").GetGuid() == heldId);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCommentModerated, heldId.ToString());
    }

    [Fact]
    public async Task Three_reports_hide_a_comment_until_a_moderator_restores_it()
    {
        var o = await FreshAsync();
        var id = (await CommentAsync(o.T["ali"].Client, o.Item, "Spicy take")).GetProperty("id").GetGuid();
        var report = $"{Comments}/{id}/report";
        // The author cannot report their own comment, and a second report by the same person does not count.
        Assert.Equal(HttpStatusCode.Conflict, (await o.T["ali"].Client.PostAsync(report, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await o.T["bea"].Client.PostAsync(report, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await o.T["bea"].Client.PostAsync(report, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await o.T["omar"].Client.PostAsync(report, null)).StatusCode);
        var stillThere = (await CommentsAsync(o.T["mona"].Client, o.Item)).Single(c => c.GetProperty("id").GetGuid() == id);
        Assert.False(stillThere.GetProperty("reported").GetBoolean());
        Assert.True((await CommentsAsync(o.T["bea"].Client, o.Item)).Single(c => c.GetProperty("id").GetGuid() == id).GetProperty("reported").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await o.T["mona"].Client.PostAsync(report, null)).StatusCode);
        Assert.DoesNotContain(await CommentsAsync(o.T["bea"].Client, o.Item), c => c.GetProperty("id").GetGuid() == id);
        var moderated = (await CommentsAsync(o.T["mod"].Client, o.Item)).Single(c => c.GetProperty("id").GetGuid() == id);
        Assert.Equal("hidden", moderated.GetProperty("status").GetString());

        Assert.Equal("visible", (await OkAsync(o.T["mod"].Client.PostAsync($"{Comments}/{id}/approve", null))).GetProperty("status").GetString());
        Assert.Contains(await CommentsAsync(o.T["pia"].Client, o.Item), c => c.GetProperty("id").GetGuid() == id);
        // Once a moderator has restored it, one new report does not hide it again.
        Assert.Equal(HttpStatusCode.NoContent, (await o.T["pia"].Client.PostAsync(report, null)).StatusCode);
        Assert.Contains(await CommentsAsync(o.T["bea"].Client, o.Item), c => c.GetProperty("id").GetGuid() == id);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCommentModerated, id.ToString());
    }

    [Fact]
    public async Task Only_the_author_deletes_a_comment_and_a_site_can_turn_comments_off()
    {
        var o = await FreshAsync();
        var id = (await CommentAsync(o.T["ali"].Client, o.Item, "Oops")).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["bea"].Client.DeleteAsync($"{Comments}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await o.T["ali"].Client.DeleteAsync($"{Comments}/{id}")).StatusCode);
        Assert.Empty(await CommentsAsync(o.T["ali"].Client, o.Item));
        Assert.Equal(HttpStatusCode.Conflict, (await o.T["mod"].Client.PostAsync($"{Comments}/{id}/hide", null)).StatusCode);

        await SiteAsync(o, new { commentsMode = "off" });
        Assert.Equal("conflict.content.comments_off",
            await DetailKeyAsync(await o.T["ali"].Client.PostAsJsonAsync($"{Items}/{o.Item}/comments", new { body = "Hello?" }), HttpStatusCode.Conflict));
        Assert.False((await OkAsync(o.T["ali"].Client.GetAsync($"{Items}/{o.Item}/comments"))).GetProperty("commentsOn").GetBoolean());
        // Reactions stay available when comments are off.
        Assert.Equal(HttpStatusCode.OK, (await o.T["ali"].Client.PostAsJsonAsync($"{Items}/{o.Item}/reactions", new { kind = "support" })).StatusCode);
    }

    [Fact]
    public async Task Items_and_comments_of_another_tenant_are_not_found()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var comment = (await CommentAsync(a.T["ali"].Client, a.Item, "Ours")).GetProperty("id").GetGuid();
        var other = b.T["ali"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Items}/{a.Item}/comments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Items}/{a.Item}/comments", new { body = "Theirs" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Items}/{a.Item}/reactions", new { kind = "like" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{Comments}/{comment}/report", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.T["mod"].Client.PostAsync($"{Comments}/{comment}/hide", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Comments}/{comment}")).StatusCode);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(b.T.Id, "select count(*) from content.comment"));
        Assert.Equal("visible", (await CommentsAsync(a.T["bea"].Client, a.Item)).Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_draft_or_an_item_outside_the_audience_takes_no_comments_or_reactions()
    {
        var o = await FreshAsync();
        var created = await o.T["pia"].Client.PostAsJsonAsync(Items, new
        {
            type = "news",
            variants = new Dictionary<string, object> { ["en"] = new { title = "Draft", body = new { blocks = Array.Empty<object>() } } },
        });
        var draft = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["ali"].Client.PostAsJsonAsync($"{Items}/{draft}/comments", new { body = "Early" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["ali"].Client.PostAsJsonAsync($"{Items}/{draft}/reactions", new { kind = "like" })).StatusCode);
    }
}
