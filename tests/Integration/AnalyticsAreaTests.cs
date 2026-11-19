using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T020 (FR-002, section 2): the knowledge, engagement, HR and security trend dashboards read their figures from
/// the owning modules, hide groups of fewer people than the minimum group size, count sensitive leave types together,
/// need <c>analytics.advanced</c> and the access of section 3, and stay in their tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AnalyticsAreaTests(TenantWorld world)
{
    private const string Api = "/api/v1/analytics";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static double? Total(JsonElement view, string key)
    {
        var value = view.GetProperty("totals").EnumerateArray().Single(f => f.GetProperty("key").GetString() == key).GetProperty("value");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    }

    private static JsonElement[] Rows(JsonElement view, string section) =>
        [.. view.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("key").GetString() == section).GetProperty("rows").EnumerateArray()];

    private async Task<TestTenant> PeopleAsync(string prefix, int people)
    {
        var t = await world.CreateTenantAsync(prefix + "-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        for (var i = 1; i <= people; i++)
        {
            await world.SeedUserAsync(t, "e" + i, MembershipTypes.Employee);
        }
        return t;
    }

    private Task SeedAsync(TestTenant t, string sql, object args) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await Sql.ExecAsync(session, sql, args);
            await session.CommitAsync();
            return true;
        });

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    [Fact]
    public async Task Knowledge_shows_article_helpfulness_and_unanswered_questions_to_editors()
    {
        var t = await PeopleAsync("akn", 5);
        var admin = t["admin"].Client;
        // Asked before anything is published: no source, so no answer.
        for (var i = 1; i <= 5; i++)
        {
            await OkAsync(t["e" + i].Client.PostAsJsonAsync("/api/v1/assistant/turns", new { question = "How do I check a forklift?" }));
        }
        var created = await OkAsync(admin.PostAsJsonAsync("/api/v1/kb/articles", new { template = "sop", titles = new Dictionary<string, string> { ["en"] = "Forklift checks" } }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(admin.PutAsJsonAsync($"/api/v1/content/items/{id}", new { variants = new { en = new { title = "Forklift checks", body = Body("Check the brakes.") } } }));
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
        for (var i = 1; i <= 5; i++)
        {
            await OkAsync(t["e" + i].Client.PostAsJsonAsync($"/api/v1/kb/articles/{id}/feedback", new { helpful = i != 5 }));
        }

        var view = await OkAsync(admin.GetAsync($"{Api}/knowledge"));
        Assert.Equal("knowledge", view.GetProperty("area").GetString());
        Assert.Equal(1, Total(view, "articles"));
        Assert.Equal(5, Total(view, "ratings"));
        Assert.Equal(0.8, Total(view, "helpfulRate"));
        Assert.Equal(5, Total(view, "questions"));
        Assert.Equal(5, Total(view, "unanswered"));
        Assert.Equal(1.0, Total(view, "unansweredRate"));
        var article = Assert.Single(Rows(view, "articles"));
        Assert.Equal(id.ToString(), article.GetProperty("key").GetString());
        Assert.Equal("Forklift checks", article.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(5, article.GetProperty("amount").GetDouble());
        Assert.Equal(0.8, article.GetProperty("rate").GetDouble());
        var outcome = Assert.Single(Rows(view, "outcomes"));
        Assert.Equal(5, outcome.GetProperty("count").GetInt64());
        Assert.Equal(5, Rows(view, "unansweredWeeks").Sum(w => w.GetProperty("count").GetInt64()));

        // A knowledge editor may open it; an employee may not; neither may anyone without advanced analytics.
        await world.AssignAsync(t, "e1", BuiltInRoles.ContentPublisher, "tenant", null);
        await OkAsync(t["e1"].Client.GetAsync($"{Api}/knowledge"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e2"].Client.GetAsync($"{Api}/knowledge")).StatusCode);

        // Two askers are fewer than the minimum: questions and their weeks are hidden, and the other tenant is not counted.
        var small = await PeopleAsync("akn", 2);
        for (var i = 1; i <= 2; i++)
        {
            await OkAsync(small["e" + i].Client.PostAsJsonAsync("/api/v1/assistant/turns", new { question = "Where do I park?" }));
        }
        var hidden = await OkAsync(small["admin"].Client.GetAsync($"{Api}/knowledge"));
        Assert.Null(Total(hidden, "questions"));
        Assert.Null(Total(hidden, "unanswered"));
        Assert.Equal(0, Total(hidden, "articles"));
        Assert.All(Rows(hidden, "unansweredWeeks"), w => Assert.Equal(JsonValueKind.Null, w.GetProperty("count").ValueKind));

        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.AnalyticsAdvanced, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync($"{Api}/knowledge")).StatusCode);
    }

    [Fact]
    public async Task Engagement_counts_community_activity_and_folds_small_groups()
    {
        var t = await PeopleAsync("aen", 5);
        var admin = t["admin"].Client;
        var community = (await OkAsync(admin.PostAsJsonAsync("/api/v1/engagement/communities", new { names = new { en = "Running club", ar = "نادي الجري" }, kind = "open" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        for (var i = 1; i <= 5; i++)
        {
            var c = t["e" + i].Client;
            await OkAsync(c.PostAsync($"/api/v1/engagement/communities/{community}:join", null));
            await OkAsync(c.PostAsJsonAsync($"/api/v1/engagement/communities/{community}/posts", new { body = Body("Who is in for the Friday run?") }), HttpStatusCode.Created);
        }

        var view = await OkAsync(admin.GetAsync($"{Api}/engagement"));
        Assert.Equal(5, Total(view, "posts"));
        Assert.Equal(5, Total(view, "contributors"));
        var row = Assert.Single(Rows(view, "communities"));
        Assert.Equal(community.ToString(), row.GetProperty("key").GetString());
        Assert.Equal("Running club", row.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(5, row.GetProperty("count").GetInt64());
        Assert.True(row.GetProperty("people").GetInt64() >= 5);
        Assert.Equal(5, Rows(view, "activityWeeks").Sum(w => w.GetProperty("count").GetInt64()));

        // Two posters: totals hidden, the community folds into Other without figures.
        var small = await PeopleAsync("aen", 2);
        var other = (await OkAsync(small["admin"].Client.PostAsJsonAsync("/api/v1/engagement/communities", new { names = new { en = "Book club" }, kind = "open" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        for (var i = 1; i <= 2; i++)
        {
            var c = small["e" + i].Client;
            await OkAsync(c.PostAsync($"/api/v1/engagement/communities/{other}:join", null));
            await OkAsync(c.PostAsJsonAsync($"/api/v1/engagement/communities/{other}/posts", new { body = Body("Next book?") }), HttpStatusCode.Created);
        }
        var hidden = await OkAsync(small["admin"].Client.GetAsync($"{Api}/engagement"));
        Assert.Null(Total(hidden, "posts"));
        var folded = Assert.Single(Rows(hidden, "communities"));
        Assert.Equal("other", folded.GetProperty("key").GetString());
        Assert.Equal(JsonValueKind.Null, folded.GetProperty("count").ValueKind);

        Assert.Equal(HttpStatusCode.Forbidden, (await t["e1"].Client.GetAsync($"{Api}/engagement")).StatusCode);
        var unit = await world.CreateUnitAsync(t, "Ops", null, "department");
        await world.AssignAsync(t, "e2", BuiltInRoles.AnalyticsViewer, "org_subtree", unit);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e2"].Client.GetAsync($"{Api}/engagement")).StatusCode);
    }

    [Fact]
    public async Task Hr_counts_leave_by_type_with_sensitive_types_together()
    {
        var t = await PeopleAsync("ahr", 5);
        await SeedAsync(t,
            """
            insert into hr.leave_type (tenant_id, id, key, names, sensitive, created_at, updated_at) values
              (platform.current_tenant_id(), @annual, 'annual', '{"en":"Annual leave"}', false, now(), now()),
              (platform.current_tenant_id(), @sick, 'sick', '{"en":"Sick leave"}', true, now(), now());
            insert into hr.leave_request (tenant_id, id, number, user_id, filed_by, leave_type_id, start_date, end_date, days, status, created_at, updated_at)
            select platform.current_tenant_id(), gen_random_uuid(), 'L-' || k || '-' || i, u, u, case k when 'a' then @annual else @sick end,
                   current_date, current_date + 1, 2, 'approved', now(), now()
            from unnest(@users) with ordinality as x(u, i) cross join (values ('a'), ('s')) as kinds(k);
            """, new { annual = Guid.NewGuid(), sick = Guid.NewGuid(), users = Enumerable.Range(1, 5).Select(i => t["e" + i].Id).ToArray() });

        var view = await OkAsync(t["admin"].Client.GetAsync($"{Api}/hr"));
        Assert.Equal(10, Total(view, "leaveRequests"));
        Assert.Equal(5, Total(view, "leavePeople"));
        Assert.Equal(20, Total(view, "leaveDays"));
        var types = Rows(view, "leaveTypes");
        Assert.Equal(["annual", "sensitive"], types.Select(r => r.GetProperty("key").GetString()!).Order(StringComparer.Ordinal).ToArray());
        var sensitive = types.Single(r => r.GetProperty("key").GetString() == "sensitive");
        Assert.Equal(JsonValueKind.Null, sensitive.GetProperty("names").ValueKind);
        Assert.Equal(10, sensitive.GetProperty("amount").GetDouble());
        Assert.DoesNotContain("Sick leave", view.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(Rows(view, "exitReasons"));

        // An HR officer for the whole organization may open it; an employee and another tenant see nothing of it.
        await world.AssignAsync(t, "e1", BuiltInRoles.HrOfficer, "tenant", null);
        await OkAsync(t["e1"].Client.GetAsync($"{Api}/hr"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e2"].Client.GetAsync($"{Api}/hr")).StatusCode);
        var other = await PeopleAsync("ahr", 1);
        // Nobody behind a figure is a zero, not a hidden group.
        Assert.Equal(0, Total(await OkAsync(other["admin"].Client.GetAsync($"{Api}/hr")), "leaveRequests"));
        Assert.Empty(Rows(await OkAsync(other["admin"].Client.GetAsync($"{Api}/hr")), "leaveTypes"));
    }

    [Fact]
    public async Task Security_trends_count_alerts_guests_and_weaker_settings()
    {
        var t = await PeopleAsync("asc", 1);
        await world.SeedUserAsync(t, "guest", MembershipTypes.Guest);
        await SeedAsync(t,
            """
            insert into security.alert (tenant_id, id, kind, subject_user_id, created_at)
            select platform.current_tenant_id(), gen_random_uuid(), 'download_volume', @user, now() - make_interval(hours => i) from generate_series(1, 3) i;
            """, new { user = t["e1"].Id });

        var view = await OkAsync(t["admin"].Client.GetAsync($"{Api}/security"));
        Assert.Equal(3, Total(view, "alerts"));
        Assert.Equal(3, Total(view, "openAlerts"));
        Assert.Equal(1, Total(view, "guests"));
        Assert.True(Total(view, "guestsAdded") >= 1);
        Assert.True(Total(view, "weakerChecks") > 0);
        Assert.Equal(Total(view, "weakerChecks"), Rows(view, "weakerChecks").Length);
        Assert.Contains(Rows(view, "weakerChecks"), r => r.GetProperty("key").GetString() == "mfa_required");
        var kind = Assert.Single(Rows(view, "alertKinds"));
        Assert.Equal("download_volume", kind.GetProperty("key").GetString());
        Assert.Equal(3, kind.GetProperty("count").GetInt64());

        // A security officer may open it; an employee may not; another tenant has no alerts.
        await world.AssignAsync(t, "e1", BuiltInRoles.SecurityOfficer, "tenant", null);
        await OkAsync(t["e1"].Client.GetAsync($"{Api}/security"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["guest"].Client.GetAsync($"{Api}/security")).StatusCode);
        var other = await PeopleAsync("asc", 1);
        Assert.Equal(0, Total(await OkAsync(other["admin"].Client.GetAsync($"{Api}/security")), "alerts"));

        // Each area is a report dashboard too, exported through the same service.
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.AnalyticsExport, new { enabled = true });
        var queued = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/exports", new { definition = new { dashboard = "security" }, format = "csv", name = "Security trends" }),
            HttpStatusCode.Accepted);
        Assert.Equal("security", queued.GetProperty("dashboard").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.PostAsJsonAsync($"{Api}/exports",
            new { definition = new { dashboard = "hr", target = "x" }, format = "csv" })).StatusCode);
    }
}
