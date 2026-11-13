using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>
/// <c>list_services</c> (spec 035 section 6, scope <c>services:read</c>), exposable to external AI clients: the
/// catalogue the employee is offered, as in TeamBeats, with names, descriptions and links. Catalogue data is Internal.
/// </summary>
public sealed class ListServicesTool(Catalogue catalogue) : IAgentTool
{
    public const string Name = "list_services";

    public ToolDefinition Definition { get; } = new(
        Name, "services", 1, ToolSideEffects.Read, ToolApprovalRules.Execute,
        ToolArgs.Schema("""{"type":"object","additionalProperties":false,"properties":{"query":{"type":"string","maxLength":200}}}"""),
        "internal", Idempotent: true, TimeoutSeconds: 10,
        "List the services the employee can request in TeamBeats, optionally matching a query, with names, descriptions and IDs.",
        Capabilities.WorkflowCatalogue, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        var view = await catalogue.ListAsync(ToolArgs.String(arguments, "query"), null, ct);
        var content = ToolArgs.Json(view.Services.Select(s => new
        {
            id = s.Id, kind = s.Kind, names = s.Names, descriptions = s.Descriptions, link = ServiceSearchSource.Link(s.Id),
        }));
        return new ToolResult(true, content, "internal",
            view.Services.Select(s => new ToolSource("services", s.Id, s.Names.Values.FirstOrDefault() ?? string.Empty, ServiceSearchSource.Link(s.Id), "internal")).ToList());
    }
}

/// <summary><c>list_my_requests</c> (scope <c>requests:read</c>): the employee's own requests, as the agent tool reads them.</summary>
public sealed class ListMyRequestsTool(Requests requests, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "list_my_requests";
    private readonly RequestStatusTool inner = new(requests, entitlements);

    public ToolDefinition Definition => inner.Definition with
    {
        Name = Name, Exposable = true,
        Description = "List the employee's own TeamBeats service requests, newest first, with request number, service, status, current step and dates.",
    };

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct) => inner.ExecuteAsync(arguments, context, ct);
}

/// <summary><c>list_my_tasks</c> (scope <c>tasks:read</c>): the employee's task inbox. Deciding a task stays inside TeamBeats.</summary>
public sealed class ListMyTasksTool(Tasks tasks, IEntitlementService entitlements) : IAgentTool
{
    public const string Name = "list_my_tasks";
    private readonly TaskInboxTool inner = new(tasks, entitlements);

    public ToolDefinition Definition => inner.Definition with
    {
        Name = Name, Exposable = true,
        Description = "List the tasks and approvals waiting for the employee in TeamBeats, with name, request number, requester and due date. Decisions are made in TeamBeats.",
    };

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct) => inner.ExecuteAsync(arguments, context, ct);
}
