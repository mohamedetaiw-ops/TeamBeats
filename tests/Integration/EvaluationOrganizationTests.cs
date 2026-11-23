using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R2 T023 (US10, FR-020): a unit sets thresholds only stricter than the platform, the tenant and the units
/// above, for itself and every unit below; the effective rule is the strictest of all levels; a unit's business datasets
/// are an inherited suite of every agent of the unit and below, and of no sibling unit; the level needs advanced
/// evaluation and stays inside the unit's scope and tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationOrganizationTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("eorg-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "fin", "aip", "hrp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var hr = await world.CreateUnitAsync(t, "Hr", group, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", group, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fin", BuiltInRoles.AgentDesigner, "org_subtree", finance);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "hrp", BuiltInRoles.AiPolicyAdministrator, "org_subtree", hr);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private async Task<Guid> AgentAsync(TestTenant t, string designer, string unit)
    {
        var detail = await OkAsync(t[designer].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit(unit) }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        definition["autonomy"] = "L0";
        await OkAsync(t[designer].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t[designer].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private static object Metric(string key, double? threshold = null, string? severity = null) => new { key, weight = 1, threshold, severity };

    private static Task<HttpResponseMessage> SaveUnitAsync(TestTenant t, string user, string unit, int version, object[] metrics, Guid[]? datasets = null) =>
        t[user].Client.PutAsJsonAsync($"{Eval}/units/{t.Unit(unit)}/settings", new { version, metrics, datasets = datasets ?? [] });

    private static JsonElement Rule(JsonElement view, string key) => view.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("key").GetString() == key);

    [Fact]
    public async Task A_unit_only_tightens_the_levels_above_and_its_rules_apply_to_its_agents_and_those_below_but_not_to_siblings()
    {
        var t = await FreshAsync();
        var hrAgent = await AgentAsync(t, "sara", "Hr");
        var financeAgent = await AgentAsync(t, "fin", "Finance");

        // The group tightens groundedness from the platform's 90%; a looser value is refused as below the platform.
        Assert.Equal("validation.eval.threshold_below_platform", await ProblemAsync(SaveUnitAsync(t, "aip", "Group", 0, [Metric("groundedness", 0.85)]),
            HttpStatusCode.BadRequest));
        var group = await OkAsync(SaveUnitAsync(t, "aip", "Group", 0, [Metric("groundedness", 0.93)]));
        Assert.Equal(1, group.GetProperty("version").GetInt32());
        Assert.Equal(0.93, Rule(group, "groundedness").GetProperty("threshold").GetDouble());
        Assert.Equal("org", Rule(group, "groundedness").GetProperty("thresholdLevel").GetString());
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a and target_id = @unit",
            ("a", AuditActions.EvalPolicyChanged), ("unit", t.Unit("Group").ToString())));

        // Hr may not loosen the group's rule, only tighten it; a stale version is refused.
        Assert.Equal("validation.eval.threshold_below_org", await ProblemAsync(SaveUnitAsync(t, "hrp", "Hr", 0, [Metric("groundedness", 0.91)]), HttpStatusCode.BadRequest));
        var hr = await OkAsync(SaveUnitAsync(t, "hrp", "Hr", 0, [Metric("groundedness", 0.95)]));
        Assert.Single(hr.GetProperty("above").EnumerateArray());
        Assert.True(hr.GetProperty("canEdit").GetBoolean());
        Assert.Equal("conflict.eval.unit_settings_version", await ProblemAsync(SaveUnitAsync(t, "hrp", "Hr", 0, [Metric("groundedness", 0.96)]), HttpStatusCode.Conflict));

        // Hr's administrator cannot reach the group or the sibling unit; Finance's designer cannot read Hr.
        await OkAsync(SaveUnitAsync(t, "hrp", "Group", 1, [Metric("groundedness", 0.99)]), HttpStatusCode.NotFound);
        await OkAsync(t["hrp"].Client.GetAsync($"{Eval}/units/{t.Unit("Finance")}/settings"), HttpStatusCode.NotFound);
        await OkAsync(t["fin"].Client.GetAsync($"{Eval}/units/{t.Unit("Hr")}/settings"), HttpStatusCode.NotFound);
        // A designer reads the settings of their unit but does not change them.
        var read = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/units/{t.Unit("Hr")}/settings"));
        Assert.False(read.GetProperty("canEdit").GetBoolean());
        await OkAsync(SaveUnitAsync(t, "sara", "Hr", 1, [Metric("groundedness", 0.97)]), HttpStatusCode.Forbidden);

        // The Hr agent's own suite may only be stricter than Hr's rule; the effective rule is the strictest of all levels.
        Assert.Equal("validation.eval.threshold_below_org", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{hrAgent}/suite",
            new { version = 0, metrics = new[] { Metric("groundedness", 0.94) }, datasets = Array.Empty<Guid>() }), HttpStatusCode.BadRequest));
        var hrSuites = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{hrAgent}/suites"));
        Assert.Equal(0.95, Rule(hrSuites, "groundedness").GetProperty("threshold").GetDouble());
        Assert.Equal(2, hrSuites.GetProperty("units").GetArrayLength());

        // The sibling unit's agent inherits only the group's rule.
        var financeSuites = await OkAsync(t["fin"].Client.GetAsync($"{Eval}/agents/{financeAgent}/suites"));
        Assert.Equal(0.93, Rule(financeSuites, "groundedness").GetProperty("threshold").GetDouble());
        Assert.Single(financeSuites.GetProperty("units").EnumerateArray());
    }

    [Fact]
    public async Task A_units_business_datasets_are_tested_on_every_agent_of_the_unit_as_an_inherited_suite()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t, "sara", "Hr");

        // A designer of the unit makes its business dataset; a designer of another unit cannot.
        await OkAsync(t["fin"].Client.PostAsJsonAsync($"{Eval}/datasets", new { orgUnitId = t.Unit("Hr"), names = new { en = "Hr policy cases" } }), HttpStatusCode.NotFound);
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { orgUnitId = t.Unit("Hr"), names = new { en = "Hr policy cases" } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new
        {
            version = 0,
            cases = new[] { new { input = "How many leave days do I get?", locale = "en", expect = new { keyFacts = new[] { "leave" } }, tags = new[] { "hr" } } },
        }));
        var financeDataset = (await OkAsync(t["fin"].Client.PostAsJsonAsync($"{Eval}/datasets", new { orgUnitId = t.Unit("Finance"), names = new { en = "Finance cases" } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["fin"].Client.GetAsync($"{Eval}/datasets/{dataset}"), HttpStatusCode.NotFound);

        // The unit's datasets are its own, and need the metrics they are scored on.
        Assert.Equal("validation.eval.unit_datasets", await ProblemAsync(SaveUnitAsync(t, "hrp", "Hr", 0, [Metric("groundedness")], [financeDataset]),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.unit_metrics", await ProblemAsync(SaveUnitAsync(t, "hrp", "Hr", 0, [], [dataset]), HttpStatusCode.BadRequest));
        var settings = await OkAsync(SaveUnitAsync(t, "hrp", "Hr", 0, [Metric("groundedness"), Metric("answer_correctness")], [dataset]));
        Assert.Equal(dataset, settings.GetProperty("ownDatasets").EnumerateArray().Single().GetProperty("id").GetGuid());

        // A run of the Hr agent includes the unit's suite with its cases, at the rules of every level.
        var run = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/runs", new { agentId = agent }), HttpStatusCode.Created);
        var suite = run.GetProperty("suites").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "org");
        Assert.Equal($"org:{t.Unit("Hr"):N}", suite.GetProperty("key").GetString());
        Assert.Equal(dataset, suite.GetProperty("datasets").EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.Equal(0.90, Rule(suite, "groundedness").GetProperty("threshold").GetDouble());
        var cases = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/runs/{run.GetProperty("run").GetProperty("id").GetGuid()}/cases"));
        Assert.Single(cases.EnumerateArray(), c => c.GetProperty("suite").GetString() == $"org:{t.Unit("Hr"):N}");

        // The sibling unit's agent is not tested on it.
        var financeAgent = await AgentAsync(t, "fin", "Finance");
        var financeRun = await OkAsync(t["fin"].Client.PostAsJsonAsync($"{Eval}/runs", new { agentId = financeAgent }), HttpStatusCode.Created);
        Assert.DoesNotContain(financeRun.GetProperty("suites").EnumerateArray(), s => s.GetProperty("kind").GetString() == "org");
    }

    [Fact]
    public async Task Without_advanced_evaluation_or_from_another_tenant_the_organization_level_is_closed()
    {
        var basic = await FreshAsync("workplace");
        var view = await OkAsync(basic["aip"].Client.GetAsync($"{Eval}/units/{basic.Unit("Hr")}/settings"));
        Assert.False(view.GetProperty("available").GetBoolean());
        Assert.False(view.GetProperty("canEdit").GetBoolean());
        await OkAsync(SaveUnitAsync(basic, "aip", "Hr", 0, [Metric("groundedness", 0.95)]), HttpStatusCode.Forbidden);
        await OkAsync(basic["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { orgUnitId = basic.Unit("Hr"), names = new { en = "Hr cases" } }), HttpStatusCode.Forbidden);

        var t = await FreshAsync();
        await OkAsync(SaveUnitAsync(t, "aip", "Hr", 0, [Metric("groundedness", 0.95)]));
        var other = await FreshAsync();
        await OkAsync(other["aip"].Client.GetAsync($"{Eval}/units/{t.Unit("Hr")}/settings"), HttpStatusCode.NotFound);
        await OkAsync(other["aip"].Client.PutAsJsonAsync($"{Eval}/units/{t.Unit("Hr")}/settings", new { version = 1, metrics = Array.Empty<object>() }),
            HttpStatusCode.NotFound);
    }
}
