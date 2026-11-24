using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R2 T021 (US9 scenarios 2 and 3, FR-026): a designer proposes comparing the agent's version in force with
/// another version or another model; the AI Policy Administrator other than the proposer approves it only once the
/// challenger passed its gate; a share of people gets the challenger, each always on the same arm, and both arms are
/// measured by production evaluation; stopping it returns everyone to the champion. Packages without experiments,
/// people outside the agent's scope and other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ComparisonTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";
    private static readonly string[] People = ["e1", "e2", "e3", "e4", "e5", "e6"];

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("cmp-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "aip", "aip2", "out" }.Concat(People))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        foreach (var name in People)
        {
            await world.AddMembershipAsync(t, name, "Hr");
        }
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "out", BuiltInRoles.AgentDesigner, "org_subtree", finance);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "aip2", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
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

    private async Task SaveAsync(TestTenant t, Guid id, string instructions)
    {
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        definition["autonomy"] = "L0";
        definition["instructions"] = new Dictionary<string, string> { ["en"] = instructions, ["ar"] = "أجب عن أسئلة سياسات الشركة." };
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = detail.GetProperty("latest").GetProperty("revision").GetInt32(), definition }));
    }

    private async Task<int> SubmitAsync(TestTenant t, Guid id)
    {
        var latest = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"))).GetProperty("latest");
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = latest.GetProperty("revision").GetInt32() }));
        return latest.GetProperty("number").GetInt32();
    }

    private async Task<Guid> AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        await SaveAsync(t, id, "Answer questions about the company's policies.");
        var number = await SubmitAsync(t, id);
        await world.Database.PassGateAsync(t.Id, id, number);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }));
        return id;
    }

    private async Task<(int Version, string? Requested, string? Arm)> RunAsync(TestTenant t, Guid agent, string user)
    {
        var run = await OkAsync(t[user].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "How many leave days do I get?" }));
        var id = run.GetProperty("id").GetGuid();
        var rows = await world.Database.TenantScalarAsync<string>(t.Id,
            "select agent_version || '|' || coalesce(requested_model, '') || '|' || coalesce(arm, '') from agents.run where id = @id", ("id", id));
        var parts = rows!.Split('|');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1].Length == 0 ? null : parts[1], parts[2].Length == 0 ? null : parts[2]);
    }

    private Task<long> AuditAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    [Fact]
    public async Task A_version_challenger_runs_for_its_share_once_approved_by_another_administrator_and_stopping_returns_everyone_to_the_champion()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t);
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 }));
        await SaveAsync(t, agent, "Answer questions about the company's policies, briefly.");
        var challenger = await SubmitAsync(t, agent);

        // Validation: one challenger, a share within limits, a version other than the one in force.
        Assert.Equal("validation.eval.comparison_challenger", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons",
            new { agentId = agent, challengerVersion = challenger, challengerModel = "openai/gpt-4o" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.comparison_share", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons",
            new { agentId = agent, challengerVersion = challenger, sharePercent = 80 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.comparison_version", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons",
            new { agentId = agent, challengerVersion = 1 }), HttpStatusCode.BadRequest));
        // A designer of another unit cannot propose one.
        await OkAsync(t["out"].Client.PostAsJsonAsync($"{Eval}/comparisons", new { agentId = agent, challengerVersion = challenger }), HttpStatusCode.NotFound);

        var proposed = await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons",
            new { agentId = agent, challengerVersion = challenger, sharePercent = 50, purpose = "Shorter answers" }), HttpStatusCode.Created);
        var id = proposed.GetProperty("id").GetGuid();
        Assert.Equal("proposed", proposed.GetProperty("status").GetString());
        Assert.Equal("running", proposed.GetProperty("gateStatus").GetString());
        Assert.Equal(1, await AuditAsync(t, AuditActions.EvalComparisonProposed));
        Assert.Equal("conflict.eval.comparison_active", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons",
            new { agentId = agent, challengerVersion = challenger }), HttpStatusCode.Conflict));
        // Until approved, everyone is on the version in force.
        Assert.Equal((1, null, null), await RunAsync(t, agent, "e1"));

        // Only the AI Policy Administrator decides, and the challenger must have passed its gate first.
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true }), HttpStatusCode.Forbidden);
        Assert.Equal("conflict.eval.comparison_gate", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true }),
            HttpStatusCode.Conflict));
        await world.Database.PassGateAsync(t.Id, agent, challenger);
        var started = await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true, note = "Go" }));
        Assert.Equal("running", started.GetProperty("status").GetString());
        Assert.Equal(1, await AuditAsync(t, AuditActions.EvalComparisonDecided));

        // Each person gets the arm the allocation gives them, the same every time; both arms are sampled with their arm.
        foreach (var person in People)
        {
            var expected = ComparisonRules.InChallenger(id, t[person].Id, 50);
            var first = await RunAsync(t, agent, person);
            Assert.Equal(expected ? challenger : 1, first.Version);
            Assert.Equal(expected ? "challenger" : "champion", first.Arm);
            Assert.Equal(first, await RunAsync(t, agent, person));
        }
        Assert.Equal(People.Length * 2, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from evaluation.production_sample where comparison_id = @id", ("id", id)));
        var view = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{agent}/comparisons"));
        Assert.True(view.GetProperty("available").GetBoolean());
        var listed = Assert.Single(view.GetProperty("comparisons").EnumerateArray());
        Assert.Equal(2, listed.GetProperty("arms").GetArrayLength());
        Assert.True(listed.GetProperty("canStop").GetBoolean());

        // Stopping returns everyone to the champion from the next run.
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/stop", new { }));
        Assert.Equal(1, await AuditAsync(t, AuditActions.EvalComparisonStopped));
        foreach (var person in People)
        {
            Assert.Equal((1, null, null), await RunAsync(t, agent, person));
        }
        Assert.Equal("conflict.eval.comparison_stopped", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/stop", new { }),
            HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_model_challenger_is_gated_on_its_model_and_the_proposer_cannot_approve_it()
    {
        var t = await FreshAsync();
        var agent = await AgentAsync(t);
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 }));

        // An AI Policy Administrator who also designs the agent proposes; a model challenger starts its own gate run on that model.
        await world.AssignAsync(t, "aip", BuiltInRoles.AgentDesigner, "org_subtree", t.Unit("Hr"));
        var proposed = await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Eval}/comparisons",
            new { agentId = agent, challengerModel = "openai/gpt-4o", sharePercent = 50 }), HttpStatusCode.Created);
        var id = proposed.GetProperty("id").GetGuid();
        Assert.Equal("model", proposed.GetProperty("kind").GetString());
        Assert.Equal("openai/gpt-4o", await world.Database.TenantScalarAsync<string>(t.Id,
            "select model from evaluation.run where trigger = 'comparison' and agent_id = @agent", ("agent", agent)));
        Assert.Equal("conflict.eval.comparison_own", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true }),
            HttpStatusCode.Conflict));
        Assert.False(proposed.GetProperty("canDecide").GetBoolean());
        Assert.Equal("conflict.eval.comparison_gate", await ProblemAsync(t["aip2"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true }),
            HttpStatusCode.Conflict));
        // The comparison's gate run is not the version's release gate.
        Assert.Equal("passed", (await OkAsync(t["sara"].Client.GetAsync($"{Eval}/gates?agentId={agent}&version=1"))).GetProperty("status").GetString());

        await world.Database.PassGateAsync(t.Id, agent, 1);
        await OkAsync(t["aip2"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true }));
        foreach (var person in People)
        {
            var challenger = ComparisonRules.InChallenger(id, t[person].Id, 50);
            var run = await RunAsync(t, agent, person);
            Assert.Equal(1, run.Version);
            Assert.Equal(challenger ? "openai/gpt-4o" : null, run.Requested);
        }

        // Publishing another version stops the comparison: everyone is on the new champion.
        await SaveAsync(t, agent, "Answer questions about the company's policies, in short sentences.");
        var next = await SubmitAsync(t, agent);
        await world.Database.PassGateAsync(t.Id, agent, next);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{agent}/publish", new { version = next }));
        Assert.Equal((next, null, null), await RunAsync(t, agent, "e1"));
        Assert.Equal("champion_changed", await world.Database.TenantScalarAsync<string>(t.Id, "select stop_reason from evaluation.comparison where id = @id", ("id", id)));
    }

    [Fact]
    public async Task Packages_without_experiments_and_other_tenants_cannot_compare()
    {
        var t = await FreshAsync("workplace_plus");
        var agent = await AgentAsync(t);
        Assert.False((await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{agent}/comparisons"))).GetProperty("available").GetBoolean());
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons", new { agentId = agent, challengerModel = "openai/gpt-4o" }), HttpStatusCode.Forbidden);

        var owner = await FreshAsync();
        var compared = await AgentAsync(owner);
        await OkAsync(owner["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 }));
        var id = (await OkAsync(owner["sara"].Client.PostAsJsonAsync($"{Eval}/comparisons", new { agentId = compared, challengerModel = "openai/gpt-4o" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var other = await FreshAsync();
        await OkAsync(other["aip"].Client.GetAsync($"{Eval}/comparisons/{id}"), HttpStatusCode.NotFound);
        await OkAsync(other["aip"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/decision", new { approve = true }), HttpStatusCode.NotFound);
        await OkAsync(other["aip"].Client.PostAsJsonAsync($"{Eval}/comparisons/{id}/stop", new { }), HttpStatusCode.NotFound);
        // Someone without evaluation access to the agent does not see it either.
        await OkAsync(owner["out"].Client.GetAsync($"{Eval}/comparisons/{id}"), HttpStatusCode.NotFound);
    }
}
