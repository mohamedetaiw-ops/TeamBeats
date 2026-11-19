using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T017 (FR-016, US8 scenario 3): the AI usage dashboard shows the AI Gateway's own numbers and the
/// assistant's, hides figures behind fewer people than the minimum group size, needs analytics.advanced and tenant wide
/// analytics.view, and stays in its tenant; a dashboard summary comes from the AI Gateway with the figures it used,
/// labelled as AI-generated, and is refused to people who may not see the dashboard.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AnalyticsAiTests(TenantWorld world)
{
    private const string Usage = "/api/v1/analytics/ai-usage";
    private const string Summaries = "/api/v1/analytics/summaries";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<TestTenant> AskedAsync(int people)
    {
        var t = await world.CreateTenantAsync("aiu-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        for (var i = 1; i <= people; i++)
        {
            await world.SeedUserAsync(t, "e" + i, MembershipTypes.Employee);
            await OkAsync(t["e" + i].Client.PostAsJsonAsync("/api/v1/assistant/turns", new { question = "Where do I park?" }));
        }
        return t;
    }

    [Fact]
    public async Task Ai_usage_shows_the_gateway_numbers_and_hides_small_groups()
    {
        var t = await AskedAsync(5);
        var admin = t["admin"].Client;
        var view = await OkAsync(admin.GetAsync(Usage));
        var gateway = await OkAsync(admin.GetAsync("/api/v1/ai/usage?groupBy=use_case"));
        // One number everywhere: the same requests and people as the AI usage page.
        Assert.Equal(gateway.GetProperty("totals").GetProperty("requests").GetInt64(), view.GetProperty("requests").GetInt64());
        Assert.Equal(gateway.GetProperty("totals").GetProperty("users").GetInt64(), view.GetProperty("users").GetInt64());
        Assert.True(view.GetProperty("requests").GetInt64() >= 5);
        var assistant = view.GetProperty("assistant");
        Assert.Equal(5, assistant.GetProperty("questions").GetInt64());
        Assert.Equal(5, assistant.GetProperty("users").GetInt64());
        // With nothing published the question is embedded for retrieval and never reaches a chat model.
        var embed = view.GetProperty("useCases").EnumerateArray().Single(u => u.GetProperty("group").GetString() == "embed");
        Assert.Equal(5, embed.GetProperty("users").GetInt64());
        Assert.Equal(0.0, assistant.GetProperty("answerRate").GetDouble());

        // Fewer people than the minimum: the assistant figures are hidden, the use case folds away.
        var small = await AskedAsync(2);
        var hidden = await OkAsync(small["admin"].Client.GetAsync(Usage));
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("assistant").GetProperty("questions").ValueKind);
        Assert.DoesNotContain(hidden.GetProperty("useCases").EnumerateArray(), u => u.GetProperty("group").GetString() == "embed");
        // Each tenant sees its own numbers only.
        Assert.True(hidden.GetProperty("requests").GetInt64() < view.GetProperty("requests").GetInt64());

        // An employee and a unit scoped viewer are refused; so is a package without advanced analytics.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e1"].Client.GetAsync(Usage)).StatusCode);
        var unit = await world.CreateUnitAsync(t, "Finance", null, "department");
        await world.AssignAsync(t, "e2", "analytics_viewer", "org_subtree", unit);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e2"].Client.GetAsync(Usage)).StatusCode);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.AnalyticsAdvanced, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(Usage)).StatusCode);
    }

    [Fact]
    public async Task A_summary_names_the_figures_it_used_and_follows_the_dashboard_access()
    {
        var t = await AskedAsync(5);
        var admin = t["admin"].Client;
        var summary = await OkAsync(admin.PostAsJsonAsync(Summaries, new { definition = new { dashboard = ReportDashboards.AiUsage }, language = "en" }));
        Assert.True(summary.GetProperty("aiGenerated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("denial").ValueKind);
        Assert.False(string.IsNullOrEmpty(summary.GetProperty("text").GetString()));
        var figure = Assert.Single(summary.GetProperty("figures").EnumerateArray());
        Assert.Contains(figure.GetProperty("value").GetRawText(), summary.GetProperty("text").GetString(), StringComparison.Ordinal);
        // The call went through the AI Gateway and is counted there.
        var after = await OkAsync(admin.GetAsync("/api/v1/ai/usage?groupBy=use_case"));
        Assert.Contains(after.GetProperty("rows").EnumerateArray(), r => r.GetProperty("key").GetString() == "summarize");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(Summaries, new { definition = new { dashboard = ReportDashboards.AiUsage }, language = "fr" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["e1"].Client.PostAsJsonAsync(Summaries, new { definition = new { dashboard = ReportDashboards.AiUsage } })).StatusCode);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.AnalyticsAdvanced, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(Summaries, new { definition = new { dashboard = ReportDashboards.Overview, metric = "active_people" } })).StatusCode);
    }
}
