using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

/// <summary>A user task of an external engine as its assignee sees it. The outcome and comment are shown while they wait to be sent.</summary>
public sealed record ExternalTaskView(Guid Id, string Engine, string Name, string? ProcessName, DateTimeOffset CreatedAt, DateTimeOffset? DueAt, string Status,
    string? Outcome, string? Comment, string? LastError, int Attempts, DateTimeOffset? NextAttemptAt, DateTimeOffset? CompletedAt);

public sealed record CompleteExternalTaskRequest(string? Outcome, string? Comment);

/// <summary>
/// User tasks of an external engine in the TeamBeats inbox (spec 024 US8, FR-016), Camunda 8 first. A sync reads the
/// open, assigned tasks of every enabled Camunda 8 connection through the connector runtime and keeps a reference per
/// task, matched to a TeamBeats person by the assignee email. The assignee completes it from its TeamBeats page: the
/// outcome and comment go to Camunda as variables, a technical failure is shown on the task and retried with back-off,
/// and once Camunda has them they are cleared. Process state stays in Camunda (US8 scenario 3).
/// </summary>
public sealed class ExternalTasks(IDbSession session, ITenantContext context, IEntitlementService entitlements, IConnectorActions actions, IConnectorRuntime runtime,
    IUserDirectory users, IAuditWriter audit, IClock clock)
{
    public const string Engine = "camunda8";
    public const string Source = "camunda_task";
    public const string SearchAction = "search_user_tasks";
    public const string CompleteAction = "complete_user_task";
    public const int MaxPages = 20;
    public const int MaxAttempts = 6;

    /// <summary>The outcomes a person can send (spec 024 section 12 default for US8): Camunda receives them as the variable <c>outcome</c>.</summary>
    public static readonly IReadOnlyList<string> Outcomes = ["completed", "approved", "rejected"];

    public static string Link(Guid id) => "/tasks/external/" + id;

    /// <summary>Reads the open, assigned tasks of each enabled Camunda 8 connection and closes the ones Camunda no longer lists.</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowBpmnAdvanced, ct))
        {
            return 0;
        }
        var now = clock.UtcNow;
        var found = 0;
        var people = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in (await actions.ListAsync(ct)).Where(a => a.Kind == Engine && a.Action == SearchAction && a.Enabled))
        {
            string? after = null;
            var complete = false;
            for (var page = 0; page < MaxPages; page++)
            {
                var input = JsonSerializer.SerializeToElement(after is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["after"] = after });
                var result = await runtime.InvokeAsync(new ConnectorCall(connection.ConnectionId, SearchAction, input,
                    $"camunda8-sync:{connection.ConnectionId:N}:{now.UtcTicks}:{page}", "external-task-sync"), ct);
                if (result.Outcome != ConnectorOutcomes.Succeeded || result.Output is not { ValueKind: JsonValueKind.Object } output
                    || !output.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                {
                    break;
                }
                foreach (var task in tasks.EnumerateArray())
                {
                    if (Text(task, "key") is not { } key || Text(task, "assignee") is not { } assignee || await PersonAsync(assignee, people, ct) is not { } userId)
                    {
                        continue;
                    }
                    await session.ExecAsync(
                        """
                        insert into workflow.external_task (tenant_id, id, connection_id, external_key, user_id, name, process_name, created_at, due_at, status, seen_at)
                        values (platform.current_tenant_id(), @id, @connection, @key, @userId, @name, @process, @createdAt, @dueAt, 'open', @now)
                        on conflict (tenant_id, connection_id, external_key) do update set
                          user_id = excluded.user_id, name = excluded.name, process_name = excluded.process_name, due_at = excluded.due_at, seen_at = excluded.seen_at,
                          status = case when workflow.external_task.status = 'closed' then 'open' else workflow.external_task.status end
                        """,
                        new
                        {
                            id = Guid.NewGuid(), connection = connection.ConnectionId, key, userId, name = Text(task, "name") ?? key, process = Text(task, "process"),
                            createdAt = Time(task, "createdAt") ?? now, dueAt = Time(task, "dueAt"), now,
                        }, ct);
                    found++;
                }
                if (Text(output, "next") is not { } next)
                {
                    complete = true;
                    break;
                }
                after = next;
            }
            if (complete)
            {
                // Completed or cancelled in Camunda, or no longer assigned to a TeamBeats person: it leaves the inbox.
                await session.ExecAsync(
                    "update workflow.external_task set status = 'closed' where connection_id = @connection and status = 'open' and seen_at < @now",
                    new { connection = connection.ConnectionId, now }, ct);
            }
        }
        return found;
    }

    /// <summary>Sends again the completions that failed for a technical reason and are due for another attempt.</summary>
    public async Task<int> RetryAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowBpmnAdvanced, ct))
        {
            return 0;
        }
        var due = await session.QueryAsync(
            "select id from workflow.external_task where status = 'failed' and next_attempt_at <= @now order by next_attempt_at limit 50 for update skip locked",
            new { now = clock.UtcNow }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            await SendAsync(id, ct);
        }
        return due.Count;
    }

    public async Task<ExternalTaskView> GetAsync(Guid id, CancellationToken ct)
    {
        var me = context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmnAdvanced, ct);
        return await ViewAsync(id, me, false, ct) ?? throw AppException.NotFound();
    }

    /// <summary>The assignee completes the task (US8 scenario 2). Only an open task, or one whose completion failed, can be completed.</summary>
    public async Task<ExternalTaskView> CompleteAsync(Guid id, CompleteExternalTaskRequest request, CancellationToken ct)
    {
        var me = context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmnAdvanced, ct);
        var outcome = request.Outcome?.Trim();
        if (outcome is null || !Outcomes.Contains(outcome))
        {
            throw AppException.Validation("outcome", "validation.process.external_task_outcome");
        }
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is { Length: > 2000 })
        {
            throw AppException.Validation("comment", "validation.process.external_task_comment");
        }
        var current = await ViewAsync(id, me, true, ct) ?? throw AppException.NotFound();
        if (current.Status is not ("open" or "failed"))
        {
            throw AppException.Conflict("conflict.process.external_task_not_open");
        }
        await session.ExecAsync(
            "update workflow.external_task set status = 'completing', outcome = @outcome, comment = @comment, next_attempt_at = null where id = @id",
            new { id, outcome, comment }, ct);
        await SendAsync(id, ct);
        return (await ViewAsync(id, me, false, ct))!;
    }

    /// <summary>The person's tasks that are still theirs to finish, for the inbox.</summary>
    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct)
    {
        if (context.ActorId != userId || !await entitlements.IsEnabledAsync(Capabilities.WorkflowBpmnAdvanced, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select id, name, created_at, due_at from workflow.external_task
            where user_id = @userId and status in ('open', 'completing', 'failed') order by created_at limit 200
            """,
            new { userId }, r =>
            {
                var name = r.GetString(1);
                return new PersonalTask(Source, r.GetGuid(0), new Dictionary<string, string> { ["en"] = name, ["ar"] = name }, Link(r.GetGuid(0)),
                    r.GetFieldValue<DateTimeOffset>(2), r.Get<DateTimeOffset?>("due_at"));
            }, ct);
    }

    private async Task SendAsync(Guid id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            update workflow.external_task set attempts = attempts + 1 where id = @id and status in ('completing', 'failed')
            returning connection_id, external_key, user_id, outcome, comment, attempts
            """,
            new { id }, r => (Found: true, Connection: r.GetGuid(0), Key: r.GetString(1), User: r.GetGuid(2), Outcome: r.Get<string?>("outcome"),
                Comment: r.Get<string?>("comment"), Attempts: r.GetInt32(5)), ct);
        if (!row.Found)
        {
            return;
        }
        var input = JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["key"] = row.Key, ["outcome"] = row.Outcome, ["comment"] = row.Comment }
            .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
        var result = await runtime.InvokeAsync(new ConnectorCall(row.Connection, CompleteAction, input, $"camunda8-complete:{id:N}:{row.Attempts}", "external-task-" + id), ct);
        var now = clock.UtcNow;
        if (result.Outcome == ConnectorOutcomes.Succeeded)
        {
            await session.ExecAsync(
                """
                update workflow.external_task set status = 'completed', outcome = null, comment = null, last_error = null, next_attempt_at = null, completed_at = @now
                where id = @id
                """, new { id, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ProcessExternalTaskCompleted, "external_task", id.ToString(), Changes: new
            {
                engine = Engine, connectionId = row.Connection, externalKey = row.Key, completedBy = row.User, outcome = row.Outcome, attempts = row.Attempts,
            }), ct);
            return;
        }
        if (result.Outcome == ConnectorOutcomes.BusinessError)
        {
            // Camunda no longer has the task open (completed, cancelled or reassigned there): nothing to retry.
            await session.ExecAsync(
                "update workflow.external_task set status = 'closed', outcome = null, comment = null, last_error = @error, next_attempt_at = null where id = @id",
                new { id, error = result.ErrorCode ?? result.Outcome }, ct);
            return;
        }
        var retryAt = row.Attempts < MaxAttempts ? now + TimeSpan.FromMinutes(5 * Math.Pow(2, row.Attempts - 1)) : (DateTimeOffset?)null;
        await session.ExecAsync(
            "update workflow.external_task set status = 'failed', last_error = @error, next_attempt_at = @retryAt where id = @id",
            new { id, error = result.ErrorClass ?? result.Outcome, retryAt }, ct);
    }

    private async Task<ExternalTaskView?> ViewAsync(Guid id, Guid userId, bool locked, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            $"""
            select id, name, process_name, created_at, due_at, status, outcome, comment, last_error, attempts, next_attempt_at, completed_at
            from workflow.external_task where id = @id and user_id = @userId{(locked ? " for update" : "")}
            """,
            new { id, userId }, r => new ExternalTaskView(r.GetGuid(0), Engine, r.GetString(1), r.Get<string?>("process_name"), r.GetFieldValue<DateTimeOffset>(3),
                r.Get<DateTimeOffset?>("due_at"), r.GetString(5), r.Get<string?>("outcome"), r.Get<string?>("comment"), r.Get<string?>("last_error"), r.GetInt32(9),
                r.Get<DateTimeOffset?>("next_attempt_at"), r.Get<DateTimeOffset?>("completed_at")), ct);
        return row;
    }

    /// <summary>The TeamBeats person a Camunda assignee names: the active user with that email. Other assignees are left out.</summary>
    private async Task<Guid?> PersonAsync(string assignee, Dictionary<string, Guid?> cache, CancellationToken ct)
    {
        if (!cache.TryGetValue(assignee, out var found))
        {
            found = assignee.Contains('@') && await users.FindByEmailAsync(assignee, ct) is { Status: "active", IsTest: false } user ? user.Id : null;
            cache[assignee] = found;
        }
        return found;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;
}

/// <summary>Puts external engine tasks in the spec 016 inbox.</summary>
public sealed class ExternalTaskInbox(ExternalTasks tasks) : IPersonalTaskSource
{
    public Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default) => tasks.OpenForAsync(userId, ct);
}

/// <summary>
/// Every minute, for every active tenant in its own scope and transaction: reads Camunda 8 user tasks and sends the
/// completions due for another attempt. Runs as the system actor. Hosted by the worker, and by the development host
/// when <c>Workflow:ExternalTasks:InProcess</c> is true.
/// </summary>
public sealed class ExternalTaskWorker(IServiceScopeFactory scopes, ILogger<ExternalTaskWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Workflow:ExternalTasks:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "External task sync could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                var tasks = scope.ServiceProvider.GetRequiredService<ExternalTasks>();
                await tasks.SyncAsync(ct);
                await tasks.RetryAsync(ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "External task sync failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
