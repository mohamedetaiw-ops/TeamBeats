using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>An agent action as its approver sees it (spec 027 US4 scenario 1): the exact parameters, who asked, the agent and its reasoning summary.</summary>
public sealed record AgentApprovalView(
    Guid Id,
    Guid RunId,
    Guid AgentId,
    int AgentVersion,
    IReadOnlyDictionary<string, string> AgentNames,
    Guid RequestedBy,
    string? RequesterName,
    string Tool,
    JsonElement Parameters,
    string Hash,
    string? Summary,
    string Status,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt,
    Guid? TaskId);

/// <summary>
/// L3 governed execution (spec 027 US1, US4, FR-010). A call whose rule is "approval" waits for an approver: the run
/// stops at the call, and an approval runs on the spec 024 engine so it shows in the spec 016 task inbox, marked as
/// requested by an AI agent on behalf of the person. The approvers are holders of the tool's approver permission at the
/// agent's unit or the people the tenant policy names; the person who asked never decides (separation of duties). The
/// approval is bound to the hash of the exact parameters. After the decision a worker continues the run as the person
/// (<see cref="AgentRuns.ApprovalAsync"/>); nobody deciding within three working days cancels the action.
/// </summary>
public sealed class AgentApprovals(IDbSession session, ITenantContext context, IProcessEngine engine, ILocalizer localizer, IAuditWriter audit, IUserDirectory users,
    IClock clock)
    : ITaskSource
{
    public const string KeyPrefix = "agent.approval:";
    public const string Source = "agent_approval";
    public const int DueWorkingDays = 3;
    public const int MaxSummary = 500;
    public const int MaxComment = 2000;

    /// <summary>One route per tool, so its approvers decide; republishing the same route keeps its version.</summary>
    public static string ProcessKey(string tool) => KeyPrefix + tool;

    public static string Link(Guid approvalId) => $"/agents/approvals/{approvalId}";

    internal sealed record Row(Guid Id, Guid RunId, Guid AgentId, int AgentVersion, Dictionary<string, string> AgentNames, Guid RequestedBy, string Tool, string Parameters,
        string Hash, string? Summary, Guid? InstanceId, string Status, Guid? DecidedBy, DateTimeOffset DueAt, DateTimeOffset CreatedAt, Guid? OrgUnitId);

    private const string Select =
        """
        select a.id, a.run_id, a.agent_id, a.agent_version, a.agent_names::text, a.requested_by, a.tool, a.parameters::text, a.parameter_hash, a.summary,
          a.instance_id, a.status, a.decided_by, a.due_at, a.created_at, g.org_unit_id
        from agents.run_approval a join agents.agent g on g.id = a.agent_id
        """;

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4)) ?? [], r.GetGuid(5), r.GetString(6), r.GetString(7), r.GetString(8),
        r.Get<string?>("summary"), r.Get<Guid?>("instance_id"), r.GetString(11), r.Get<Guid?>("decided_by"), r.GetFieldValue<DateTimeOffset>(13),
        r.GetFieldValue<DateTimeOffset>(14), r.Get<Guid?>("org_unit_id"));

    internal async Task<Row?> FindAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        (await session.QueryAsync($"{Select} where a.id = @id{(forUpdate ? " for update of a" : "")}", new { id }, Read, ct)).FirstOrDefault();

    private async Task<Row?> ForTaskAsync(UserTaskInfo task, CancellationToken ct) =>
        Guid.TryParseExact(task.BusinessKey, "N", out var id) ? await FindAsync(id, false, ct) : null;

    /// <summary>
    /// Asks for the approval of a call: keeps it with its parameters and hash and starts the approval route, the person
    /// excluded as a candidate. Returns when the approval is due, the task's due time from the tenant calendar.
    /// </summary>
    internal async Task<DateTimeOffset> RequestAsync(Guid approvalId, Guid runId, RunAgent agent, Guid user, string tool, JsonElement parameters, string hash,
        string? summary, ToolApprover approver, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var text = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim() is { Length: > MaxSummary } cut ? cut[..MaxSummary] + "…" : summary.Trim();
        await session.ExecAsync(
            """
            insert into agents.run_approval (tenant_id, id, run_id, agent_id, agent_version, requested_by, tool, parameter_hash, parameters, agent_names, summary, due_at, created_at)
            values (platform.current_tenant_id(), @approvalId, @runId, @agent, @version, @user, @tool, @hash, @parameters::jsonb, @names::jsonb, @text, @due, @now)
            """, new
            {
                approvalId, runId, agent = agent.Id, version = agent.Version, user, tool, hash, parameters = parameters.GetRawText(),
                names = JsonSerializer.Serialize(agent.Definition.Name), text, due = now.AddDays(DueWorkingDays), now,
            }, ct);
        var key = ProcessKey(tool);
        await engine.PublishRouteAsync(key, RouteSources.AgentRoute, new RouteSpec("agent action approval",
            [new RouteStep("decision", "Agent action approval", new CandidateRule(Users: approver.Users, Permission: approver.Permission), DueWorkingDays)]), ct);
        var instance = await engine.StartAsync(key, approvalId.ToString("N"), agent.OrgUnitId, user, [user], user, ct: ct);
        var due = (await engine.InstanceTasksAsync(instance.Id, ct)).FirstOrDefault(t => t.Status == "open")?.DueAt ?? now.AddDays(DueWorkingDays);
        await session.ExecAsync("update agents.run_approval set instance_id = @instance, due_at = @due where id = @approvalId",
            new { instance = instance.Id, due, approvalId }, ct);
        return due;
    }

    /// <summary>
    /// Ends an approval that no longer applies (the person stopped the run, the agent was suspended or switched off): its
    /// task leaves the inbox at once. The route is withdrawn when the person who asked is the one ending it.
    /// </summary>
    internal async Task CancelAsync(Guid approvalId, CancellationToken ct)
    {
        var instance = await session.ScalarAsync<Guid?>(
            "update agents.run_approval set status = 'cancelled', decided_at = @now where id = @approvalId and status = 'pending' returning instance_id",
            new { approvalId, now = clock.UtcNow }, ct);
        if (instance is { } id && context.ActorType == ActorType.User && await engine.GetInstanceAsync(id, ct) is { Status: "active", StartedBy: var starter } && starter == context.ActorId)
        {
            await engine.WithdrawAsync(id, ct);
        }
    }

    /// <summary>Marks an approval carried out by the worker, once; false when another worker got there first.</summary>
    internal async Task<bool> MarkAppliedAsync(Guid approvalId, CancellationToken ct) =>
        await session.ExecAsync("update agents.run_approval set applied_at = @now where id = @approvalId and applied_at is null",
            new { approvalId, now = clock.UtcNow }, ct) == 1;

    /// <summary>Nobody decided in time (US4 scenario 3): the approval expires and its task leaves the inbox, run as the person who asked.</summary>
    internal async Task ExpireAsync(Row row, CancellationToken ct)
    {
        await session.ExecAsync("update agents.run_approval set status = 'expired', decided_at = @now where id = @id and status = 'pending'",
            new { id = row.Id, now = clock.UtcNow }, ct);
        if (row.InstanceId is { } id && await engine.GetInstanceAsync(id, ct) is { Status: "active" })
        {
            await engine.WithdrawAsync(id, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentActionExpired, "agent_run", row.RunId.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { agent = row.AgentId, version = row.AgentVersion, tool = row.Tool, hash = row.Hash }), ct);
    }

    /// <summary>The approval of a waiting call for its approver: someone who holds its open task, directly or as a delegate. 404 for anyone else.</summary>
    public async Task<AgentApprovalView> GetAsync(Guid id, CancellationToken ct)
    {
        context.RequireUser();
        var row = await FindAsync(id, false, ct) ?? throw AppException.NotFound();
        UserTaskInfo? mine = null;
        if (row.InstanceId is { } instance)
        {
            foreach (var task in await engine.InstanceTasksAsync(instance, ct))
            {
                if (await engine.MyTaskAsync(task.Id, ct) is { } held)
                {
                    mine = held;
                    break;
                }
            }
        }
        if (mine is null)
        {
            throw AppException.NotFound();
        }
        using var doc = JsonDocument.Parse(row.Parameters);
        var requester = await users.GetAsync(row.RequestedBy, ct);
        return new AgentApprovalView(row.Id, row.RunId, row.AgentId, row.AgentVersion, row.AgentNames, row.RequestedBy, requester?.DisplayName, row.Tool, doc.RootElement.Clone(), row.Hash,
            row.Summary, row.Status, row.DueAt, row.CreatedAt, mine.Status == "open" ? mine.Id : null);
    }

    // ---- The spec 016 task inbox ----

    public bool Handles(string definitionKey) => definitionKey.StartsWith(KeyPrefix, StringComparison.Ordinal);

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await ForTaskAsync(task, ct) is not { Status: "pending" } row ||
            !await session.ScalarAsync<bool>("select exists (select 1 from agents.run where id = @run and status = 'waiting')", new { run = row.RunId }, ct))
        {
            return null;
        }
        var title = Locales.LaunchComplete.ToDictionary(l => l, l => localizer.Get("agents.approval.task", l)
            .Replace("{agent}", row.AgentNames.GetValueOrDefault(l) ?? row.AgentNames.GetValueOrDefault("en") ?? row.AgentNames.Values.FirstOrDefault() ?? "", StringComparison.Ordinal));
        return new TaskSubject(Source, title, Link(row.Id) + "?task=" + task.Id, row.OrgUnitId, row.RequestedBy);
    }

    /// <summary>Separation of duties (US4 scenario 5): the person who asked never approves their own agent's action.</summary>
    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await ForTaskAsync(task, ct) is { Status: "pending" } row && authority != row.RequestedBy;

    /// <summary>The decision is kept with the approval; the worker carries it out as the person (approved) or tells the agent (rejected).</summary>
    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await ForTaskAsync(task, ct) is not { Status: "pending" } row || await engine.GetInstanceAsync(task.InstanceId, ct) is not { Status: "completed" } instance)
        {
            return;
        }
        var approved = instance.Outcome == InstanceOutcomes.Approved;
        var by = done.CompletedBy ?? context.RequireUser();
        var comment = string.IsNullOrWhiteSpace(done.Comment) ? null : done.Comment.Trim() is { Length: > MaxComment } c ? c[..MaxComment] : done.Comment.Trim();
        await session.ExecAsync(
            "update agents.run_approval set status = @status, decided_by = @by, decided_at = @now, comment = @comment where id = @id and status = 'pending'",
            new { status = approved ? "approved" : "rejected", by, now = clock.UtcNow, comment, id = row.Id }, ct);
        await audit.WriteAsync(new AuditEntry(approved ? AuditActions.AgentActionApproved : AuditActions.AgentActionRejected, "agent_run", row.RunId.ToString(),
            TargetOrgUnitId: row.OrgUnitId, Changes: new { agent = row.AgentId, version = row.AgentVersion, tool = row.Tool, hash = row.Hash, approver = by, onBehalfOf = done.OnBehalfOf }), ct);
    }

    /// <summary>Approvals the worker has to carry out: decided and not applied yet, or pending past their due time.</summary>
    internal Task<List<(Guid Approval, Guid User)>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        session.QueryAsync(
            """
            select a.id, a.requested_by from agents.run_approval a join agents.run r on r.id = a.run_id
            where r.status = 'waiting' and a.applied_at is null
              and (a.status in ('approved', 'rejected') or (a.status = 'pending' and a.due_at <= @now))
            order by a.created_at
            limit @limit
            for update of a skip locked
            """, new { now, limit }, r => (r.GetGuid(0), r.GetGuid(1)), ct);
}
