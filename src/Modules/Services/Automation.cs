using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>A connector step of a request as people see it: its state, the record the system returned, and an open incident to act on.</summary>
public sealed record AutomationStepView(string ElementId, string Name, string Status, string? ExternalReference, string? ErrorClass, string? ErrorCode,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, Guid? IncidentId, IReadOnlyDictionary<string, string>? Names = null);

/// <summary>The automatic steps of a request. <paramref name="CanOperate"/> is true for service owners, who may retry, skip or cancel a failed step.</summary>
public sealed record RequestAutomation(IReadOnlyList<AutomationStepView> Steps, bool CanOperate);

public sealed record IncidentResolveRequest(string? Resolution, string? Reason);

/// <summary>
/// Connector steps of service routes (spec 016 US8 scenario 5, FR-016; spec 017 T009). The process engine runs the steps;
/// this keeps the request in line with them: a succeeded step adds its reference to Tracking, a failed one shows
/// "automation failed" and waits for an owner, and the end of the route closes the request. An owner (service.manage at
/// the service's owning unit) retries, skips with a reason, or cancels.
/// </summary>
public sealed class Automation(IDbSession session, ITenantContext context, IAuthorizationService authz, IProcessEngine engine,
    Requests requests, RouteEnds routeEnds)
    : IMessageHandler<ProcessInstanceCompleted>, IMessageHandler<ServiceTaskCompleted>, IMessageHandler<ProcessIncidentRaised>
{
    /// <summary>Resolves an open incident of the request (an owner only), and records it on Tracking with the reason.</summary>
    public async Task<RequestDetail> ResolveAsync(Guid requestId, Guid incidentId, IncidentResolveRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var row = await requests.VisibleRowAsync(requestId, ct);
        var owner = await session.ScalarAsync<Guid?>("select owner_org_unit_id from services.service where id = @id", new { id = row.ServiceId }, ct);
        await authz.DemandAsync(Permissions.ServiceManage, owner is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant, ct);
        if (row.InstanceId is not { } instance || (await engine.IncidentsAsync(instance, ct)).FirstOrDefault(i => i.Id == incidentId) is not { } incident)
        {
            throw AppException.NotFound();
        }
        var resolved = await engine.ResolveIncidentAsync(incidentId, request.Resolution ?? string.Empty, request.Reason, ct);
        await requests.EventAsync(requestId, "automation_" + resolved.Resolution, await StepNameAsync(instance, incident.ElementId, ct), user, null, resolved.Reason, ct);
        return await requests.DetailAsync(requestId, ct);
    }

    private async Task<string?> StepNameAsync(Guid instance, string elementId, CancellationToken ct) =>
        (await engine.ServiceTasksAsync(instance, ct)).FirstOrDefault(s => s.ElementId == elementId)?.StepName;

    /// <summary>The request of an instance, only while that instance is still the request's current one (a returned request starts a new one).</summary>
    private async Task<RequestRow?> RequestOfAsync(Guid instanceId, string definitionKey, string businessKey, bool forUpdate, CancellationToken ct) =>
        Tasks.RequestIdOf(definitionKey, businessKey) is { } id && await requests.RowAsync(id, forUpdate, ct) is { } row && row.InstanceId == instanceId ? row : null;

    public async Task HandleAsync(ServiceTaskCompleted message, CancellationToken ct)
    {
        if (await RequestOfAsync(message.InstanceId, message.DefinitionKey, message.BusinessKey, false, ct) is { } row)
        {
            await requests.EventAsync(row.Id, "automation_done", await StepNameAsync(message.InstanceId, message.ElementId, ct), null, null,
                message.ExternalReference, ct);
        }
    }

    public async Task HandleAsync(ProcessIncidentRaised message, CancellationToken ct)
    {
        // The owner alert of US8 scenario 5 is a follow-up: owners see the failed step on the request and in the Connections run history.
        if (await RequestOfAsync(message.InstanceId, message.DefinitionKey, message.BusinessKey, false, ct) is { } row)
        {
            await requests.EventAsync(row.Id, "automation_failed", await StepNameAsync(message.InstanceId, message.ElementId, ct), null, null, null, ct);
        }
    }

    /// <summary>
    /// The end of a route that did not end on an approver's decision: after its connector steps, or on a business error
    /// path or an operator's cancel. A request the decision already closed is left as it is.
    /// </summary>
    public async Task HandleAsync(ProcessInstanceCompleted message, CancellationToken ct)
    {
        if (await RequestOfAsync(message.InstanceId, message.DefinitionKey, message.BusinessKey, true, ct) is not { } row || !Waiting(row))
        {
            return;
        }
        // A step graph route ends with the result of the end step it reached; an R1 route with its outcome.
        var status = message.Result is { } result ? RouteEnds.StatusFor(result) : message.Outcome switch
        {
            InstanceOutcomes.Approved => RequestStatuses.Completed,
            InstanceOutcomes.Rejected => RequestStatuses.Rejected,
            _ => null,
        };
        if (message.Outcome == InstanceOutcomes.Cancelled)
        {
            status = RequestStatuses.Cancelled;
        }
        if (status is null)
        {
            return;
        }
        await routeEnds.CloseAsync(row, status, ct, record: true);
    }

    /// <summary>Still running: before or during approval, or approved with automatic steps left.</summary>
    private static bool Waiting(RequestRow row) =>
        row.Status is RequestStatuses.Submitted or RequestStatuses.InApproval || row.Status == RequestStatuses.Approved && row.ClosedAt is null;
}

/// <summary>A connector action a service owner may place as a step: one that declares its fields (spec 017 FR-009).</summary>
public sealed record RouteActionView(Guid ConnectionId, string ConnectionName, string Action, string SideEffect, bool Idempotent,
    IReadOnlyList<string> BusinessErrors, IReadOnlyList<TeamBeats.Modules.Integrations.Contracts.ConnectorInput> Inputs, bool Enabled);

/// <summary>The automatic steps a service owner can use; empty with <paramref name="Available"/> false without the package capability.</summary>
public sealed record RouteActionsView(bool Available, IReadOnlyList<RouteActionView> Actions);

/// <summary>The connector actions for the route designer (spec 016 US8 scenario 1, FR-016). Names and fields only; never settings or secrets.</summary>
public sealed class RouteActions(IAuthorizationService authz, TeamBeats.Entitlements.Contracts.IEntitlementService entitlements, TeamBeats.Modules.Integrations.Contracts.IConnectorActions actions)
{
    public async Task<RouteActionsView> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(TeamBeats.Entitlements.Contracts.Capabilities.WorkflowCatalogue, ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.ServiceManage, ct))
        {
            throw AppException.Denied();
        }
        if (!await entitlements.IsEnabledAsync(TeamBeats.Entitlements.Contracts.Capabilities.WorkflowAutomation, ct))
        {
            return new RouteActionsView(false, []);
        }
        var list = (await actions.ListAsync(ct))
            .Where(a => a.Inputs is not null)
            .Select(a => new RouteActionView(a.ConnectionId, a.ConnectionName, a.Action, a.SideEffect, a.Idempotent, a.BusinessErrors, a.Inputs!, a.Enabled))
            .ToList();
        return new RouteActionsView(true, list);
    }
}
