using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 T014 (US8 scenarios 1 and 4, FR-011): an Evaluation Reviewer labels answers for a judged rubric; the AI
/// Policy Administrator measures the judge against them per locale; too few labels change nothing; agreement at the
/// target calibrates the rubric and below it demotes the rubric, whose blocking thresholds then count as warnings in
/// new runs, and the person who asked is told; each tenant's labels and calibrations are its own.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationCalibrationTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";
    private const string Fail = "[fake:judge:fail]";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("evc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "rev", "aip", "sara", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AssignAsync(t, "rev", BuiltInRoles.EvaluationReviewer, "tenant", null);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    private static Task LabelAsync(TestTenant t, string metric, string answer, string label, string locale = "en") =>
        OkAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/calibration/{metric}/labels",
            new { locale, input = "How many days of annual leave do I get?", expectedAnswer = "25 days a year.", answer, label }), HttpStatusCode.Created);

    /// <summary>Starts a calibration as the AI Policy Administrator and measures it the way the worker does.</summary>
    private async Task<JsonElement> CalibrateAsync(TestTenant t, string metric)
    {
        var started = await OkAsync(t["aip"].Client.PostAsync($"{Eval}/calibration/{metric}/runs", null), HttpStatusCode.Created);
        var id = started.GetProperty("id").GetGuid();
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<CalibrationRunner>().HandleAsync(new CalibrationRequested(id), CancellationToken.None);
            return true;
        });
        return await OkAsync(t["aip"].Client.GetAsync($"{Eval}/calibration/runs/{id}"));
    }

    private static async Task<JsonElement> StateAsync(TestTenant t, string metric) =>
        (await OkAsync(t["aip"].Client.GetAsync($"{Eval}/calibration"))).EnumerateArray().Single(r => r.GetProperty("metric").GetString() == metric);

    [Fact]
    public async Task Labels_need_a_reviewer_and_too_few_labels_leave_the_platform_rubric_as_it_is()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t["aip"].Client.PostAsJsonAsync($"{Eval}/calibration/relevance/labels",
            new { locale = "en", input = "Hi", answer = "Hello", label = "pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync($"{Eval}/calibration")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["rev"].Client.PostAsync($"{Eval}/calibration/relevance/runs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["rev"].Client.GetAsync($"{Eval}/calibration/unknown_metric/labels")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["rev"].Client.PostAsJsonAsync($"{Eval}/calibration/answer_correctness/labels",
            new { locale = "en", input = "Hi", answer = "Hello", label = "pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["rev"].Client.PostAsJsonAsync($"{Eval}/calibration/relevance/labels",
            new { locale = "fr", input = "Hi", answer = "Hello", label = "maybe" })).StatusCode);

        for (var i = 0; i < 10; i++)
        {
            await LabelAsync(t, "relevance", $"Annual leave is 25 days ({i}).", "pass");
        }
        var result = await CalibrateAsync(t, "relevance");
        Assert.Equal(CalibrationOutcomes.Insufficient, result.GetProperty("outcome").GetString());
        Assert.Equal(RubricStates.Platform, (await StateAsync(t, "relevance")).GetProperty("state").GetString());
        Assert.Equal(10, (await StateAsync(t, "relevance")).GetProperty("labels").GetProperty("en").GetInt32());
        // The page offers each person only what they may do: reviewers label, the AI Policy Administrator calibrates.
        var forAip = await StateAsync(t, "relevance");
        Assert.True(forAip.GetProperty("canCalibrate").GetBoolean());
        Assert.False(forAip.GetProperty("canLabel").GetBoolean());
        var forReviewer = (await OkAsync(t["rev"].Client.GetAsync($"{Eval}/calibration"))).EnumerateArray().Single(r => r.GetProperty("metric").GetString() == "relevance");
        Assert.True(forReviewer.GetProperty("canLabel").GetBoolean());
        Assert.False(forReviewer.GetProperty("canCalibrate").GetBoolean());
        Assert.Equal(0, await AuditCountAsync(t, AuditActions.EvalRubricCalibrated));

        // Labels and calibrations belong to their tenant.
        var theirs = (await OkAsync(other["aip"].Client.GetAsync($"{Eval}/calibration"))).EnumerateArray().Single(r => r.GetProperty("metric").GetString() == "relevance");
        Assert.Equal(0, theirs.GetProperty("labels").GetProperty("en").GetInt32());
        Assert.Empty((await OkAsync(other["aip"].Client.GetAsync($"{Eval}/calibration/relevance/labels"))).EnumerateArray());
        var label = (await OkAsync(t["rev"].Client.GetAsync($"{Eval}/calibration/relevance/labels")))[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other["rev"].Client.DeleteAsync($"{Eval}/calibration/labels/{label}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["rev"].Client.DeleteAsync($"{Eval}/calibration/labels/{label}")).StatusCode);
    }

    [Fact]
    public async Task Agreement_at_the_target_calibrates_and_below_it_demotes_the_rubric_to_warning_until_recalibrated()
    {
        var t = await FreshAsync();
        // 45 answers people pass and the judge passes, 5 they fail and the judge fails: full agreement.
        for (var i = 0; i < CalibrationService.MinLabels; i++)
        {
            await LabelAsync(t, "answer_correctness", i < 45 ? $"Annual leave is 25 days ({i})." : $"Annual leave is 10 days ({i}). {Fail}", i < 45 ? "pass" : "fail");
        }
        var calibrated = await CalibrateAsync(t, "answer_correctness");
        Assert.Equal(CalibrationOutcomes.Calibrated, calibrated.GetProperty("outcome").GetString());
        var en = calibrated.GetProperty("agreement").EnumerateArray().Single(a => a.GetProperty("locale").GetString() == "en");
        Assert.Equal(1.0, en.GetProperty("value").GetDouble());
        Assert.Equal(CalibrationOutcomes.Insufficient, calibrated.GetProperty("agreement").EnumerateArray().Single(a => a.GetProperty("locale").GetString() == "ar")
            .GetProperty("status").GetString());
        Assert.Equal(RubricStates.Calibrated, (await StateAsync(t, "answer_correctness")).GetProperty("state").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalRubricCalibrated));

        // Ten answers people fail that the judge passes take agreement to 50 of 60, below 85%: the rubric is demoted.
        for (var i = 0; i < 10; i++)
        {
            await LabelAsync(t, "answer_correctness", $"Annual leave is 25 days, plus whatever you like ({i}).", "fail");
        }
        var demoted = await CalibrateAsync(t, "answer_correctness");
        Assert.Equal(CalibrationOutcomes.Demoted, demoted.GetProperty("outcome").GetString());
        Assert.Equal(RubricStates.Demoted, (await StateAsync(t, "answer_correctness")).GetProperty("state").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalRubricDemoted));
        var links = (await OkAsync(t["aip"].Client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString());
        Assert.Contains("/admin/evaluation/calibration", links);

        // New runs count the demoted rubric's blocking threshold as a warning.
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var agent = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var run = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/runs", new { agentId = agent }), HttpStatusCode.Created);
        var rule = run.GetProperty("suites").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "template.hr_assistant")
            .GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("key").GetString() == "answer_correctness");
        Assert.Equal(EvalSeverities.Warning, rule.GetProperty("severity").GetString());
        Assert.Equal("calibration", rule.GetProperty("severityLevel").GetString());
    }
}
