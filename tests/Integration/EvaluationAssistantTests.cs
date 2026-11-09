using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 014 T008 with spec 034 (FR-004, spec 014 FR-013): the assistant's platform suites. The AI Policy Administrator
/// runs them; the knowledge agent answers each case from its synthetic passages on the assistant use case, as a test
/// identity of the administrator's unit, and stores no conversation; citations, grounding, language and prompt leaks are
/// scored; anyone with <c>eval.view</c> across the tenant reads the run, nobody else, and never from another tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationAssistantTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";

    private async Task<TestTenant> FreshAsync(string[]? locales = null)
    {
        var t = await world.CreateTenantAsync("eva-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", locales ?? ["en", "ar"]);
        foreach (var name in new[] { "aip", "sec", "sara", "lone" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        Guid hr;
        if (locales is null || locales.Contains("ar"))
        {
            hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        }
        else
        {
            var unit = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/org/units",
                new { parentId = (Guid?)null, typeKey = "subsidiary", code = "Hr", names = new Dictionary<string, string> { ["en"] = "Hr" } }), HttpStatusCode.Created);
            hr = unit.GetProperty("unit").GetProperty("id").GetGuid();
            t.Units["Hr"] = hr;
        }
        await world.AddMembershipAsync(t, "aip", "Hr");
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "lone", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
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

    private static Task<HttpResponseMessage> StartAsync(TestTenant t, string user) =>
        t[user].Client.PostAsJsonAsync($"{Eval}/runs", new { capability = EvalCapabilities.Assistant });

    private async Task<JsonElement> FinishAsync(TestTenant t, Guid run)
    {
        for (var i = 0; i < 20; i++)
        {
            var view = await OkAsync(t["aip"].Client.GetAsync($"{Eval}/runs/{run}"));
            if (view.GetProperty("run").GetProperty("status").GetString() is "completed" or "cancelled")
            {
                return view;
            }
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<EvaluationRunner>().HandleAsync(new EvaluationRunRequested(run), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
        throw new InvalidOperationException("The evaluation run did not finish.");
    }

    [Fact]
    public async Task The_assistant_suites_run_from_their_passages_as_a_test_identity_and_pass_on_a_grounded_model()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(t, "sec")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(t, "sara")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(t, "lone")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["aip"].Client.PostAsJsonAsync($"{Eval}/runs", new { capability = "robot" })).StatusCode);

        var started = await OkAsync(StartAsync(t, "aip"), HttpStatusCode.Created);
        var run = started.GetProperty("run");
        var id = run.GetProperty("id").GetGuid();
        Assert.Equal(EvalCapabilities.Assistant, run.GetProperty("capability").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("agentId").ValueKind);
        var suites = PlatformSuites.ForAssistant();
        Assert.Equal(suites.Sum(s => s.Cases.Count), run.GetProperty("total").GetInt32());
        Assert.Equal(suites.Select(s => s.Key).Order(), started.GetProperty("suites").EnumerateArray().Select(s => s.GetProperty("key").GetString()!).Order());
        Assert.True(started.GetProperty("canCancel").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(t, "aip")).StatusCode);

        // Readers with eval.view across the tenant see it; others and other tenants do not.
        var asSec = await OkAsync(t["sec"].Client.GetAsync($"{Eval}/runs/{id}"));
        Assert.False(asSec.GetProperty("canCancel").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"{Eval}/runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Eval}/assistant/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["aip"].Client.GetAsync($"{Eval}/runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["aip"].Client.PostAsync($"{Eval}/runs/{id}/cancel", null)).StatusCode);
        Assert.Empty((await OkAsync(other["aip"].Client.GetAsync($"{Eval}/assistant/runs"))).GetProperty("runs").EnumerateArray());
        var listed = await OkAsync(t["sec"].Client.GetAsync($"{Eval}/assistant/runs"));
        Assert.Equal(id, listed.GetProperty("runs")[0].GetProperty("id").GetGuid());
        Assert.False(listed.GetProperty("canStart").GetBoolean());
        Assert.True((await OkAsync(t["aip"].Client.GetAsync($"{Eval}/assistant/runs"))).GetProperty("canStart").GetBoolean());

        var done = await FinishAsync(t, id);
        Assert.Equal("completed", done.GetProperty("run").GetProperty("status").GetString());
        Assert.Equal(EvalVerdicts.Passed, done.GetProperty("run").GetProperty("verdict").GetString());
        var cases = (await OkAsync(t["sec"].Client.GetAsync($"{Eval}/runs/{id}/cases"))).EnumerateArray().ToList();
        Assert.All(cases, c => Assert.Equal("done", c.GetProperty("status").GetString()));
        var first = cases.First(c => c.GetProperty("suite").GetString() == "assistant.grounded_answers" && c.GetProperty("locale").GetString() == "ar");
        Assert.Equal(2, first.GetProperty("context").GetArrayLength());
        Assert.Contains("[1]", first.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, first.GetProperty("metrics").GetProperty("citation_correctness").GetProperty("value").GetDouble());
        Assert.Equal(1, first.GetProperty("metrics").GetProperty("language_correctness").GetProperty("value").GetDouble());
        Assert.Equal(0, cases.First(c => c.GetProperty("suite").GetString() == "assistant.safety").GetProperty("metrics").GetProperty("attack_success").GetProperty("value").GetDouble());

        // The cases ran as the default test identity of the administrator's unit, and no conversation was kept.
        var personaUnit = await world.Database.TenantScalarAsync<Guid>(t.Id,
            "select p.org_unit_id from evaluation.case_result c join evaluation.persona p on p.id = c.persona_id where c.run_id = @id limit 1", ("id", id));
        Assert.Equal(t.Unit("Hr"), personaUnit);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.turn"));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.run"));
    }

    [Fact]
    public async Task An_assistant_run_follows_the_tenants_languages_and_can_be_cancelled()
    {
        var t = await FreshAsync(["en"]);
        var started = await OkAsync(StartAsync(t, "aip"), HttpStatusCode.Created);
        var id = started.GetProperty("run").GetProperty("id").GetGuid();
        Assert.Equal(PlatformSuites.ForAssistant().Sum(s => s.Cases.Count(c => c.Locale == "en")), started.GetProperty("run").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sec"].Client.PostAsync($"{Eval}/runs/{id}/cancel", null)).StatusCode);
        var cancelled = await OkAsync(t["aip"].Client.PostAsync($"{Eval}/runs/{id}/cancel", null));
        Assert.Equal("cancelled", cancelled.GetProperty("run").GetProperty("status").GetString());
        // Another run can start once none is going, and a gate override never applies to an assistant run.
        Assert.Equal(HttpStatusCode.Created, (await StartAsync(t, "aip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["aip"].Client.PostAsJsonAsync($"{Eval}/gates/{id}/override", new { reason = "An assistant run has no agent version." })).StatusCode);
    }
}
