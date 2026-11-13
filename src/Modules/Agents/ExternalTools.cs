using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// The tool gateway for external AI clients (spec 027 FR-025, spec 035 FR-020 to FR-022), in order: the tenant kill
/// switch, external AI access, the client on the tenant's list, the registry entry marked exposable, the tenant agent
/// policy and its rule (never "block"), the external tools list, entitlement, the scopes the token holds, call limits
/// and the parameter schema. Then the tool runs as the employee, through the owning module's use cases, and nothing
/// above the client's classification cap goes back (Internal by default, never Restricted). Every call leaves an
/// execution record. A write tool is offered only when the tenant turned write tools on and the package has them, and
/// it never acts on the call: it becomes a pending action the employee confirms or rejects inside TeamBeats
/// (<see cref="ExternalActions"/>).
/// </summary>
public sealed class ExternalToolGateway(IDbSession session, ITenantContext context, ToolRegistry tools, AgentPolicyService policies,
    IEntitlementService entitlements, IAuditWriter audit, INotificationSender notifications, IClock clock) : IExternalToolGateway
{
    /// <summary>MCP tool calls per employee and client in a minute (spec 035 section 6).</summary>
    public const int CallsPerMinute = 60;

    /// <summary>Calls of one tool per employee and client in an hour, where section 6 names a figure.</summary>
    public static readonly IReadOnlyDictionary<string, int> HourlyLimits = new Dictionary<string, int>(StringComparer.Ordinal) { ["ask_teambeats"] = 20 };

    /// <summary>How long the employee has to confirm a write tool call (spec 035 FR-022).</summary>
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromMinutes(15);

    /// <summary>How long a confirmed action answers the same call again with its result, so a retry never acts twice.</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(24);

    private async Task<bool> KillSwitchOnAsync(CancellationToken ct) =>
        await session.ScalarAsync<bool?>("select active from agents.kill_switch", null, ct) == true;

    public async Task<string?> RefusalAsync(string clientId, CancellationToken ct)
    {
        if (await KillSwitchOnAsync(ct))
        {
            return ExternalRefusals.KillSwitch;
        }
        var external = (await policies.CurrentAsync(ct)).Policy.ExternalPolicy;
        if (!external.Enabled)
        {
            return ExternalRefusals.AccessOff;
        }
        return external.Client(clientId) is null ? ExternalRefusals.ClientNotAllowed : null;
    }

    /// <summary>
    /// The tools this caller may call now; what is not listed is refused as unknown when called. A tool whose rule needs
    /// an approver is left out, as approver tasks come with L3 (027 US4) and the runtime refuses such calls too.
    /// </summary>
    private async Task<IReadOnlyList<IAgentTool>> OfferedAsync(AgentPolicy policy, IReadOnlySet<string> scoped, CancellationToken ct)
    {
        var writes = policy.ExternalPolicy.WriteTools && await entitlements.IsEnabledAsync(Capabilities.ApiMcpWriteTools, ct);
        var offered = new List<IAgentTool>();
        foreach (var tool in tools.All.OrderBy(t => t.Definition.Name, StringComparer.Ordinal))
        {
            var d = tool.Definition;
            if (!d.Exposable || !scoped.Contains(d.Name) || !policy.Allows(d.Name) || !policy.ExternalPolicy.Allows(d.Name)
                || policy.RuleFor(d) is ToolApprovalRules.Block or ToolApprovalRules.Approval || (d.SideEffect != ToolSideEffects.Read && !writes))
            {
                continue;
            }
            if (d.RequiredCapability is { } capability && !await entitlements.IsEnabledAsync(capability, ct))
            {
                continue;
            }
            offered.Add(tool);
        }
        return offered;
    }

    public async Task<IReadOnlyList<ExternalTool>> ToolsAsync(ExternalCaller caller, IReadOnlySet<string> scoped, CancellationToken ct)
    {
        if (await RefusalAsync(caller.ClientId, ct) is not null)
        {
            return [];
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        return (await OfferedAsync(policy, scoped, ct))
            .Select(t => new ExternalTool(t.Definition.Name, t.Definition.Version, t.Definition.SideEffect, t.Definition.Description, t.Definition.Parameters))
            .ToList();
    }

    public async Task<ExternalToolOutcome> CallAsync(ExternalCaller caller, IReadOnlySet<string> scoped, string tool, JsonElement arguments, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var name = tool.Length > 100 ? tool[..100] : tool;
        if (context.ActorType != ActorType.User || context.ActorId != caller.UserId)
        {
            throw new InvalidOperationException("External tool calls run as the employee the token was issued for.");
        }
        if (await RefusalAsync(caller.ClientId, ct) is { } refusal)
        {
            return await RecordAsync(caller, name, null, null, ExternalToolOutcome.Refused(refusal), watch, ct);
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        if ((await OfferedAsync(policy, scoped, ct)).FirstOrDefault(t => t.Definition.Name == name) is not { } found)
        {
            return await RecordAsync(caller, name, null, null, ExternalToolOutcome.Refused(ExternalRefusals.UnknownTool), watch, ct);
        }
        var hash = Hash(name, arguments);
        if (await OverLimitAsync(caller, name, ct))
        {
            return await RecordAsync(caller, name, hash, arguments, ExternalToolOutcome.Refused(ExternalRefusals.RateLimited), watch, ct);
        }
        if (arguments.ValueKind != JsonValueKind.Object || !ToolSchema.Valid(found.Definition.Parameters, arguments))
        {
            return await RecordAsync(caller, name, hash, arguments, ExternalToolOutcome.Refused(ExternalRefusals.InvalidArguments), watch, ct);
        }
        if (found.Definition.SideEffect != ToolSideEffects.Read)
        {
            var (pending, decision) = await ActionAsync(caller, scoped, found, arguments, hash, ct);
            return await RecordAsync(caller, name, hash, arguments, pending, watch, ct, decision);
        }
        var cap = policy.ExternalPolicy.Client(caller.ClientId)!.MaxClassification;
        var outcome = await RunAsync(found, arguments, caller.UserId, cap, hash, ct);
        return await RecordAsync(caller, name, hash, arguments, outcome, watch, ct);
    }

    /// <summary>Runs the tool as the employee and checks what it gave back against the client's cap (spec 035 FR-021).</summary>
    private static async Task<ExternalToolOutcome> RunAsync(IAgentTool tool, JsonElement arguments, Guid user, string cap, string idempotencyKey, CancellationToken ct)
    {
        ToolResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(tool.Definition.TimeoutSeconds));
            try
            {
                result = await tool.ExecuteAsync(arguments, new ToolCallContext(Ids.New(), Guid.Empty, 0, user, false, cap, idempotencyKey), timeout.Token);
            }
            catch (AppException e) when (e.Status is 403 or 404)
            {
                // The employee may not read or do this now: refused as the agent runtime refuses it (027 US2 scenario 3).
                result = ToolResult.Failed(ExternalRefusals.Denied);
            }
            catch (AppException e) when (e.Status is 400 or 409)
            {
                // The owning module refused the values, such as a form field that does not validate.
                result = ToolResult.Failed(ExternalRefusals.InvalidArguments);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = ToolResult.Failed(ExternalRefusals.Timeout);
            }
        }
        // Nothing above the client's cap leaves TeamBeats, and Restricted never does.
        if (result.Ok && (Classifications.Rank(result.Classification) > Classifications.Rank(cap)
            || result.Sources.Any(s => Classifications.Rank(s.Classification) > Classifications.Rank(cap))))
        {
            result = ToolResult.Failed(ExternalRefusals.ClassificationBlocked);
        }
        return result.Ok
            ? new ExternalToolOutcome(true, result.Content, result.Classification, result.Sources)
            : ExternalToolOutcome.Refused(result.ErrorKey ?? ExternalRefusals.Denied);
    }

    /// <summary>
    /// A write tool call (spec 035 US3 scenarios 5 and 6). The same call again answers with what became of it: still
    /// waiting, confirmed with its result, or rejected, expired or failed (each told once). Otherwise a new pending
    /// action is stored with the exact parameters and the employee is notified; nothing acts until they confirm.
    /// </summary>
    private async Task<(ExternalToolOutcome Outcome, string Decision)> ActionAsync(ExternalCaller caller, IReadOnlySet<string> scoped, IAgentTool tool,
        JsonElement arguments, string hash, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var last = await session.QuerySingleOrDefaultAsync(
            """
            select id, status, reason, result, classification, sources::text, expires_at from agents.external_action
            where user_id = @user and app_id = @app and parameter_hash = @hash and created_at > @since
            order by created_at desc limit 1
            """, new { user = caller.UserId, app = caller.AppId, hash, since = now - ReplayWindow },
            r => (Id: r.GetGuid(0), Status: r.GetString(1), Reason: r.IsDBNull(2) ? null : r.GetString(2), Result: r.IsDBNull(3) ? null : r.GetString(3),
                Classification: r.IsDBNull(4) ? null : r.GetString(4), Sources: r.IsDBNull(5) ? null : r.GetString(5), ExpiresAt: r.GetFieldValue<DateTimeOffset>(6)), ct);
        if (last.Id != Guid.Empty)
        {
            switch (last.Status)
            {
                case ExternalActionStatuses.Pending when last.ExpiresAt > now:
                    return (Pending(last.Id, last.ExpiresAt), ToolDecisions.ConfirmationRequested);
                case ExternalActionStatuses.Confirmed:
                    var sources = last.Sources is null ? [] : JsonSerializer.Deserialize<List<ToolSource>>(last.Sources, ToolArgs.Options) ?? [];
                    return (new ExternalToolOutcome(true, last.Result ?? ToolArgs.Json(new { status = ExternalActionStatuses.Confirmed }), last.Classification ?? "internal",
                        sources), ToolDecisions.Executed);
                case ExternalActionStatuses.Pending or ExternalActionStatuses.Expired or ExternalActionStatuses.Rejected or ExternalActionStatuses.Failed
                    when last.Reason is null || !last.Reason.EndsWith(Told, StringComparison.Ordinal):
                    // Told once; the next call with the same parameters asks the employee again.
                    var status = last.Status == ExternalActionStatuses.Pending ? ExternalActionStatuses.Expired : last.Status;
                    await session.ExecAsync(
                        "update agents.external_action set status = @status, reason = coalesce(reason, '') || @told, decided_at = coalesce(decided_at, expires_at) where id = @id",
                        new { id = last.Id, status, told = Told }, ct);
                    var key = status switch
                    {
                        ExternalActionStatuses.Rejected => ExternalRefusals.Rejected,
                        ExternalActionStatuses.Expired => ExternalRefusals.ConfirmationExpired,
                        _ => last.Reason ?? ExternalRefusals.Denied,
                    };
                    return (ExternalToolOutcome.Refused(key), ToolDecisions.Cancelled);
            }
        }

        var id = Ids.New();
        var expires = now + ConfirmationWindow;
        await session.ExecAsync(
            """
            insert into agents.external_action (tenant_id, id, app_id, client_id, app_name, user_id, tool, scoped_tools, parameter_hash, parameters, status, created_at, expires_at)
            values (platform.current_tenant_id(), @id, @app, @client, @appName, @user, @tool, @scoped, @hash, @parameters, 'pending', @now, @expires)
            """, new
            {
                id, app = caller.AppId, client = caller.ClientId, appName = caller.AppName, user = caller.UserId, tool = tool.Definition.Name,
                scoped = scoped.Order(StringComparer.Ordinal).ToArray(), hash, parameters = arguments.GetRawText(), now, expires,
            }, ct);
        await notifications.SendAsync(new NotificationRequest([caller.UserId], NotificationCategories.ApprovalWaiting, ExternalActions.Template,
            ExternalActions.Link(id), new Dictionary<string, string> { ["app"] = caller.AppName }, DedupeKey: $"external_action:{id}"), ct);
        return (Pending(id, expires), ToolDecisions.ConfirmationRequested);
    }

    /// <summary>The mark that the client was told how a closed action ended, so it is told once.</summary>
    private const string Told = "|told";

    private static ExternalToolOutcome Pending(Guid id, DateTimeOffset expiresAt) =>
        new(true, ToolArgs.Json(new
        {
            status = "pending_confirmation", actionId = id, expiresAt, link = ExternalActions.Link(id),
            message = "Nothing has been done yet. The employee must confirm this action in TeamBeats within 15 minutes. Call the tool again with the same arguments to get the result.",
        }), "internal", []);

    /// <summary>
    /// Runs a pending action the employee confirmed (spec 035 FR-022): the gateway checks run again as at the call, with
    /// the scopes the token held then, and the tool runs with exactly the stored parameters.
    /// </summary>
    internal async Task<ExternalToolOutcome> ExecuteConfirmedAsync(ExternalActionRow action, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var caller = new ExternalCaller(action.AppId, action.ClientId, action.UserId, action.AppName);
        using var doc = JsonDocument.Parse(action.Parameters);
        var arguments = doc.RootElement.Clone();
        ExternalToolOutcome outcome;
        if (await RefusalAsync(action.ClientId, ct) is { } refusal)
        {
            outcome = ExternalToolOutcome.Refused(refusal);
        }
        else
        {
            var policy = (await policies.CurrentAsync(ct)).Policy;
            var scoped = action.ScopedTools.ToHashSet(StringComparer.Ordinal);
            if ((await OfferedAsync(policy, scoped, ct)).FirstOrDefault(t => t.Definition.Name == action.Tool) is not { } found)
            {
                outcome = ExternalToolOutcome.Refused(ExternalRefusals.UnknownTool);
            }
            else if (Hash(action.Tool, arguments) != action.Hash || !ToolSchema.Valid(found.Definition.Parameters, arguments))
            {
                outcome = ExternalToolOutcome.Refused(ExternalRefusals.InvalidArguments);
            }
            else
            {
                outcome = await RunAsync(found, arguments, action.UserId, policy.ExternalPolicy.Client(action.ClientId)!.MaxClassification,
                    "external_action:" + action.Id.ToString("N"), ct);
            }
        }
        return await RecordAsync(caller, action.Tool, action.Hash, arguments, outcome, watch, ct, outcome.Ok ? ToolDecisions.Confirmed : null);
    }

    /// <summary>The per minute limit of all tools and the per hour limit of the tool, counted on the execution records.</summary>
    private async Task<bool> OverLimitAsync(ExternalCaller caller, string tool, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where at > @minute), count(*) filter (where tool = @tool and decision = 'executed')
            from agents.external_call where user_id = @user and app_id = @app and at > @hour
            """, new { user = caller.UserId, app = caller.AppId, tool, minute = now.AddMinutes(-1), hour = now.AddHours(-1) },
            r => (Minute: r.GetInt64(0), Hour: r.GetInt64(1)), ct);
        return counts.Minute >= CallsPerMinute || (HourlyLimits.TryGetValue(tool, out var hourly) && counts.Hour >= hourly);
    }

    /// <summary>
    /// Writes the execution record (spec 035 US3 scenario 7) in the call's own transaction. Parameters are masked when
    /// what went back was Confidential (027 US6); the content itself is never stored.
    /// </summary>
    private async Task<ExternalToolOutcome> RecordAsync(ExternalCaller caller, string tool, string? hash, JsonElement? arguments, ExternalToolOutcome outcome,
        Stopwatch watch, CancellationToken ct, string? decision = null)
    {
        var classification = outcome.Ok ? outcome.Classification : null;
        string? parameters = null;
        if (arguments is { ValueKind: JsonValueKind.Object } a)
        {
            parameters = Classifications.Rank(classification) >= Classifications.Rank("confidential")
                ? JsonSerializer.Serialize(a.EnumerateObject().ToDictionary(p => p.Name, _ => "***"))
                : a.GetRawText();
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into agents.external_call (tenant_id, id, app_id, client_id, user_id, tool, parameter_hash, parameters, decision, reason, classification, duration_ms, at)
            values (platform.current_tenant_id(), @id, @app, @client, @user, @tool, @hash, @parameters::jsonb, @decision, @reason, @classification, @ms, @at)
            """, new
            {
                id, app = caller.AppId, client = caller.ClientId, user = caller.UserId, tool, hash, parameters,
                decision = decision ?? (outcome.Ok ? ToolDecisions.Executed : ToolDecisions.Refused), reason = outcome.ErrorKey, classification,
                ms = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds), at = clock.UtcNow,
            }, ct);
        if (!outcome.Ok && outcome.ErrorKey is ExternalRefusals.KillSwitch or ExternalRefusals.AccessOff or ExternalRefusals.ClientNotAllowed)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AgentExternalCallRefused, "api_app", caller.AppId.ToString(), AuditOutcome.Denied,
                Changes: new { tool, reason = outcome.ErrorKey }), ct);
        }
        return outcome;
    }

    internal static string Hash(string tool, JsonElement arguments) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + arguments.GetRawText()))).ToLowerInvariant();
}
