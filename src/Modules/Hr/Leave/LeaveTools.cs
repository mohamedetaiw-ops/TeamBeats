using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>
/// Leave tools for agents (spec 014 US7, spec 022, spec 027 FR-005): self-scoped reads that run as the person through
/// the leave use cases, so they return what the person's leave page shows. Each result says where the figures come
/// from and when they were read, which the answer shows with its source.
/// </summary>
internal static class LeaveToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>
/// <c>leave.balance</c>: the caller's leave balances. Another person's are given only to an HR officer who covers them
/// (spec 004 through the leave ledger rule); anyone else is refused with <c>tool.other_person</c>, which never says
/// whether that person exists.
/// </summary>
public sealed class LeaveBalanceTool(LeaveRequests leave, IEntitlementService entitlements, IClock clock) : IAgentTool
{
    public const string Name = "leave.balance";
    public const string Source = "leave_balance";

    public ToolDefinition Definition { get; } = new(
        Name, "hr", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        LeaveToolJson.Schema("""{"type":"object","additionalProperties":false,"properties":{"person":{"type":"string","format":"uuid"}}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "Read the employee's own leave balances per leave type: entitlement balance, pending days, days available, unit, leave year, where the figures come from and when. Leave out person to read the employee's own.",
        Capabilities.HrLeave);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var person = context.UserId;
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("person", out var p))
        {
            if (p.ValueKind != JsonValueKind.String || !Guid.TryParse(p.GetString(), out person))
            {
                return ToolResult.Failed("tool.invalid_arguments");
            }
        }
        if (await leave.BalancesForAsync(person, ct) is not { } balances)
        {
            return ToolResult.Failed("tool.other_person");
        }
        var read = clock.UtcNow;
        var rows = balances.Select(b => new
        {
            type = b.TypeNames, b.LeaveYear, b.Balance, b.Pending, b.Available, b.Unit, source = b.Source, asOf = b.AsOf ?? read, b.Stale,
        });
        var title = person == context.UserId ? "My leave balance" : "Leave balance";
        var link = person == context.UserId ? "/leave" : "/admin/leave";
        return new ToolResult(true, JsonSerializer.Serialize(new { readAt = read, balances = rows }, LeaveToolJson.Options), "internal",
            [new ToolSource(Source, person, title, link, "internal")]);
    }
}

/// <summary><c>leave.my_requests</c>: the caller's open leave, pending or approved and not yet over, soonest first.</summary>
public sealed class MyLeaveRequestsTool(LeaveRequests leave, IEntitlementService entitlements, IClock clock) : IAgentTool
{
    public const string Name = "leave.my_requests";
    public const int MaxItems = 20;

    public ToolDefinition Definition { get; } = new(
        Name, "hr", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        LeaveToolJson.Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "List the employee's own open leave requests: pending, or approved and not yet over, with number, type, dates, days and status.",
        Capabilities.HrLeave);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var open = await leave.MyOpenAsync(MaxItems, ct);
        var rows = open.Select(r => new { r.Number, type = r.TypeNames, r.Start, r.End, r.Days, r.Unit, r.Status, r.CreatedAt });
        var sources = open.Select(r => new ToolSource("leave_request", r.Id, r.Number, LeaveRequests.Link(r.Id), "internal")).ToList();
        return new ToolResult(true, JsonSerializer.Serialize(new { readAt = clock.UtcNow, requests = rows }, LeaveToolJson.Options), "internal", sources);
    }
}
