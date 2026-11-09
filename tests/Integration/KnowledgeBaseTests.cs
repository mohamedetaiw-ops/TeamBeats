using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 T011 and T012 (US4, FR-011): articles from the SOP template carry its sections and follow the spec 009
/// draft, publish and version lifecycle; readers mark them helpful or not and the owner sees the score; an FAQ gives
/// one unit per question and answer with a link to its anchor; writing needs <c>kb.edit</c>; restricted articles,
/// other tenants and packages without <c>docs.core</c> are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class KnowledgeBaseTests(TenantWorld world)
{
    private const string Kb = "/api/v1/kb";
    private const string Items = "/api/v1/content/items";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("kb-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "kay", "ann", "emp", "dee" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "emp", "Dubai");
        await world.AddMembershipAsync(t, "dee", "Dubai");
        await world.AssignAsync(t, "kay", BuiltInRoles.ContentPublisher, "tenant", null);
        // Ann writes news but is no knowledge editor.
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "news_author", names = new Dictionary<string, string> { ["en"] = "News author" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentAuthor }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "ann", "news_author", "tenant", null);
        return t;
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

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string template, string en, string? ar = null, string? classification = null) =>
        client.PostAsJsonAsync($"{Kb}/articles", new
        {
            template, classification,
            titles = ar is null ? new Dictionary<string, string> { ["en"] = en } : new Dictionary<string, string> { ["en"] = en, ["ar"] = ar },
        });

    private static object Heading(string text) => new { type = "heading", level = 2, children = new object[] { new { text } } };

    private static object Paragraph(string text) => new { type = "paragraph", children = new object[] { new { text } } };

    /// <summary>Saves the draft with these blocks per locale and publishes it.</summary>
    private static async Task PublishAsync(HttpClient client, Guid id, Dictionary<string, (string Title, object[] Blocks)> variants)
    {
        await OkAsync(client.PutAsJsonAsync($"{Items}/{id}", new
        {
            variants = variants.ToDictionary(v => v.Key, v => (object)new { title = v.Value.Title, body = new { blocks = v.Value.Blocks } }),
        }));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
    }

    private static string[] Headings(JsonElement item, string locale) =>
        item.GetProperty("variants").GetProperty(locale).GetProperty("body").GetProperty("blocks").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "heading")
            .Select(b => string.Concat(b.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("text").GetString()))).ToArray();

    [Fact]
    public async Task An_SOP_article_starts_with_its_sections_and_follows_the_content_lifecycle()
    {
        var t = await FreshAsync();
        var kay = t["kay"].Client;
        var templates = await OkAsync(kay.GetAsync($"{Kb}/templates"));
        Assert.Equal(["sop", "howto", "faq", "troubleshooting"], templates.EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToArray());

        var created = await OkAsync(CreateAsync(kay, "sop", "Forklift checks", "فحص الرافعة"), HttpStatusCode.Created);
        var id = Id(created);
        Assert.Equal($"/content/items/{id}", created.GetProperty("editLink").GetString());
        var draft = await OkAsync(kay.GetAsync($"{Items}/{id}"));
        Assert.Equal("knowledge", draft.GetProperty("type").GetString());
        Assert.Equal("draft", draft.GetProperty("status").GetString());
        Assert.Equal(t["kay"].Id, draft.GetProperty("owner").GetProperty("id").GetGuid());
        Assert.Equal(["Purpose", "Scope", "Responsibilities", "Steps", "Related documents"], Headings(draft, "en"));
        Assert.Equal(["الغرض", "النطاق", "المسؤوليات", "الخطوات", "المستندات ذات الصلة"], Headings(draft, "ar"));

        // A draft is not in the knowledge base yet.
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Kb}/articles/{id}")).StatusCode);
        Assert.DoesNotContain((await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles"))).EnumerateArray(), a => Id(a) == id);

        await PublishAsync(kay, id, new()
        {
            ["en"] = ("Forklift checks", [Heading("Purpose"), Paragraph("Keep forklifts safe."), Heading("Steps"), Paragraph("Check the brakes.")]),
            ["ar"] = ("فحص الرافعة", [Heading("الغرض"), Paragraph("سلامة الرافعات."), Heading("الخطوات"), Paragraph("افحص المكابح.")]),
        });
        var read = await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles/{id}"));
        Assert.Equal("sop", read.GetProperty("template").GetString());
        Assert.Equal("en", read.GetProperty("locale").GetString());
        Assert.Equal(1, read.GetProperty("version").GetInt32());
        Assert.False(read.GetProperty("canManage").GetBoolean());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("score").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("reviewDueAt").ValueKind);
        var sections = read.GetProperty("sections").EnumerateArray().ToArray();
        Assert.Equal(["purpose", "steps"], sections.Select(s => s.GetProperty("anchor").GetString()!).ToArray());
        Assert.Equal("Check the brakes.", sections[1].GetProperty("text").GetString());
        Assert.Equal($"/content/read/{id}#steps", sections[1].GetProperty("link").GetString());
        Assert.Contains((await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles"))).EnumerateArray(),
            a => Id(a) == id && a.GetProperty("template").GetString() == "sop");

        // Arabic readers get the Arabic sections.
        (await t["dee"].Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var inArabic = await OkAsync(t["dee"].Client.GetAsync($"{Kb}/articles/{id}"));
        Assert.Equal("ar", inArabic.GetProperty("locale").GetString());
        Assert.Equal("الخطوات", inArabic.GetProperty("sections")[1].GetProperty("heading").GetString());

        // Editing the live article starts version 2; readers keep version 1 until it is published.
        await OkAsync(kay.PutAsJsonAsync($"{Items}/{id}", new
        {
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "Forklift checks", body = new { blocks = new[] { Heading("Purpose"), Paragraph("Keep forklifts safe."), Heading("Steps"), Paragraph("Check the brakes and horn.") } } },
                ["ar"] = new { title = "فحص الرافعة", body = new { blocks = new[] { Heading("الغرض"), Paragraph("سلامة الرافعات."), Heading("الخطوات"), Paragraph("افحص المكابح والبوق.") } } },
            },
        }));
        Assert.Equal("Check the brakes.", (await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles/{id}"))).GetProperty("sections")[1].GetProperty("text").GetString());
        await OkAsync(kay.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        var next = await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles/{id}"));
        Assert.Equal(2, next.GetProperty("version").GetInt32());
        Assert.Equal("Check the brakes and horn.", next.GetProperty("sections")[1].GetProperty("text").GetString());

        // Owners link related articles and documents; readers see only the ones they can read.
        var other = Id(await OkAsync(CreateAsync(kay, "howto", "Charge a forklift"), HttpStatusCode.Created));
        var hidden = Id(await OkAsync(CreateAsync(kay, "howto", "Draft only"), HttpStatusCode.Created));
        await PublishAsync(kay, other, new() { ["en"] = ("Charge a forklift", [Heading("Overview"), Paragraph("Plug it in.")]) });
        var refused = await kay.PutAsJsonAsync($"{Kb}/articles/{id}/related", new { articleIds = new[] { other, hidden } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.kb.related_article", await ErrorKeysAsync(refused));
        await OkAsync(kay.PutAsJsonAsync($"{Kb}/articles/{id}/related", new { articleIds = new[] { other } }));
        var related = Assert.Single((await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles/{id}"))).GetProperty("relatedArticles").EnumerateArray());
        Assert.Equal(other, Id(related));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.PutAsJsonAsync($"{Kb}/articles/{id}/related", new { articleIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Readers_mark_an_article_helpful_or_not_and_the_owner_sees_the_score()
    {
        var t = await FreshAsync();
        var kay = t["kay"].Client;
        var id = Id(await OkAsync(CreateAsync(kay, "troubleshooting", "Printer offline"), HttpStatusCode.Created));
        await PublishAsync(kay, id, new() { ["en"] = ("Printer offline", [Heading("Symptoms"), Paragraph("The printer shows offline.")]) });

        var emp = t["emp"].Client;
        var mine = await OkAsync(emp.PostAsJsonAsync($"{Kb}/articles/{id}/feedback", new { helpful = false, comment = "Step two is missing." }));
        Assert.False(mine.GetProperty("helpful").GetBoolean());
        Assert.Equal(1, mine.GetProperty("version").GetInt32());
        // A new answer replaces the earlier one.
        await OkAsync(emp.PostAsJsonAsync($"{Kb}/articles/{id}/feedback", new { helpful = true, comment = "Worked after a restart." }));
        await OkAsync(t["dee"].Client.PostAsJsonAsync($"{Kb}/articles/{id}/feedback", new { helpful = false }));
        await OkAsync(t["ann"].Client.PostAsJsonAsync($"{Kb}/articles/{id}/feedback", new { helpful = true }));

        var view = await OkAsync(emp.GetAsync($"{Kb}/articles/{id}"));
        Assert.True(view.GetProperty("myFeedback").GetProperty("helpful").GetBoolean());
        Assert.Equal("Worked after a restart.", view.GetProperty("myFeedback").GetProperty("comment").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("score").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await emp.GetAsync($"{Kb}/articles/{id}/feedback")).StatusCode);

        var owner = await OkAsync(kay.GetAsync($"{Kb}/articles/{id}"));
        Assert.True(owner.GetProperty("canManage").GetBoolean());
        Assert.Equal(2, owner.GetProperty("score").GetProperty("helpful").GetInt32());
        Assert.Equal(1, owner.GetProperty("score").GetProperty("notHelpful").GetInt32());
        Assert.Equal(67, owner.GetProperty("score").GetProperty("percent").GetInt32());
        var report = await OkAsync(kay.GetAsync($"{Kb}/articles/{id}/feedback"));
        var comment = Assert.Single(report.GetProperty("comments").EnumerateArray());
        Assert.Equal("Worked after a restart.", comment.GetProperty("comment").GetString());
        // Comments reach the owner without who wrote them.
        Assert.False(comment.TryGetProperty("userId", out _));
        Assert.DoesNotContain(t["emp"].Id.ToString(), report.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var noAnswer = await emp.PostAsJsonAsync($"{Kb}/articles/{id}/feedback", new { comment = "Hmm" });
        Assert.Contains("validation.kb.helpful", await ErrorKeysAsync(noAnswer));
        var tooLong = await emp.PostAsJsonAsync($"{Kb}/articles/{id}/feedback", new { helpful = true, comment = new string('x', 1001) });
        Assert.Contains("validation.kb.comment", await ErrorKeysAsync(tooLong));

        // A draft takes no feedback.
        var draft = Id(await OkAsync(CreateAsync(kay, "sop", "Not yet"), HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.NotFound, (await emp.PostAsJsonAsync($"{Kb}/articles/{draft}/feedback", new { helpful = true })).StatusCode);
    }

    [Fact]
    public async Task Each_FAQ_question_and_answer_is_its_own_unit_with_a_link_to_its_anchor()
    {
        var t = await FreshAsync();
        var kay = t["kay"].Client;
        var faq = await OkAsync(CreateAsync(kay, "faq", "IT questions", "أسئلة تقنية"), HttpStatusCode.Created);
        var id = Id(faq);
        Assert.Empty(Headings(await OkAsync(kay.GetAsync($"{Items}/{id}")), "en"));
        await PublishAsync(kay, id, new()
        {
            ["en"] = ("IT questions", [Heading("How do I reset my password?"), Paragraph("Use the self service page."),
                Heading("Who approves new laptops?"), Paragraph("Your line manager."), Heading("How do I reset my password?"), Paragraph("Or call the desk.")]),
            ["ar"] = ("أسئلة تقنية", [Heading("كيف أعيد تعيين كلمة المرور؟"), Paragraph("استخدم صفحة الخدمة الذاتية.")]),
        });

        var units = (await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles/{id}/units"))).EnumerateArray().ToArray();
        Assert.Equal(4, units.Length);
        var en = units.Where(u => u.GetProperty("locale").GetString() == "en").ToArray();
        Assert.Equal(["how-do-i-reset-my-password", "who-approves-new-laptops", "how-do-i-reset-my-password-2"],
            en.Select(u => u.GetProperty("anchor").GetString()!).ToArray());
        Assert.Equal("Who approves new laptops?", en[1].GetProperty("title").GetString());
        Assert.Equal("Your line manager.", en[1].GetProperty("text").GetString());
        Assert.Equal($"/content/read/{id}#who-approves-new-laptops", en[1].GetProperty("link").GetString());
        var ar = Assert.Single(units, u => u.GetProperty("locale").GetString() == "ar");
        Assert.Equal("كيف-أعيد-تعيين-كلمة-المرور", ar.GetProperty("anchor").GetString());

        // Other templates give one unit per locale for the whole article.
        var sop = Id(await OkAsync(CreateAsync(kay, "sop", "Visitors"), HttpStatusCode.Created));
        await PublishAsync(kay, sop, new() { ["en"] = ("Visitors", [Heading("Purpose"), Paragraph("Sign visitors in."), Heading("Steps"), Paragraph("Issue a badge.")]) });
        var whole = Assert.Single((await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles/{sop}/units"))).EnumerateArray());
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("anchor").ValueKind);
        Assert.Equal("Purpose\nSign visitors in.\n\nSteps\nIssue a badge.", whole.GetProperty("text").GetString());
        Assert.Equal($"/content/read/{sop}", whole.GetProperty("link").GetString());
    }

    [Fact]
    public async Task Writing_needs_kb_edit_restricted_articles_and_other_tenants_get_not_found()
    {
        var t = await FreshAsync();
        var kay = t["kay"].Client;

        var denied = await CreateAsync(t["ann"].Client, "sop", "Mine");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        // The content API holds the same rule, so a knowledge item cannot be written around the knowledge base.
        var direct = await t["ann"].Client.PostAsJsonAsync(Items, new
        {
            type = "knowledge", variants = new Dictionary<string, object> { ["en"] = new { title = "Mine", body = new { blocks = Array.Empty<object>() } } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(t["emp"].Client, "sop", "Mine")).StatusCode);

        var bad = await CreateAsync(kay, "memo", "Nope");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("validation.kb.template", await ErrorKeysAsync(bad));
        var untitled = await kay.PostAsJsonAsync($"{Kb}/articles", new { template = "sop" });
        Assert.Contains("validation.kb.titles", await ErrorKeysAsync(untitled));

        var restricted = Id(await OkAsync(CreateAsync(kay, "sop", "Board pay", classification: "restricted"), HttpStatusCode.Created));
        await PublishAsync(kay, restricted, new() { ["en"] = ("Board pay", [Heading("Purpose"), Paragraph("Secret.")]) });
        Assert.Equal(HttpStatusCode.OK, (await kay.GetAsync($"{Kb}/articles/{restricted}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Kb}/articles/{restricted}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Kb}/articles/{restricted}/units")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.PostAsJsonAsync($"{Kb}/articles/{restricted}/feedback", new { helpful = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Kb}/articles/{restricted}/feedback")).StatusCode);
        Assert.DoesNotContain((await OkAsync(t["emp"].Client.GetAsync($"{Kb}/articles"))).EnumerateArray(), a => Id(a) == restricted);

        var open = Id(await OkAsync(CreateAsync(kay, "sop", "Fire drill"), HttpStatusCode.Created));
        await PublishAsync(kay, open, new() { ["en"] = ("Fire drill", [Heading("Steps"), Paragraph("Walk out.")]) });
        var other = await FreshAsync();
        var outsider = other["kay"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Kb}/articles/{open}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"{Kb}/articles/{open}/feedback", new { helpful = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Kb}/articles/{open}/feedback")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsJsonAsync($"{Kb}/articles/{open}/related", new { articleIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"{Kb}/articles/{open}/units")).StatusCode);
        Assert.DoesNotContain((await OkAsync(outsider.GetAsync($"{Kb}/articles"))).EnumerateArray(), a => Id(a) == open);
        // Nor can another tenant's article or document be linked.
        var own = Id(await OkAsync(CreateAsync(outsider, "sop", "Ours"), HttpStatusCode.Created));
        var foreign = await outsider.PutAsJsonAsync($"{Kb}/articles/{own}/related", new { articleIds = new[] { open } });
        Assert.Contains("validation.kb.related_article", await ErrorKeysAsync(foreign));
        var foreignDoc = await outsider.PutAsJsonAsync($"{Kb}/articles/{own}/related", new { documentIds = new[] { Guid.NewGuid() } });
        Assert.Contains("validation.kb.related_document", await ErrorKeysAsync(foreignDoc));
    }

    [Fact]
    public async Task Without_the_document_capability_the_knowledge_base_is_refused()
    {
        var tenant = await world.CreateTenantAsync("kb-p-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        // New tenants take the latest published version, so the package's current capabilities are published again at
        // the end and later tests see the package as it was.
        var current = (await OkAsync(tenant["admin"].Client.GetAsync("/api/v1/tenant/entitlements"))).GetProperty("capabilities");
        var capabilities = new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
            [Capabilities.OrgHierarchy] = new { enabled = true },
        };
        var draft = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new { capabilities }), HttpStatusCode.Created);
        var version = draft.GetProperty("version").GetInt32();
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace/versions/{version}:publish", null)).EnsureSuccessStatusCode();
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{tenant.Id}/subscription", new { packageKey = "workplace", version })).EnsureSuccessStatusCode();
        var restore = await OkAsync(world.Platform.PostAsJsonAsync("/api/v1/platform/packages/workplace/versions", new { capabilities = current }), HttpStatusCode.Created);
        (await world.Platform.PostAsync($"/api/v1/platform/packages/workplace/versions/{restore.GetProperty("version").GetInt32()}:publish", null)).EnsureSuccessStatusCode();

        foreach (var call in new[]
        {
            tenant["admin"].Client.GetAsync($"{Kb}/articles"),
            tenant["admin"].Client.PostAsJsonAsync($"{Kb}/articles", new { template = "sop", titles = new Dictionary<string, string> { ["en"] = "X" } }),
            tenant["admin"].Client.GetAsync($"{Kb}/articles/{Guid.NewGuid()}/units"),
        })
        {
            var response = await call;
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("entitlement.not_in_package", await ErrorKeysAsync(response));
        }
    }
}
