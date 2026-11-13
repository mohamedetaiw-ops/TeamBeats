using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
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
/// execution record. Write tools wait for the employee's confirmation inside TeamBeats; until that is built they are
/// neither listed nor called.
/// </summary>
public sealed class ExternalToolGateway(IDbSession session, ITenantContext context, ToolRegistry tools, AgentPolicyService policies,
    IEntitlementService entitlements, IAuditWriter audit, IClock clock) : IExternalToolGateway
{
    /// <summary>MCP tool calls per employee and client in a minute (spec 035 section 6).</summary>
    public const int CallsPerMinute = 60;

    /// <summary>Calls of one tool per employee and client in an hour, where section 6 names a figure.</summary>
    public static readonly IReadOnlyDictionary<string, int> HourlyLimits = new Dictionary<string, int>(StringComparer.Ordinal) { ["ask_teambeats"] = 20 };

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

    /// <summary>The tools this caller may call now; what is not listed is refused as unknown when called.</summary>
    private async Task<IReadOnlyList<IAgentTool>> OfferedAsync(AgentPolicy policy, IReadOnlySet<string> scoped, CancellationToken ct)
    {
        var offered = new List<IAgentTool>();
        foreach (var tool in tools.All.OrderBy(t => t.Definition.Name, StringComparer.Ordinal))
        {
            var d = tool.Definition;
            if (!d.Exposable || !scoped.Contains(d.Name) || !policy.Allows(d.Name) || !policy.ExternalPolicy.Allows(d.Name)
                || policy.RuleFor(d) == ToolApprovalRules.Block || d.SideEffect != ToolSideEffects.Read)
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

        var cap = policy.ExternalPolicy.Client(caller.ClientId)!.MaxClassification;
        ToolResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(found.Definition.TimeoutSeconds));
            try
            {
                result = await found.ExecuteAsync(arguments, new ToolCallContext(Ids.New(), Guid.Empty, 0, caller.UserId, false, cap, hash), timeout.Token);
            }
            catch (AppException e) when (e.Status is 403 or 404)
            {
                // The employee may not read this now: refused as the agent runtime refuses it (027 US2 scenario 3).
                result = ToolResult.Failed(ExternalRefusals.Denied);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                result = ToolResult.Failed(ExternalRefusals.Timeout);
            }
        }
        // Output check: nothing above the client's cap leaves TeamBeats, and Restricted never does (spec 035 FR-021).
        if (result.Ok && (Classifications.Rank(result.Classification) > Classifications.Rank(cap)
            || result.Sources.Any(s => Classifications.Rank(s.Classification) > Classifications.Rank(cap))))
        {
            result = ToolResult.Failed(ExternalRefusals.ClassificationBlocked);
        }
        var outcome = result.Ok
            ? new ExternalToolOutcome(true, result.Content, result.Classification, result.Sources)
            : ExternalToolOutcome.Refused(result.ErrorKey ?? ExternalRefusals.Denied);
        return await RecordAsync(caller, name, hash, arguments, outcome, watch, ct);
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
        Stopwatch watch, CancellationToken ct)
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
                decision = outcome.Ok ? ToolDecisions.Executed : ToolDecisions.Refused, reason = outcome.ErrorKey, classification,
                ms = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds), at = clock.UtcNow,
            }, ct);
        if (!outcome.Ok && outcome.ErrorKey is ExternalRefusals.KillSwitch or ExternalRefusals.AccessOff or ExternalRefusals.ClientNotAllowed)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AgentExternalCallRefused, "api_app", caller.AppId.ToString(), AuditOutcome.Denied,
                Changes: new { tool, reason = outcome.ErrorKey }), ct);
        }
        return outcome;
    }

    private static string Hash(string tool, JsonElement arguments) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\n" + arguments.GetRawText()))).ToLowerInvariant();
}
