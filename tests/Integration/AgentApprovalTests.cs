using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 027 R2 T015 (US1 L3, US4, FR-010): an L3 agent's action whose rule is "approval" waits for an approver in the
/// task inbox, bound to the hash of its exact parameters, and runs as the person only after approval. The person cannot
/// approve it, a rejection or an expiry does nothing, suspension takes it out of the inbox, L3 needs its package, and
/// nothing crosses tenants. All content is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentApprovalTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";
    private const string Order = """[fake:tool:test__order] [fake:args:{"item":"Ergonomic chair","amount":450}]""";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("apv-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "fin", "aip", "sec" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fin", BuiltInRoles.FinanceOfficer, "tenant", null);
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

    private async Task AllowL3Async(TestTenant t)
    {
        var current = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy",
            new { version = current.GetProperty("version").GetInt32(), policy = AgentPolicy.Default with { MaxAutonomy = "L3" } }));
    }

    private async Task<Guid> L3AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search", "test.order" };
        definition["autonomy"] = "L3";
        definition["escalation"] = new Dictionary<string, string> { ["en"] = "Ask the finance team.", ["ar"] = "اسأل فريق المالية." };
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private Task<int> ApplyAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<AgentRunResumer>().ApplyApprovalsAsync(CancellationToken.None));

    private static async Task<JsonElement?> InboxItemAsync(TestTenant t, string user) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Cast<JsonElement?>()
            .FirstOrDefault(i => i!.Value.GetProperty("source").GetString() == AgentApprovals.Source);

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    [Fact]
    public async Task An_L3_action_waits_for_the_approver_in_the_inbox_and_runs_as_the_person_only_after_approval()
    {
        var t = await FreshAsync();
        await AllowL3Async(t);
        var agent = await L3AgentAsync(t);

        var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = $"Order me a chair. {Order}" }));
        Assert.Equal("waiting", run.GetProperty("status").GetString());
        var card = run.GetProperty("confirmation");
        Assert.True(card.GetProperty("awaitingApproval").GetBoolean());
        var runId = run.GetProperty("id").GetGuid();
        Assert.DoesNotContain(world.Factory.AgentTools.For(t["emp"].Id), c => c.Tool == "test.order");

        // The person cannot confirm it themselves, nor see it in their inbox.
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/runs/{runId}/confirmations/{card.GetProperty("id").GetGuid()}",
            new { confirm = true, hash = card.GetProperty("hash").GetString() }), HttpStatusCode.NotFound);
        Assert.Null(await InboxItemAsync(t, "emp"));

        // The finance officer sees it marked as asked by an AI agent for the person, with the exact parameters.
        var item = (await InboxItemAsync(t, "fin"))!.Value;
        Assert.Contains("AI agent", item.GetProperty("title").GetProperty("en").GetString());
        Assert.Equal(t["emp"].Id, item.GetProperty("requester").GetProperty("id").GetGuid());
        var approvalId = Guid.Parse(item.GetProperty("link").GetString()!.Split('/')[^1].Split('?')[0]);
        var detail = await OkAsync(t["fin"].Client.GetAsync($"{Agents}/approvals/{approvalId}"));
        Assert.Equal("test.order", detail.GetProperty("tool").GetString());
        Assert.Equal(450, detail.GetProperty("parameters").GetProperty("amount").GetInt32());
        Assert.Equal(card.GetProperty("hash").GetString(), detail.GetProperty("hash").GetString());
        await OkAsync(t["emp"].Client.GetAsync($"{Agents}/approvals/{approvalId}"), HttpStatusCode.NotFound);

        // Nothing runs before the decision.
        Assert.Equal(0, await ApplyAsync(t));
        await OkAsync(t["fin"].Client.PostAsJsonAsync($"/api/v1/tasks/{item.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        Assert.Equal(1, await ApplyAsync(t));
        var calls = world.Factory.AgentTools.For(t["emp"].Id).Where(c => c.Tool == "test.order").ToList();
        var call = Assert.Single(calls);
        Assert.Contains("Ergonomic chair", call.Arguments);
        var result = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{runId}/result"));
        Assert.Equal("completed", result.GetProperty("status").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentActionApproved));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from agents.run_step where run_id = @id and tool = 'test.order' and decision = 'approved'", ("id", runId)));

        // A decision is carried out once.
        Assert.Equal(0, await ApplyAsync(t));
        Assert.Single(world.Factory.AgentTools.For(t["emp"].Id), c => c.Tool == "test.order");

        // Another tenant's approver never sees it and its worker finds nothing.
        var other = await FreshAsync();
        Assert.Null(await InboxItemAsync(other, "fin"));
        await OkAsync(other["fin"].Client.GetAsync($"{Agents}/approvals/{approvalId}"), HttpStatusCode.NotFound);
        Assert.Equal(0, await ApplyAsync(other));
    }

    [Fact]
    public async Task A_rejection_or_an_expiry_does_nothing_and_the_person_who_asked_never_approves()
    {
        var t = await FreshAsync();
        await AllowL3Async(t);
        var agent = await L3AgentAsync(t);

        // Rejected: the agent is told and answers; the order never runs.
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = $"Order me a chair. {Order}" }));
        var item = (await InboxItemAsync(t, "fin"))!.Value;
        await OkAsync(t["fin"].Client.PostAsJsonAsync($"/api/v1/tasks/{item.GetProperty("taskId").GetGuid()}/decision", new { outcome = "reject", comment = "Over budget" }));
        Assert.Equal(1, await ApplyAsync(t));
        Assert.DoesNotContain(world.Factory.AgentTools.For(t["emp"].Id), c => c.Tool == "test.order");
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentActionRejected));

        // Nobody decides by the due time: the action is cancelled, the task leaves the inbox and the run ends as expired.
        var late = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = $"Order me another chair. {Order}" }));
        var lateId = late.GetProperty("id").GetGuid();
        await world.Database.TenantScalarAsync<int>(t.Id, "update agents.run_approval set due_at = now() - interval '1 minute' where run_id = @id returning 1", ("id", lateId));
        Assert.Equal(1, await ApplyAsync(t));
        var expired = await OkAsync(t["emp"].Client.GetAsync($"{Agents}/runs/{lateId}/result"));
        Assert.Equal("expired", expired.GetProperty("outcome").GetString());
        Assert.Null(await InboxItemAsync(t, "fin"));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentActionExpired));
        Assert.DoesNotContain(world.Factory.AgentTools.For(t["emp"].Id), c => c.Tool == "test.order");

        // Separation of duties: a person who holds the approver permission still never decides their own agent's action.
        await world.AssignAsync(t, "emp", BuiltInRoles.FinanceOfficer, "tenant", null);
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = $"Order me a desk. {Order}" }));
        Assert.Null(await InboxItemAsync(t, "emp"));
        Assert.NotNull(await InboxItemAsync(t, "fin"));
    }

    [Fact]
    public async Task Suspension_cancels_a_waiting_approval_and_L3_needs_its_package_and_a_named_approver()
    {
        var t = await FreshAsync();
        await AllowL3Async(t);
        var agent = await L3AgentAsync(t);
        var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = $"Order me a chair. {Order}" }));
        Assert.NotNull(await InboxItemAsync(t, "fin"));

        // Suspending the agent takes the approval out of the inbox; nothing runs even if it was already decided.
        await OkAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{agent}/suspend", new { reason = "Checking an order problem" }));
        Assert.Null(await InboxItemAsync(t, "fin"));
        Assert.Equal("cancelled", await world.Database.TenantScalarAsync<string>(t.Id, "select status from agents.run_approval where run_id = @id",
            ("id", run.GetProperty("id").GetGuid())));
        Assert.Equal(0, await ApplyAsync(t));
        Assert.DoesNotContain(world.Factory.AgentTools.For(t["emp"].Id), c => c.Tool == "test.order");

        // A tenant policy that names no approver for a tool without one refuses the call; one that names people routes to them.
        var policy = AgentPolicy.Default with
        {
            MaxAutonomy = "L3",
            Approvers = new() { ["test.order"] = new ToolApprover(null, [t["aip"].Id]) },
        };
        var current = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = current.GetProperty("version").GetInt32(), policy }));
        await OkAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{agent}/resume", new { }));
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = $"Order me a lamp. {Order}" }));
        Assert.NotNull(await InboxItemAsync(t, "aip"));
        Assert.Null(await InboxItemAsync(t, "fin"));
        var bad = policy with { Approvers = new() { ["test.order"] = new ToolApprover("no.such_permission", null) } };
        current = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = current.GetProperty("version").GetInt32(), policy = bad }), HttpStatusCode.BadRequest);

        // Without write tools in the package, L3 cannot be allowed.
        var basic = await FreshAsync("workplace");
        var refused = await basic["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = AgentPolicy.Default with { MaxAutonomy = "L3" } });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync());
    }
}
