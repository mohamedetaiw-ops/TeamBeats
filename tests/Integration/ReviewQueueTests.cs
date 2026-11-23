using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R2 T020 (US8 scenarios 2 and 3, FR-025): a scored production sample with a low score, a judge that was not
/// sure, a policy denial or a person's negative feedback goes to the evaluation reviewers in scope of the agent's unit,
/// as an inbox task. Content shows only while retained and when the reviewer may read its sources. A decision corrects
/// the scores and becomes a calibration label; the reviewed case is promoted to a dataset after redaction. Reviewers out
/// of scope, other tenants and packages without production evaluation see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ReviewQueueTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";
    private const string Fail = "[fake:judge:fail]";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("rvq-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "aip", "rev", "fin" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "rev", BuiltInRoles.EvaluationReviewer, "org_subtree", hr);
        await world.AssignAsync(t, "fin", BuiltInRoles.EvaluationReviewer, "org_subtree", finance);
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

    private async Task<Guid> AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        definition["autonomy"] = "L0";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    /// <summary>Offers a finished run as the runtime would and scores it as the worker would; returns the run's ID.</summary>
    private async Task<Guid> SampleAsync(TestTenant t, Guid agent, string answer, IReadOnlyList<EvalRunSource>? sources = null, int denials = 0,
        string question = "How many leave days do I get? Mail me at sam.lee@example.com")
    {
        var runId = Guid.NewGuid();
        var facts = new ProductionRunFacts(runId, agent, 1, t["emp"].Id, t.Unit("Hr"), "completed", "answered", null, "en", "internal",
            "openai/gpt-4o-mini", 120, 40, 900, 1, denials);
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IProductionSampler>().OfferAsync(facts, _ => Task.FromResult(new ProductionRunContent(question, answer,
                "Annual leave is 25 days.", [new EvalRunStep(1, "tool", "knowledge.search", "executed", null, null)], sources ?? [], [], null)), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        var sample = await world.Database.TenantScalarAsync<Guid>(t.Id, "select id from evaluation.production_sample where agent_run_id = @id", ("id", runId));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ProductionScorer>().HandleAsync(new ProductionSampleCaptured(sample), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        return runId;
    }

    private Task<Guid> ItemAsync(TestTenant t, Guid runId) => world.Database.TenantScalarAsync<Guid>(t.Id,
        "select i.id from evaluation.review_item i join evaluation.production_sample s on s.id = i.sample_id where s.agent_run_id = @id", ("id", runId));

    private Task<long> ItemsAsync(TestTenant t) => world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.review_item");

    private static async Task<List<string>> InboxSourcesAsync(HttpClient client)
    {
        var inbox = await OkAsync(client.GetAsync("/api/v1/tasks"));
        var items = inbox.ValueKind == JsonValueKind.Array ? inbox : inbox.GetProperty("items");
        return [.. items.EnumerateArray().Select(i => i.GetProperty("source").GetString()!)];
    }

    [Fact]
    public async Task A_low_score_goes_to_the_reviewer_in_scope_whose_decision_corrects_the_result_and_whose_case_can_be_promoted()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t);
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100, retentionDays = 10 }));
        // The judge fails groundedness and relevance: a low score.
        var runId = await SampleAsync(t, agent, "Annual leave is 25 days. Call 050 123 4567. " + Fail);
        var id = await ItemAsync(t, runId);
        Assert.Equal(1, await world.Database.OutboxCountAsync("EvaluationReviewRequested", t.Id));

        // The reviewer of the agent's unit has it in the queue and in the inbox; the reviewer of another unit does not.
        var queue = await OkAsync(t["rev"].Client.GetAsync($"{Eval}/review"));
        Assert.True(queue.GetProperty("available").GetBoolean());
        var listed = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Contains("low_score", listed.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("eval_review", await InboxSourcesAsync(t["rev"].Client));
        Assert.Empty((await OkAsync(t["fin"].Client.GetAsync($"{Eval}/review"))).GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("eval_review", await InboxSourcesAsync(t["fin"].Client));
        await OkAsync(t["fin"].Client.GetAsync($"{Eval}/review/{id}"), HttpStatusCode.NotFound);
        // The person who ran it has no review permission.
        await OkAsync(t["emp"].Client.GetAsync($"{Eval}/review/{id}"), HttpStatusCode.NotFound);

        // Retained content with no sources to check is shown, with the judge's verdicts and reasons.
        var item = await OkAsync(t["rev"].Client.GetAsync($"{Eval}/review/{id}"));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("contentWithheld").ValueKind);
        Assert.Contains("25 days", item.GetProperty("content").GetProperty("answer").GetString());
        Assert.Equal("fail", item.GetProperty("judge").GetProperty("groundedness").GetProperty("verdict").GetString());
        Assert.False(item.GetProperty("canPromote").GetBoolean());

        // Promotion waits for the decision; a verdict for a metric the judge did not score is refused.
        Assert.Equal("conflict.eval.review_not_decided", await ProblemAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/promote", new { datasetId = Guid.NewGuid() }),
            HttpStatusCode.Conflict));
        Assert.Equal("validation.eval.review_verdict", await ProblemAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/decision",
            new { verdicts = new { citation_correctness = "pass" } }), HttpStatusCode.BadRequest));

        // The reviewer overturns groundedness: the sample's score is corrected and both verdicts become calibration labels.
        var decided = await OkAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/decision",
            new { verdicts = new { groundedness = "pass", relevance = "pass" }, note = "The answer matches the policy." }));
        Assert.Equal("reviewed", decided.GetProperty("status").GetString());
        Assert.Equal("pass", decided.GetProperty("verdicts").GetProperty("groundedness").GetProperty("reviewer").GetString());
        Assert.Equal("fail", decided.GetProperty("verdicts").GetProperty("groundedness").GetProperty("judge").GetString());
        Assert.Equal(1, decided.GetProperty("metrics").GetProperty("groundedness").GetProperty("value").GetDouble());
        Assert.True(decided.GetProperty("canPromote").GetBoolean());
        Assert.Equal(2, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.calibration_label where label = 'pass'"));
        // The labels are redacted: no email address or phone number from the conversation is kept.
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from evaluation.calibration_label where input like '%sam.lee%' or answer like '%123 4567%'"));
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", AuditActions.EvalReviewVerdict)));
        Assert.Equal("conflict.eval.review_decided", await ProblemAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/decision", new { }),
            HttpStatusCode.Conflict));
        Assert.DoesNotContain("eval_review", await InboxSourcesAsync(t["rev"].Client));

        // Promoted to a dataset of the agent, redacted, tagged and as a version of its own kind.
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "From production" } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var promoted = await OkAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/promote", new { datasetId = dataset }));
        Assert.Equal(dataset, promoted.GetProperty("promotedDatasetId").GetGuid());
        var set = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/datasets/{dataset}"));
        var promotedCase = Assert.Single(set.GetProperty("cases").EnumerateArray());
        Assert.Contains("[email]", promotedCase.GetProperty("input").GetString());
        Assert.DoesNotContain("sam.lee", promotedCase.GetProperty("input").GetString());
        Assert.Contains("[number]", promotedCase.GetProperty("expect").GetProperty("answer").GetString());
        Assert.Contains("production_review", promotedCase.GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("review", set.GetProperty("versions")[0].GetProperty("source").GetString());
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", AuditActions.EvalReviewPromoted)));
        Assert.Equal("conflict.eval.review_promoted", await ProblemAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/promote", new { datasetId = dataset }),
            HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Good_runs_are_not_flagged_and_feedback_denials_and_unreadable_or_dropped_content_are_handled()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t);
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 }));

        // A run that scored well is not flagged.
        var good = await SampleAsync(t, agent, "Annual leave is 25 days.");
        Assert.Equal(0, await ItemsAsync(t));

        // The person then says the answer did not help: the feedback reaches evaluation and flags the sample.
        await world.Database.TenantScalarAsync<int>(t.Id,
            """
            with c as (insert into agents.conversation (tenant_id, id, user_id, title, classification, turns, created_at, last_turn_at)
              values (platform.current_tenant_id(), @conv, @user, 'Leave', 'internal', 1, now(), now()) returning id)
            insert into agents.turn (tenant_id, id, conversation_id, user_id, position, question, answer, outcome, language, classification, prompt_version, retrieval,
              run_id, created_at)
            select platform.current_tenant_id(), @turn, c.id, @user, 1, 'How many leave days?', 'Annual leave is 25 days.', 'answered', 'en', 'internal', 'v1', '{}', @run, now()
            from c returning 1
            """, ("conv", Guid.NewGuid()), ("turn", Guid.NewGuid()), ("user", t["emp"].Id), ("run", good));
        var turn = await world.Database.TenantScalarAsync<Guid>(t.Id, "select id from agents.turn where run_id = @run", ("run", good));
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"/api/v1/assistant/turns/{turn}/feedback", new { helpful = false, reason = "incorrect" }), HttpStatusCode.NoContent);
        Assert.Equal(1, await world.Database.OutboxCountAsync("AgentRunFeedbackGiven", t.Id));
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ReviewQueue>().HandleAsync(new AgentRunFeedbackGiven(good, false), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        var feedback = await OkAsync(t["rev"].Client.GetAsync($"{Eval}/review/{await ItemAsync(t, good)}"));
        Assert.Equal(["negative_feedback"], feedback.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToArray());
        // Without retention the content is gone once scored: nothing to show and nothing to promote, and the judge's reason went with it.
        Assert.Equal("not_retained", feedback.GetProperty("contentWithheld").GetString());
        Assert.Equal(JsonValueKind.Null, feedback.GetProperty("judge").GetProperty("groundedness").GetProperty("reason").ValueKind);

        // A policy denial is flagged too.
        var denied = await SampleAsync(t, agent, "Annual leave is 25 days.", denials: 1);
        var deniedItem = await OkAsync(t["rev"].Client.GetAsync($"{Eval}/review/{await ItemAsync(t, denied)}"));
        Assert.Contains("policy_denial", deniedItem.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        await OkAsync(t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{deniedItem.GetProperty("id").GetGuid()}/decision", new { }));
        Assert.Equal("conflict.eval.review_no_content", await ProblemAsync(
            t["rev"].Client.PostAsJsonAsync($"{Eval}/review/{deniedItem.GetProperty("id").GetGuid()}/promote", new { datasetId = Guid.NewGuid() }), HttpStatusCode.Conflict));

        // Retained content that read a source the reviewer cannot open is withheld from them.
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { retentionDays = 10 }));
        var hidden = await SampleAsync(t, agent, "Annual leave is 25 days. " + Fail, [new EvalRunSource(1, "content", Guid.NewGuid())]);
        var hiddenItem = await OkAsync(t["rev"].Client.GetAsync($"{Eval}/review/{await ItemAsync(t, hidden)}"));
        Assert.Equal("not_permitted", hiddenItem.GetProperty("contentWithheld").GetString());
        Assert.Equal(JsonValueKind.Null, hiddenItem.GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task Other_tenants_and_packages_without_production_evaluation_see_no_queue()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t);
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 }));
        var id = await ItemAsync(t, await SampleAsync(t, agent, "No. " + Fail));

        var other = await FreshAsync();
        await OkAsync(other["rev"].Client.GetAsync($"{Eval}/review/{id}"), HttpStatusCode.NotFound);
        await OkAsync(other["rev"].Client.PostAsJsonAsync($"{Eval}/review/{id}/decision", new { }), HttpStatusCode.NotFound);
        Assert.Empty((await OkAsync(other["rev"].Client.GetAsync($"{Eval}/review"))).GetProperty("items").EnumerateArray());

        var basic = await FreshAsync("workplace");
        var queue = await OkAsync(basic["rev"].Client.GetAsync($"{Eval}/review"));
        Assert.False(queue.GetProperty("available").GetBoolean());
        Assert.DoesNotContain("eval_review", await InboxSourcesAsync(basic["rev"].Client));
        await OkAsync(basic["rev"].Client.GetAsync($"{Eval}/review/{Guid.NewGuid()}"), HttpStatusCode.Forbidden);
    }
}
