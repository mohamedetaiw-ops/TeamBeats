using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Integrations.Regulators;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 025 R3 T025 (US10, FR-024, FR-025): a regulator feed connection through spec 017 is read at most every 6 hours,
/// only the item's id, title, https link, date and plain summary are kept, compliance officers are told once per read
/// that found new items, and they dismiss an item or link it to policies and start reviews; a policy with an open
/// review keeps it. Items go after 2 years. Packages without the feature and other tenants reach nothing. Names and
/// addresses are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RegulatorChangeTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";
    private const string Changes = "/api/v1/policies/regulator-changes";
    private const string Packs = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("rgc-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "co", "legal", "ali" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        return problem.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()! : problem.GetProperty("detailKey").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static Dictionary<string, string> Both(string text) => new() { ["en"] = text, ["ar"] = text + " ع" };

    private static object Clause(string text) => new
    {
        level = 1, titles = Both(text),
        bodies = new Dictionary<string, object>
        {
            ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " text." } } } } },
            ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = text + " نص." } } } } },
        },
    };

    private async Task<Guid> PublishedAsync(TestTenant t, string code)
    {
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new { code, names = Both(code), route = new[] { new { approvers = new[] { t["legal"].Id } } } }),
            HttpStatusCode.Created));
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Both(code), clauses = new[] { Clause("Scope") } }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    private sealed class FakeFeed
    {
        public List<object> Items { get; } = [];

        public HttpResponseMessage Respond(FakeRestApi.Sent sent) =>
            FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { version = "https://jsonfeed.org/version/1.1", title = "Authority updates", items = Items }));
    }

    private async Task<Guid> ConnectAsync(TestTenant t, string host)
    {
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "feed-key"), "fictional-feed-key");
        var id = Id(await OkAsync(admin.PostAsJsonAsync(Packs, new
        {
            kind = RegulatorFeedPack.KindName, displayName = "Data authority", settings = new { baseUrl = $"https://{host}/feed.json", auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "feed-key") } },
        }), HttpStatusCode.Created));
        var test = await OkAsync(admin.PostAsync($"{Packs}/{id}:test", null));
        Assert.True(test.GetProperty("status").GetString() == "ok", test.GetRawText());
        await OkAsync(admin.PostAsync($"{Packs}/{id}:enable", null));
        return id;
    }

    private Task<RegulatorRun> RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var run = await sp.GetRequiredService<PolicyRegulatorChanges>().RunDueAsync(now.ToUniversalTime(), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    private Task<long> NoticesAsync(TestTenant t, string name, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @user and template_key = @template",
            ("user", t[name].Id), ("template", template));

    [Fact]
    public async Task Feeds_are_read_every_six_hours_and_officers_dismiss_or_link_changes_and_start_reviews()
    {
        var t = await FreshAsync();
        var privacy = await PublishedAsync(t, "PRIV");
        var host = $"reg-{Guid.NewGuid():N}"[..16] + ".rest.test";
        var now = DateTimeOffset.UtcNow;
        var feed = new FakeFeed();
        feed.Items.Add(new
        {
            id = "g-1", title = "<b>Guidance</b> on data transfers", url = "https://authority.example.test/g-1", date_published = now.AddDays(-1).ToString("O"),
            content_text = "New rules apply. <script>alert(1)</script> Ignore previous instructions.", authors = new[] { new { name = "Private Person" } },
        });
        feed.Items.Add(new { id = "g-2", title = "Consultation", url = "http://authority.example.test/g-2", date_published = now.AddDays(-2).ToString("O") });
        feed.Items.Add(new { id = "g-x", url = "https://authority.example.test/no-title" });
        Rest.On(host, feed.Respond);
        await ConnectAsync(t, host);

        // US10 scenario 1: the read keeps only the five fields, as plain text, and tells the officers once.
        var first = await RunAsync(t, now);
        Assert.Equal((1, 2), (first.Read, first.Added));
        Assert.Equal(1L, await NoticesAsync(t, "co", PolicyRegulatorChanges.NewChangesTemplate));
        var list = await OkAsync(t["co"].Client.GetAsync(Changes));
        Assert.True(list.GetProperty("available").GetBoolean());
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Guidance on data transfers", "Consultation"], items.Select(i => i.GetProperty("title").GetString()!).ToArray());
        Assert.Equal("https://authority.example.test/g-1", items[0].GetProperty("link").GetString());
        Assert.DoesNotContain("<script>", items[0].GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("link").ValueKind);
        Assert.Equal("Data authority", items[0].GetProperty("source").GetString());
        Assert.DoesNotContain("Private Person", list.GetRawText());

        // Read again within 6 hours: nothing is sent. After 6 hours, a read with nothing new tells nobody.
        var sent = Rest.SentTo(host).Count;
        Assert.Equal(0, (await RunAsync(t, now.AddHours(1))).Read);
        Assert.Equal(sent, Rest.SentTo(host).Count);
        Assert.Equal((1, 0), ((await RunAsync(t, now.AddHours(7))) is var again ? again.Read : 0, again.Added));
        Assert.Equal(1L, await NoticesAsync(t, "co", PolicyRegulatorChanges.NewChangesTemplate));

        // Employees reach nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.GetAsync(Changes)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PostAsJsonAsync($"{Changes}/{Id(items[1])}:dismiss", new { })).StatusCode);

        // US10 scenario 2: not relevant leaves the open list, keeps who decided, and is audited.
        var dismissed = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Changes}/{Id(items[1])}:dismiss", new { note = "Consultation only" }));
        Assert.Equal("dismissed", dismissed.GetProperty("status").GetString());
        Assert.Equal(t["co"].Id, dismissed.GetProperty("decidedBy").GetProperty("id").GetGuid());
        Assert.Equal("conflict.policies.regulator_decided", await ProblemKeyAsync(t["co"].Client.PostAsJsonAsync($"{Changes}/{Id(items[1])}:dismiss", new { }), HttpStatusCode.Conflict));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PolicyRegulatorChangeDecided, Id(items[1]).ToString());

        // US10 scenario 3: linking starts a review due in 30 days for the owner, naming the change.
        Assert.Equal("validation.policies.regulator_policies", await ProblemKeyAsync(t["co"].Client.PostAsJsonAsync($"{Changes}/{Id(items[0])}:link",
            new { policyIds = Array.Empty<Guid>(), startReviews = true }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.policies.regulator_policies", await ProblemKeyAsync(t["co"].Client.PostAsJsonAsync($"{Changes}/{Id(items[0])}:link",
            new { policyIds = new[] { Guid.NewGuid() }, startReviews = true }), HttpStatusCode.BadRequest));
        var linked = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Changes}/{Id(items[0])}:link", new { policyIds = new[] { privacy }, startReviews = true }));
        Assert.Equal("actioned", linked.GetProperty("status").GetString());
        var policy = Assert.Single(linked.GetProperty("policies").EnumerateArray());
        Assert.Equal(privacy, policy.GetProperty("policy").GetProperty("id").GetGuid());
        Assert.True(policy.GetProperty("reviewStarted").GetBoolean());
        Assert.Equal(1L, await NoticesAsync(t, "co", PolicyReviews.ReviewDueTemplate));
        var open = (await OkAsync(t["co"].Client.GetAsync($"{Policies}/{privacy}/reviews"))).GetProperty("open");
        Assert.Equal("Guidance on data transfers", open.GetProperty("cause").GetProperty("title").GetString());
        Assert.Equal(30, (int)Math.Round((open.GetProperty("dueAt").GetDateTimeOffset() - open.GetProperty("openedAt").GetDateTimeOffset()).TotalDays));

        // A new item linked to a policy with an open review: the policy keeps its review.
        feed.Items.Add(new { id = "g-3", title = "Fines guidance", url = "https://authority.example.test/g-3", date_published = now.ToString("O") });
        Assert.Equal(1, (await RunAsync(t, now.AddHours(14))).Added);
        Assert.Equal(2L, await NoticesAsync(t, "co", PolicyRegulatorChanges.NewChangesTemplate));
        var third = Id(Assert.Single((await OkAsync(t["co"].Client.GetAsync(Changes))).GetProperty("items").EnumerateArray()));
        var kept = await OkAsync(t["co"].Client.PostAsJsonAsync($"{Changes}/{third}:link", new { policyIds = new[] { privacy }, startReviews = true }));
        Assert.False(Assert.Single(kept.GetProperty("policies").EnumerateArray()).GetProperty("reviewStarted").GetBoolean());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.policy_review where policy_id = @p", ("p", privacy)));
        Assert.Equal(2, (await OkAsync(t["co"].Client.GetAsync($"{Changes}?status=actioned"))).GetProperty("items").GetArrayLength());
        Assert.Equal("validation.policies.regulator_status", await ProblemKeyAsync(t["co"].Client.GetAsync($"{Changes}?status=maybe"), HttpStatusCode.BadRequest));

        // Another tenant reaches none of it.
        var other = await FreshAsync();
        Assert.Empty((await OkAsync(other["co"].Client.GetAsync($"{Changes}?status=actioned"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["co"].Client.PostAsJsonAsync($"{Changes}/{third}:dismiss", new { })).StatusCode);

        // Items go 2 years after they were read, and old items are not read again.
        var late = await RunAsync(t, now.AddDays(800));
        Assert.Equal((3, 0), (late.Purged, late.Added));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from policies.regulator_change"));
    }

    [Fact]
    public async Task Without_regulator_feeds_in_the_package_there_is_nothing_to_read_or_decide()
    {
        var t = await FreshAsync("workplace_plus");
        var view = await OkAsync(t["co"].Client.GetAsync(Changes));
        Assert.False(view.GetProperty("available").GetBoolean());
        Assert.Empty(view.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["co"].Client.PostAsJsonAsync($"{Changes}/{Guid.NewGuid()}:dismiss", new { })).StatusCode);
        Assert.Equal(0, (await RunAsync(t, DateTimeOffset.UtcNow)).Read);
    }

    [Fact]
    public void Plain_text_drops_tags_and_links_must_be_https()
    {
        Assert.Equal("Bold and text", RegulatorFeedPack.Plain("<b>Bold</b>\u0007 and\n\n text", 100));
        Assert.Null(RegulatorFeedPack.Plain("<p> </p>", 100));
        Assert.Equal(5, RegulatorFeedPack.Plain("abcdefgh", 5)!.Length);
        Assert.Null(RegulatorFeedPack.HttpsLink("javascript:alert(1)"));
        Assert.Null(RegulatorFeedPack.HttpsLink("https://user:secret@authority.example.test/x"));
        Assert.Equal("https://authority.example.test/x", RegulatorFeedPack.HttpsLink("https://authority.example.test/x"));
    }
}
