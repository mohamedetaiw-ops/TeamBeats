using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// The agent lifecycle (spec 020 T008 to T011; spec 027 T010, T011): submit for review with an inbox task for publishers,
/// review against the published version, publish with separation of duties, reject with a reason, roll back with a
/// policy re-check, suspend and resume, retire, the tenant kill switch, suspension of agents that break a new policy,
/// and nothing across tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentLifecycleTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("al-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "aip", "sec", "fadi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", t.Unit("Finance"));
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "sec", BuiltInRoles.SecurityOfficer, "tenant", null);
        await world.AssignAsync(t, "emp", BuiltInRoles.ContentEditor, "tenant", null);
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

    private Task<string?> RunStatusAsync(TestTenant t, Guid run) =>
        world.Database.TenantScalarAsync<string>(t.Id, "select status from agents.run where id = @id", ("id", run));

    private static async Task<Guid> InstallAsync(TestTenant t, string template) =>
        (await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = template, orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created))
        .GetProperty("agent").GetProperty("id").GetGuid();

    /// <summary>Saves the latest draft (or a new one after a published or rejected version) as Sara with these tools and autonomy.</summary>
    private static async Task<JsonElement> SaveAsync(TestTenant t, Guid id, string[] tools, string autonomy)
    {
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = autonomy;
        var revision = detail.GetProperty("latest").GetProperty("revision").GetInt32();
        return await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision, definition }));
    }

    private static async Task<int> SubmitAsync(TestTenant t, Guid id, string by = "sara")
    {
        var detail = await OkAsync(t[by].Client.GetAsync($"{Agents}/{id}"));
        var latest = detail.GetProperty("latest");
        var submitted = await OkAsync(t[by].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = latest.GetProperty("revision").GetInt32() }));
        Assert.Equal(AgentVersionStates.InReview, submitted.GetProperty("latest").GetProperty("state").GetString());
        return latest.GetProperty("number").GetInt32();
    }

    private static async Task<JsonElement> PublishAsync(TestTenant t, Guid id, int version, string by = "aip") =>
        await OkAsync(t[by].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version }));

    private static async Task<Guid> PublishedAgentAsync(TestTenant t, string template, string[] tools, string autonomy)
    {
        var id = await InstallAsync(t, template);
        await SaveAsync(t, id, tools, autonomy);
        await PublishAsync(t, id, await SubmitAsync(t, id));
        return id;
    }

    private static async Task<JsonElement> InboxAsync(TestTenant t, string user) => await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"));

    private static bool HasReviewTask(JsonElement inbox, Guid agent) =>
        inbox.EnumerateArray().Any(i => i.GetProperty("source").GetString() == AgentReviewTasks.TaskSource && i.GetProperty("taskId").GetGuid() == agent);

    [Fact]
    public async Task A_submitted_version_waits_in_the_publishers_inbox_and_the_last_editor_cannot_approve_a_medium_risk_agent()
    {
        var t = await FreshAsync();
        var id = await InstallAsync(t, "hr_assistant");
        await SaveAsync(t, id, ["knowledge.search", "test.note"], "L2");
        Assert.Equal("conflict.agent.revision", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 99 }), HttpStatusCode.Conflict));
        var number = await SubmitAsync(t, id);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentVersionSubmitted));

        // The version is locked while in review, and a second submit is refused.
        Assert.Equal("conflict.agent.not_draft", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 1 }), HttpStatusCode.Conflict));
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        Assert.Equal("conflict.agent.in_review", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft",
            new { revision = 1, definition = JsonSerializer.Deserialize<object>(detail.GetProperty("definition").GetRawText()) }), HttpStatusCode.Conflict));

        // The publisher sees an inbox task linking to the review page; the designer and the employee do not.
        var inbox = await InboxAsync(t, "aip");
        Assert.True(HasReviewTask(inbox, id));
        var task = inbox.EnumerateArray().Single(i => i.GetProperty("taskId").GetGuid() == id);
        Assert.Equal(AgentReviewTasks.ReviewLink(id), task.GetProperty("link").GetString());
        Assert.False(HasReviewTask(await InboxAsync(t, "emp"), id));

        // The review shows what changed, the risk class and the gate result, which is not built yet and does not block.
        var review = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/{id}/review"));
        Assert.Equal(number, review.GetProperty("version").GetProperty("number").GetInt32());
        Assert.Equal(AgentRiskClasses.Medium, review.GetProperty("version").GetProperty("riskClass").GetString());
        Assert.Contains(review.GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "tools");
        Assert.Equal("not_built", review.GetProperty("gate").GetProperty("status").GetString());
        Assert.True(review.GetProperty("canApprove").GetBoolean());
        Assert.Equal("agent.publish_permission", (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}/review"))).GetProperty("cannotApproveReason").GetString());

        // A designer cannot publish; the last editor who also publishes cannot approve their own medium-risk version.
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Forbidden));
        await world.AssignAsync(t, "sara", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        Assert.Equal("conflict.agent.last_editor", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Conflict));
        Assert.False(HasReviewTask(await InboxAsync(t, "sara"), id));
        Assert.Equal("agent.last_editor", (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}/review"))).GetProperty("cannotApproveReason").GetString());

        var published = await PublishAsync(t, id, number);
        Assert.Equal(AgentStatuses.Published, published.GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal(number, published.GetProperty("agent").GetProperty("currentVersion").GetInt32());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentVersionApproved));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentVersionPublished));
        Assert.False(HasReviewTask(await InboxAsync(t, "aip"), id));
        Assert.Equal("conflict.agent.not_in_review", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.Conflict));

        // The audience can use it at once.
        var run = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hello" }));
        Assert.Equal(RunStatuses.Completed, run.GetProperty("status").GetString());

        // A low-risk agent may be published by its own last editor.
        var low = await InstallAsync(t, "enterprise_knowledge");
        await SaveAsync(t, low, ["knowledge.search"], "L0");
        await PublishAsync(t, low, await SubmitAsync(t, low), by: "sara");
    }

    [Fact]
    public async Task A_rejected_version_needs_a_reason_keeps_the_published_version_and_the_designer_edits_a_new_draft()
    {
        var t = await FreshAsync();
        var id = await PublishedAgentAsync(t, "enterprise_knowledge", ["knowledge.search"], "L0");
        await SaveAsync(t, id, ["knowledge.search", "requests.status"], "L0");
        var number = await SubmitAsync(t, id);
        Assert.Equal(2, number);

        Assert.Equal("validation.agent.reason", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/reject", new { version = number, reason = " " }), HttpStatusCode.BadRequest));
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/reject", new { version = number, reason = "No" }), HttpStatusCode.Forbidden));
        var rejected = await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/reject", new { version = number, reason = "Requests are not in scope for this agent." }));
        Assert.Equal(AgentVersionStates.Rejected, rejected.GetProperty("latest").GetProperty("state").GetString());
        Assert.Equal(1, rejected.GetProperty("agent").GetProperty("currentVersion").GetInt32());
        Assert.Equal(AgentStatuses.Published, rejected.GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentVersionRejected));
        Assert.Equal("Requests are not in scope for this agent.", await world.Database.TenantScalarAsync<string>(t.Id,
            "select rejection_reason from agents.agent_version where agent_id = @id and number = 2", ("id", id)));

        var next = await SaveAsync(t, id, ["knowledge.search"], "L0");
        Assert.Equal(3, next.GetProperty("latest").GetProperty("number").GetInt32());
        Assert.Equal(AgentVersionStates.Draft, next.GetProperty("latest").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Rollback_is_checked_against_todays_policy_and_a_stricter_policy_suspends_agents_that_break_it()
    {
        var t = await FreshAsync();
        var id = await PublishedAgentAsync(t, "hr_assistant", ["knowledge.search", "test.note"], "L2");
        await SaveAsync(t, id, ["knowledge.search"], "L0");
        await PublishAsync(t, id, await SubmitAsync(t, id));

        Assert.Equal("validation.agent.rollback_version", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/rollback", new { version = 2 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.agent.rollback_version", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/rollback", new { version = 7 }), HttpStatusCode.BadRequest));
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/rollback", new { version = 1 }), HttpStatusCode.Forbidden));
        var back = await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/rollback", new { version = 1 }));
        Assert.Equal(1, back.GetProperty("agent").GetProperty("currentVersion").GetInt32());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentRolledBack));

        // A policy that caps autonomy at L1 suspends the agent, whose version in use is L2.
        var other = await PublishedAgentAsync(t, "enterprise_knowledge", ["knowledge.search"], "L0");
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = 0, policy = AgentPolicy.Default with { MaxAutonomy = "L1" } }));
        var suspended = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/{id}"));
        Assert.Equal(AgentStatuses.Suspended, suspended.GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal("policy_changed", await world.Database.TenantScalarAsync<string>(t.Id, "select suspended_reason from agents.agent where id = @id", ("id", id)));
        Assert.Equal(AgentStatuses.Published, (await OkAsync(t["aip"].Client.GetAsync($"{Agents}/{other}"))).GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal("conflict.agent.suspended", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hi" }), HttpStatusCode.Conflict));

        // The owner is told (027 FR-018), with a link to the agent; the agent that still fits sends nothing.
        var links = (await OkAsync(t["sara"].Client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()).ToArray();
        Assert.Contains($"/admin/agents/{id}", links);
        Assert.DoesNotContain($"/admin/agents/{other}", links);
        Assert.Empty((await OkAsync(t["emp"].Client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray());

        // It cannot come back on that version, and rolling back to version 1 is now blocked by the policy.
        Assert.Equal("validation.agent.autonomy_above_policy", await ProblemAsync(t["sec"].Client.PostAsync($"{Agents}/{id}/resume", null), HttpStatusCode.BadRequest));
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/rollback", new { version = 2 }));
        Assert.Equal("validation.agent.rollback_policy", await ProblemAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/rollback", new { version = 1 }), HttpStatusCode.BadRequest));
        var resumed = await OkAsync(t["sec"].Client.PostAsync($"{Agents}/{id}/resume", null));
        Assert.Equal(AgentStatuses.Published, resumed.GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentResumed));
        Assert.Equal("conflict.agent.not_suspended", await ProblemAsync(t["sec"].Client.PostAsync($"{Agents}/{id}/resume", null), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Suspending_stops_waiting_runs_and_retiring_ends_the_agent_for_good()
    {
        var t = await FreshAsync();
        var id = await PublishedAgentAsync(t, "hr_assistant", ["test.note"], "L2");
        var waiting = await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Note " + Call("test.note", """{"text":"Later"}""") }));
        Assert.Equal(RunStatuses.Waiting, waiting.GetProperty("status").GetString());
        var run = waiting.GetProperty("id").GetGuid();

        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/suspend", new { reason = "Odd answers" }), HttpStatusCode.Forbidden));
        Assert.Equal("validation.agent.reason", await ProblemAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{id}/suspend", new { reason = "" }), HttpStatusCode.BadRequest));
        var suspended = await OkAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{id}/suspend", new { reason = "Odd answers" }));
        Assert.Equal(AgentStatuses.Suspended, suspended.GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal(RunStatuses.Stopped, await RunStatusAsync(t, run));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from agents.run_checkpoint where run_id = @id", ("id", run)));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentSuspended));
        Assert.Empty(world.Factory.AgentTools.For(t["emp"].Id));
        Assert.Equal("conflict.agent.unavailable", await ProblemAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{id}/suspend", new { reason = "Again" }), HttpStatusCode.Conflict));

        await OkAsync(t["sec"].Client.PostAsync($"{Agents}/{id}/resume", null));
        Assert.Equal(RunStatuses.Waiting, (await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs",
            new { message = "Note " + Call("test.note", """{"text":"Soon"}""") }))).GetProperty("status").GetString());

        Assert.Equal("authz.denied", await ProblemAsync(t["sec"].Client.PostAsJsonAsync($"{Agents}/{id}/retire", new { reason = "Replaced" }), HttpStatusCode.Forbidden));
        var retired = await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/retire", new { reason = "Replaced" }));
        Assert.Equal(AgentStatuses.Retired, retired.GetProperty("agent").GetProperty("status").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentRetired));
        Assert.Equal("conflict.agent.unavailable", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hi" }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.agent.not_suspended", await ProblemAsync(t["sec"].Client.PostAsync($"{Agents}/{id}/resume", null), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task The_kill_switch_stops_every_agent_in_the_tenant_only_with_the_tenant_name_typed()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var id = await PublishedAgentAsync(t, "hr_assistant", ["test.note"], "L2");
        var otherAgent = await PublishedAgentAsync(other, "enterprise_knowledge", ["knowledge.search"], "L0");
        var waiting = (await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Note " + Call("test.note", """{"text":"Later"}""") })))
            .GetProperty("id").GetGuid();

        Assert.False((await OkAsync(t["sec"].Client.GetAsync($"{Agents}/kill-switch"))).GetProperty("active").GetBoolean());
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.GetAsync($"{Agents}/kill-switch"), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied", await ProblemAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/kill-switch", new { active = true, confirmation = $"{t.Key} holding", reason = "Leak" }),
            HttpStatusCode.Forbidden));
        Assert.Equal("validation.agent.kill_switch_confirmation", await ProblemAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch",
            new { active = true, confirmation = "wrong", reason = "Leak" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.agent.reason", await ProblemAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch",
            new { active = true, confirmation = $"{t.Key} holding" }), HttpStatusCode.BadRequest));

        var on = await OkAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch", new { active = true, confirmation = $"{t.Key} holding", reason = "Possible data leak" }));
        Assert.True(on.GetProperty("active").GetBoolean());
        Assert.Equal(RunStatuses.Stopped, await RunStatusAsync(t, waiting));
        Assert.Equal("conflict.agent.kill_switch", await ProblemAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hi" }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.agent.kill_switch", await ProblemAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/test", new { message = "Hi" }), HttpStatusCode.Conflict));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AgentKillSwitch));

        // The other tenant keeps working, and the rest of the intranet keeps working.
        Assert.False((await OkAsync(other["sec"].Client.GetAsync($"{Agents}/kill-switch"))).GetProperty("active").GetBoolean());
        Assert.Equal(RunStatuses.Completed, (await OkAsync(other["emp"].Client.PostAsJsonAsync($"{Agents}/{otherAgent}/runs", new { message = "Hi" })))
            .GetProperty("status").GetString());
        await OkAsync(t["emp"].Client.GetAsync("/api/v1/tasks"));

        await OkAsync(t["sec"].Client.PutAsJsonAsync($"{Agents}/kill-switch", new { active = false }));
        Assert.Equal(RunStatuses.Completed, (await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hi" }))).GetProperty("status").GetString());
        Assert.Equal(2, await AuditCountAsync(t, AuditActions.AgentKillSwitch));
    }

    [Fact]
    public async Task Lifecycle_actions_never_reach_another_tenants_agent_or_one_outside_the_publishers_scope()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var id = await InstallAsync(t, "enterprise_knowledge");
        await SaveAsync(t, id, ["knowledge.search"], "L0");
        var number = await SubmitAsync(t, id);

        Assert.Equal("not_found", await ProblemAsync(other["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(other["aip"].Client.GetAsync($"{Agents}/{id}/review"), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(other["sec"].Client.PostAsJsonAsync($"{Agents}/{id}/suspend", new { reason = "x" }), HttpStatusCode.NotFound));
        Assert.False(HasReviewTask(await InboxAsync(other, "aip"), id));

        // A designer of another unit cannot see or submit it.
        Assert.Equal("not_found", await ProblemAsync(t["fadi"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 1 }), HttpStatusCode.NotFound));
        Assert.Equal("not_found", await ProblemAsync(t["fadi"].Client.GetAsync($"{Agents}/{id}/review"), HttpStatusCode.NotFound));
    }
}
