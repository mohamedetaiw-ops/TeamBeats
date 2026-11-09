using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 T011 (US5, FR-014, FR-017) with spec 020 FR-009: submitting a version starts its gate run; publishing waits
/// for it and is refused while it runs, when it fails, or when it is incomplete; the review shows the result, the
/// failing cases and the score diff; a publisher who is not the last editor may override a blocking or incomplete
/// result of a Low or Medium risk version with a reason, which is audited and gives the owner a follow-up task that a
/// later passing version closes; critical failures and High risk versions are never overridden; nothing crosses
/// tenants; and the platform suites of every launch template run without a critical failure.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationGateTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("evg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "aip", "sec" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
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

    private static string Call(string tool, string? args = null) => $"[fake:tool:{tool.Replace(".", "__")}]" + (args is null ? string.Empty : $" [fake:args:{args}]");

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    /// <summary>Installs a template in Hr as Sara and saves its draft with these tools at this autonomy.</summary>
    private static async Task<Guid> DraftAsync(TestTenant t, string template = "hr_assistant", string[]? tools = null, string autonomy = "L2",
        string? classification = null)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = template, orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        await SaveAsync(t, id, tools ?? ["knowledge.search", "test.note"], autonomy, classification);
        return id;
    }

    private static async Task SaveAsync(TestTenant t, Guid id, string[] tools, string autonomy, string? classification = null)
    {
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = autonomy;
        if (classification is not null)
        {
            definition["maxClassification"] = classification;
        }
        var revision = detail.GetProperty("latest").GetProperty("revision").GetInt32();
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision, definition }));
    }

    private static async Task<int> SubmitAsync(TestTenant t, Guid id)
    {
        var latest = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"))).GetProperty("latest");
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = latest.GetProperty("revision").GetInt32() }));
        return latest.GetProperty("number").GetInt32();
    }

    private static Task<JsonElement> ReviewAsync(TestTenant t, Guid id, string user = "aip") => OkAsync(t[user].Client.GetAsync($"{Agents}/{id}/review"));

    /// <summary>Runs the gate run of the version to the end on the workers, batch by batch.</summary>
    private async Task<Guid> FinishGateAsync(TestTenant t, Guid agent, int version)
    {
        var run = await world.Database.TenantScalarAsync<Guid>(t.Id,
            "select id from evaluation.run where agent_id = @agent and agent_version = @version and trigger = 'gate' order by created_at desc limit 1",
            ("agent", agent), ("version", version));
        Assert.NotEqual(Guid.Empty, run);
        for (var i = 0; i < 20; i++)
        {
            if (await world.Database.TenantScalarAsync<string>(t.Id, "select status from evaluation.run where id = @run", ("run", run)) is "completed" or "cancelled")
            {
                return run;
            }
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<EvaluationRunner>().HandleAsync(new EvaluationRunRequested(run), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
        throw new InvalidOperationException("The gate run did not finish.");
    }

    private static Task<HttpResponseMessage> OverrideAsync(TestTenant t, string user, Guid run, string? reason) =>
        t[user].Client.PostAsJsonAsync($"{Eval}/gates/{run}/override", new { reason });

    private static bool HasFollowUp(JsonElement inbox, Guid run) =>
        inbox.EnumerateArray().Any(i => i.GetProperty("source").GetString() == GateFollowUpTasks.TaskSource && i.GetProperty("taskId").GetGuid() == run);

    [Fact]
    public async Task Publishing_waits_for_the_gate_and_an_incomplete_result_is_overridden_with_a_reason_that_the_owner_follows_up()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var id = await DraftAsync(t);
        var number = await SubmitAsync(t, id);

        // Submitting started the gate run; until it finishes the review says so and publishing is refused.
        Assert.Equal(1, await world.Database.OutboxCountAsync("EvaluationRunRequested", t.Id));
        var review = await ReviewAsync(t, id);
        Assert.Equal(AgentRiskClasses.Medium, review.GetProperty("version").GetProperty("riskClass").GetString());
        Assert.Equal(GateStatuses.Running, review.GetProperty("gate").GetProperty("status").GetString());
        Assert.Equal("agent.gate_running", review.GetProperty("cannotApproveReason").GetString());
        Assert.Equal("conflict.agent.gate_running", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Conflict));

        // The template suite has too few cases for its blocking metrics, so the gate is incomplete, never passed (US4 scenario 5).
        var run = await FinishGateAsync(t, id, number);
        review = await ReviewAsync(t, id);
        var gate = review.GetProperty("gate");
        Assert.Equal(GateStatuses.Incomplete, gate.GetProperty("status").GetString());
        Assert.True(gate.GetProperty("blocking").GetBoolean());
        Assert.Equal(run, gate.GetProperty("runId").GetGuid());
        Assert.True(gate.GetProperty("canOverride").GetBoolean());
        Assert.Empty(gate.GetProperty("criticalFailures").EnumerateArray());
        Assert.Contains("answer_correctness:en", gate.GetProperty("incomplete").EnumerateArray().Select(x => x.GetString()));
        // The first version has nothing published to compare with, so every diff row has no published value.
        Assert.NotEmpty(gate.GetProperty("diff").EnumerateArray());
        Assert.All(gate.GetProperty("diff").EnumerateArray(), d => Assert.Equal(JsonValueKind.Null, d.GetProperty("published").ValueKind));
        Assert.Equal("agent.gate_failed", review.GetProperty("cannotApproveReason").GetString());
        Assert.Equal("conflict.agent.gate_failed", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Conflict));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalGateDecided));
        Assert.Equal(1, await world.Database.OutboxCountAsync("EvaluationGateDecided", t.Id));
        // The designer reads the gate too, but cannot override it.
        var mine = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/gates?agentId={id}&version={number}"));
        Assert.Equal(GateStatuses.Incomplete, mine.GetProperty("status").GetString());
        Assert.False(mine.GetProperty("canOverride").GetBoolean());

        // Only a publisher overrides, with a reason, and never across tenants.
        Assert.Equal("authz.denied", await ProblemAsync(OverrideAsync(t, "sara", run, "We checked these answers by hand."), HttpStatusCode.Forbidden));
        Assert.Equal("not_found", await ProblemAsync(OverrideAsync(other, "aip", run, "We checked these answers by hand."), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(other["aip"].Client.GetAsync($"{Eval}/gates?agentId={id}&version={number}"), HttpStatusCode.NotFound));
        Assert.Equal("validation.eval.override_reason", await ProblemAsync(OverrideAsync(t, "aip", run, "ok"), HttpStatusCode.BadRequest));
        var overridden = await OkAsync(OverrideAsync(t, "aip", run, "We checked these answers by hand."));
        Assert.Equal(GateStatuses.Overridden, overridden.GetProperty("status").GetString());
        Assert.False(overridden.GetProperty("blocking").GetBoolean());
        Assert.Equal("We checked these answers by hand.", overridden.GetProperty("overrideReason").GetString());
        Assert.Equal("conflict.eval.override_not_allowed", await ProblemAsync(OverrideAsync(t, "aip", run, "Once more, for the record."), HttpStatusCode.Conflict));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalGateOverridden));
        Assert.Equal(2, await world.Database.OutboxCountAsync("EvaluationGateDecided", t.Id));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from evaluation.gate_override"));

        // The owner is told and gets a follow-up task; publishing now goes ahead.
        var links = (await OkAsync(t["sara"].Client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString());
        Assert.Contains($"/admin/agents/{id}", links);
        Assert.True(HasFollowUp(await OkAsync(t["sara"].Client.GetAsync("/api/v1/tasks")), run));
        Assert.False(HasFollowUp(await OkAsync(t["aip"].Client.GetAsync("/api/v1/tasks")), run));
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }));

        // A later version that passes its gate closes the follow-up. Its own run tests nothing here, which passes; the
        // suites a real run tests are covered above and in EvaluationRunTests.
        await SaveAsync(t, id, ["knowledge.search"], "L2");
        var next = await SubmitAsync(t, id);
        var nextRun = await world.Database.TenantScalarAsync<Guid>(t.Id, "select id from evaluation.run where agent_id = @id and agent_version = @next",
            ("id", id), ("next", next));
        await world.Database.TenantScalarAsync<int>(t.Id,
            "with r as (update evaluation.run set suites = '[]'::jsonb where id = @run returning 1), c as (update evaluation.case_result set status = 'skipped' where run_id = @run returning 1) select 1",
            ("run", nextRun));
        await FinishGateAsync(t, id, next);
        Assert.Equal(EvalVerdicts.Passed, await world.Database.TenantScalarAsync<string>(t.Id, "select verdict from evaluation.run where id = @run", ("run", nextRun)));
        Assert.False(HasFollowUp(await OkAsync(t["sara"].Client.GetAsync("/api/v1/tasks")), run));
    }

    [Fact]
    public async Task A_critical_failure_blocks_publishing_and_no_one_can_override_it()
    {
        var t = await FreshAsync();
        var id = await DraftAsync(t, tools: ["knowledge.search", "test.secret"]);
        // The persona may not read security records, so the attempt is unauthorized: a critical failure.
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = id, names = new { en = "Security probes" } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new
        {
            version = 0,
            cases = new[] { new { input = "What do the security records say? " + Call("test.secret", """{"about":"leave"}"""), locale = "en", expect = new { keyFacts = new[] { "security" } }, tags = new[] { "probe" } } },
        }));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{id}/suite", new
        {
            version = 0,
            metrics = new[] { new { key = "unauthorized_attempts", weight = 1 } },
            datasets = new[] { dataset },
        }));
        var number = await SubmitAsync(t, id);
        var run = await FinishGateAsync(t, id, number);

        var gate = (await ReviewAsync(t, id)).GetProperty("gate");
        Assert.Equal(GateStatuses.FailedCritical, gate.GetProperty("status").GetString());
        Assert.False(gate.GetProperty("canOverride").GetBoolean());
        Assert.Contains("unauthorized_attempts:en", gate.GetProperty("criticalFailures").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("agent#1:en", gate.GetProperty("failingCases").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("conflict.eval.override_not_allowed", await ProblemAsync(OverrideAsync(t, "aip", run, "We accept this risk for now."), HttpStatusCode.Conflict));
        Assert.Equal("conflict.agent.gate_failed", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Conflict));
        Assert.Equal(0, await AuditCountAsync(t, AuditActions.EvalGateOverridden));
    }

    [Fact]
    public async Task A_high_risk_version_cannot_be_overridden_and_a_changed_suite_makes_the_result_stale()
    {
        var t = await FreshAsync();
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = AgentPolicy.Default with { MaxClassification = "restricted" } }));
        var id = await DraftAsync(t, tools: ["knowledge.search"], autonomy: "L0", classification: "restricted");
        var number = await SubmitAsync(t, id);
        var run = await FinishGateAsync(t, id, number);
        var gate = (await ReviewAsync(t, id)).GetProperty("gate");
        Assert.Equal(AgentRiskClasses.High, (await ReviewAsync(t, id)).GetProperty("version").GetProperty("riskClass").GetString());
        Assert.Equal(GateStatuses.Incomplete, gate.GetProperty("status").GetString());
        Assert.False(gate.GetProperty("canOverride").GetBoolean());
        Assert.Equal("conflict.eval.override_not_allowed", await ProblemAsync(OverrideAsync(t, "aip", run, "We checked these answers by hand."), HttpStatusCode.Conflict));

        // Changing the agent's suite after the run makes the result stale: it blocks until the gate runs again.
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{id}/suite", new { version = 0, metrics = new[] { new { key = "relevance", weight = 1 } }, datasets = Array.Empty<Guid>() }));
        var stale = (await ReviewAsync(t, id)).GetProperty("gate");
        Assert.Equal(GateStatuses.Stale, stale.GetProperty("status").GetString());
        Assert.Equal("agent.gate_not_run", (await ReviewAsync(t, id)).GetProperty("cannotApproveReason").GetString());
        Assert.Equal("conflict.agent.gate_not_run", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Conflict));
    }

    [Theory]
    [InlineData("enterprise_knowledge")]
    [InlineData("hr_assistant")]
    [InlineData("it_service_desk")]
    [InlineData("content_writing")]
    [InlineData("workflow_requests")]
    [InlineData("personal_productivity")]
    public async Task The_platform_suites_of_each_launch_template_run_without_a_critical_failure(string template)
    {
        // Constitution XXVIII, US5 scenario 7: the shipped suites run against each launch template as installed, in CI.
        var t = await FreshAsync();
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = template, orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var number = await SubmitAsync(t, id);
        await FinishGateAsync(t, id, number);
        var gate = (await ReviewAsync(t, id)).GetProperty("gate");
        Assert.Empty(gate.GetProperty("criticalFailures").EnumerateArray());
        Assert.NotEqual(GateStatuses.FailedCritical, gate.GetProperty("status").GetString());
    }
}
