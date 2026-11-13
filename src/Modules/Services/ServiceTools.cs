using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Services.Contracts;

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

/// <summary>
/// <c>submit_service_request</c> (spec 035 section 6, scope <c>requests:submit</c>), a write tool for external AI clients
/// only: it submits a request for a form service in the employee's own name, with their rights, exactly as they would.
/// The gateway never runs it on the client's call; it runs once the employee confirmed the exact values inside TeamBeats.
/// Values are taken only for the fields of the form, and the form checks them as in TeamBeats.
/// </summary>
public sealed class SubmitServiceRequestTool(Catalogue catalogue, Requests requests) : IAgentTool
{
    public const string Name = "submit_service_request";

    public ToolDefinition Definition { get; } = new(
        Name, "services", 1, ToolSideEffects.WriteSelf, ToolApprovalRules.Confirm,
        ServiceToolJson.Schema(
            """
            {"type":"object","additionalProperties":false,"required":["serviceId"],
             "properties":{"serviceId":{"type":"string","minLength":36,"maxLength":36},
                           "fields":{"type":"array","maxItems":50,"items":{"type":"object","additionalProperties":false,"required":["key","value"],
                             "properties":{"key":{"type":"string","minLength":1,"maxLength":60},"value":{"type":"string","maxLength":2000}}}}}}
            """),
        "internal", Idempotent: false, TimeoutSeconds: 20,
        "Submit a request for a form service in TeamBeats in the employee's name. Give the service ID and the form fields as key and value pairs (dates as YYYY-MM-DD, several choices separated by commas). The employee must confirm it in TeamBeats before anything is submitted.",
        Capabilities.WorkflowCatalogue, Exposable: true);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct)
    {
        if (ToolArgs.Id(arguments, "serviceId") is not { } serviceId)
        {
            return ToolResult.Failed("tool.invalid_arguments");
        }
        var detail = await catalogue.DetailAsync(serviceId, ct);
        if (detail.Service.Kind != ServiceKinds.Form || detail.Version is null)
        {
            return ToolResult.Failed("tool.denied");
        }
        var data = RequestPrepareTool.Values(detail.Form, arguments);
        var (submitted, _) = await requests.SubmitAsync(new RequestSubmit(serviceId, data, null, context.IdempotencyKey), ct);
        var request = submitted.Request;
        var link = RequestNotifier.Link(request.Id);
        var content = ToolArgs.Json(new { id = request.Id, number = request.Number, status = request.Status, submittedAt = request.SubmittedAt, link });
        return new ToolResult(true, content, "internal", [new ToolSource("requests", request.Id, request.Number, link, "internal")]);
    }
}
