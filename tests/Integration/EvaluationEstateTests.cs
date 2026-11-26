using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R3 (US6 at estate level): trend benchmarks across the tenant's agents. Each agent's latest evaluated version is
/// compared with the one before it, a regression counts only beyond the noise margin and in the metric's bad direction,
/// monthly benchmarks keep locales apart, only readable agents count, the view needs <c>ai.evals.advanced</c>, and nothing
/// crosses tenants. Results are written straight into the run table so the figures are exact. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationEstateTests(TenantWorld world)
{
    private const string Trends = "/api/v1/evaluation/estate/trends";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("est-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "fadi", "sec", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", finance);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> AgentAsync(TestTenant t, string designer, string unit) =>
        (await OkAsync(t[designer].Client.PostAsJsonAsync("/api/v1/agents", new { templateId = "enterprise_knowledge", orgUnitId = t.Unit(unit) }), HttpStatusCode.Created))
            .GetProperty("agent").GetProperty("id").GetGuid();

    /// <summary>A completed evaluation run of a version with one metric per locale.</summary>
    private Task RunAsync(TestTenant t, Guid agent, string unit, int version, DateTimeOffset finished, string metric, params (string Locale, double Value)[] values)
    {
        var result = new RunResult("passed", [], [], [], [], [], new Dictionary<string, int?>(), new Dictionary<string, double?>(), null, [],
            [new MetricScore(metric, 1, null, null, null, values.Select(v => new LocaleScore(v.Locale, 20, v.Value, 0, 0, MetricStates.Pass)).ToList(), null)]);
        return world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into evaluation.run (tenant_id, id, agent_id, agent_version, org_unit_id, trigger, status, suites, total, done, verdict, result, created_at, finished_at)
            values (@tenant, @id, @agent, @version, @unit, 'gate', 'completed', '[]', 20, 20, 'passed', @result::jsonb, @at, @at)
            """, ("tenant", t.Id), ("id", Guid.NewGuid()), ("agent", agent), ("version", version), ("unit", t.Unit(unit)),
            ("result", JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))), ("at", finished));
    }

    [Fact]
    public async Task The_estate_compares_each_agents_versions_and_benchmarks_each_locale_by_month()
    {
        var t = await FreshAsync();
        var hr = await AgentAsync(t, "sara", "Hr");
        var finance = await AgentAsync(t, "fadi", "Finance");
        var now = DateTimeOffset.UtcNow;
        var earlier = now.AddMonths(-2);
        await RunAsync(t, hr, "Hr", 1, earlier, "answer_correctness", ("en", 0.90), ("ar", 0.88));
        // Version 2 drops in English beyond the noise margin and moves within it in Arabic.
        await RunAsync(t, hr, "Hr", 2, now.AddMinutes(-5), "answer_correctness", ("en", 0.80), ("ar", 0.89));
        // A metric where lower is better: a rise is the regression.
        await RunAsync(t, finance, "Finance", 1, earlier, "unauthorized_disclosures", ("en", 0));
        await RunAsync(t, finance, "Finance", 2, now.AddMinutes(-4), "unauthorized_disclosures", ("en", 0.01));

        var view = await OkAsync(t["sec"].Client.GetAsync(Trends));
        Assert.Equal(EstateTrends.Months, view.GetProperty("months").GetArrayLength());
        var agents = view.GetProperty("agents").EnumerateArray().ToList();
        Assert.Equal(2, agents.Count);
        var hrRow = agents.Single(a => a.GetProperty("agentId").GetGuid() == hr);
        Assert.True(hrRow.GetProperty("regressed").GetBoolean());
        Assert.Equal(2, hrRow.GetProperty("version").GetInt32());
        Assert.Equal(1, hrRow.GetProperty("previousVersion").GetInt32());
        var english = hrRow.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("locale").GetString() == "en");
        Assert.Equal(-0.1, english.GetProperty("delta").GetDouble(), 4);
        Assert.True(english.GetProperty("regressed").GetBoolean());
        var arabic = hrRow.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("locale").GetString() == "ar");
        Assert.False(arabic.GetProperty("regressed").GetBoolean());
        // A rise of 0.01 in disclosures is within the noise margin, so it is not a regression.
        Assert.False(agents.Single(a => a.GetProperty("agentId").GetGuid() == finance).GetProperty("regressed").GetBoolean());
        // The regressed agent comes first.
        Assert.Equal(hr, agents[0].GetProperty("agentId").GetGuid());

        // Benchmarks per metric and locale, one point per month, never mixing locales.
        var series = view.GetProperty("benchmarks").EnumerateArray().ToList();
        var en = series.Single(s => s.GetProperty("metric").GetString() == "answer_correctness" && s.GetProperty("locale").GetString() == "en");
        Assert.Equal([0.90, 0.80], en.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("value").GetDouble()));
        Assert.Equal(earlier.UtcDateTime.ToString("yyyy-MM"), en.GetProperty("points")[0].GetProperty("month").GetString());
        var ar = series.Single(s => s.GetProperty("metric").GetString() == "answer_correctness" && s.GetProperty("locale").GetString() == "ar");
        Assert.Equal([0.88, 0.89], ar.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("value").GetDouble()));
        Assert.All(series.SelectMany(s => s.GetProperty("points").EnumerateArray()), p => Assert.Equal(1, p.GetProperty("agents").GetInt32()));
    }

    [Fact]
    public async Task Only_readable_agents_count_the_package_decides_and_nothing_crosses_tenants()
    {
        var t = await FreshAsync();
        var hr = await AgentAsync(t, "sara", "Hr");
        var finance = await AgentAsync(t, "fadi", "Finance");
        await RunAsync(t, hr, "Hr", 1, DateTimeOffset.UtcNow.AddMinutes(-3), "groundedness", ("en", 0.95));
        await RunAsync(t, finance, "Finance", 1, DateTimeOffset.UtcNow.AddMinutes(-2), "groundedness", ("en", 0.70));

        // Sara designs agents in Hr only: the Finance agent and its figures are not in her estate.
        var sara = await OkAsync(t["sara"].Client.GetAsync(Trends));
        Assert.Equal([hr], sara.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("agentId").GetGuid()));
        var point = Assert.Single(Assert.Single(sara.GetProperty("benchmarks").EnumerateArray()).GetProperty("points").EnumerateArray());
        Assert.Equal(0.95, point.GetProperty("value").GetDouble());
        // An employee with no evaluation permission has an empty estate.
        Assert.Empty((await OkAsync(t["emp"].Client.GetAsync(Trends))).GetProperty("agents").EnumerateArray());

        // Another tenant sees none of these agents or runs.
        var other = await FreshAsync();
        var otherView = await OkAsync(other["sec"].Client.GetAsync(Trends));
        Assert.Empty(otherView.GetProperty("agents").EnumerateArray());
        Assert.Empty(otherView.GetProperty("benchmarks").EnumerateArray());

        // A package without ai.evals.advanced has no estate trends.
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["sec"].Client.GetAsync(Trends)).StatusCode);
    }
}
