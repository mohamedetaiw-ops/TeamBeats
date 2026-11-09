using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Tools the Services module offers to agents (spec 027 FR-005): read-only views of the person's own requests and task
/// inbox. Each runs as the person through the module's own use cases, so it returns exactly what their pages show.
/// </summary>
internal static class ServiceToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>The status of requests the person filed or that were filed for them (spec 016 FR-005).</summary>
public sealed class RequestStatusTool(Requests requests, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "requests.status";
    public static readonly IReadOnlyList<string> Statuses = ["submitted", "in_approval", "returned", "approved", "rejected", "cancelled", "completed"];

    public ToolDefinition Definition { get; } = new(
        Name, "services", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ServiceToolJson.Schema(
            """
            {"type":"object","additionalProperties":false,
             "properties":{"status":{"enum":["submitted","in_approval","returned","approved","rejected","cancelled","completed"]},
                           "limit":{"type":"integer","minimum":1,"maximum":20}}}
            """),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "List the employee's own service requests, newest first, with request number, service, status, current step and dates.",
        Capabilities.WorkflowCatalogue);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowCatalogue, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        string? status = null;
        var limit = 10;
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("status", out var s))
            {
                status = s.ValueKind == JsonValueKind.String && Statuses.Contains(s.GetString()) ? s.GetString() : null;
                if (status is null)
                {
                    return ToolResult.Failed("tool.invalid_arguments");
                }
            }
            if (arguments.TryGetProperty("limit", out var l))
            {
                if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out limit) || limit is < 1 or > 20)
                {
                    return ToolResult.Failed("tool.invalid_arguments");
                }
            }
        }
        var list = await requests.MineAsync(status, limit, ct);
        var rows = list.Select(r => new { r.Number, service = r.ServiceNames, r.Status, step = r.CurrentStep, r.SubmittedAt, r.ClosedAt });
        var sources = list.Select(r => new ToolSource("service_request", r.Id, r.Number, RequestNotifier.Link(r.Id), "internal")).ToList();
        return new ToolResult(true, JsonSerializer.Serialize(rows, ServiceToolJson.Options), "internal", sources);
    }
}

/// <summary>The tasks and approvals waiting for the person in their inbox (spec 016 US4).</summary>
public sealed class TaskInboxTool(Tasks tasks, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "tasks.inbox";
    public const int MaxItems = 20;

    public ToolDefinition Definition { get; } = new(
        Name, "services", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ServiceToolJson.Schema("""{"type":"object","additionalProperties":false,"properties":{}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "List the tasks and approvals waiting for the employee, with name, request number, requester and due date.",
        Capabilities.WorkflowCatalogue);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowCatalogue, ct))
        {
            return ToolResult.Failed("tool.not_in_package");
        }
        var items = (await tasks.InboxAsync(null, ct)).OrderBy(i => i.DueAt ?? DateTimeOffset.MaxValue).ThenBy(i => i.CreatedAt).Take(MaxItems).ToList();
        var rows = items.Select(i => new { name = i.Title ?? (IReadOnlyDictionary<string, string>?)i.Step ?? i.ServiceNames, i.Number, requester = i.Requester?.DisplayName, i.CreatedAt, i.DueAt });
        var sources = items.Where(i => i.Link is not null).Select(i => new ToolSource(i.Source, i.TaskId, i.Number ?? i.Name, i.Link!, "internal")).ToList();
        return new ToolResult(true, JsonSerializer.Serialize(rows, ServiceToolJson.Options), "internal", sources);
    }
}
