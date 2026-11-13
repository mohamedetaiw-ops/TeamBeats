using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.TenantContext;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using System.Text.Json.Nodes;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Workflow;

/// <summary>Process engine of spec 024 (module map: Workflow). R1: the engine core that approval routes need.</summary>
public static class WorkflowModule
{
    public static readonly MigrationSource Migrations = new("workflow", typeof(WorkflowModule).Assembly);

    public static IServiceCollection AddWorkflowModule(this IServiceCollection services)
    {
        services.AddScoped<EngineJobs>();
        services.AddScoped<ProcessEngine>();
        services.AddScoped<IProcessEngine>(sp => sp.GetRequiredService<ProcessEngine>());
        services.AddScoped<ProcessModels>();
        services.AddScoped<ProcessSandbox>();
        services.AddScoped<ProcessOperations>();
        services.AddScoped<Decisions.DecisionModels>();
        services.AddScoped<ProcessModelTasks>();
        services.AddScoped<ITaskSource>(sp => sp.GetRequiredService<ProcessModelTasks>());
        services.AddMessageHandler<WorkingCalendarChanged, CalendarChangeHandler>("workflow.due_recalculation");
        services.AddMessageHandler<ConnectorActionCompleted, ConnectorStepFinished>("workflow.connector_step_finished");
        services.AddMessageHandler<InboundWebhookReceived, InboundMessageReceived>("workflow.inbound_message");
        return services;
    }

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workflow").RequireTenantUser().WithTags("Workflow");

        group.MapGet("/processes", async (ProcessModels models, CancellationToken ct) => Results.Ok(await models.ListAsync(ct)));

        group.MapGet("/processes/{key}", async (string key, ProcessModels models, CancellationToken ct) => Results.Ok(await models.GetAsync(key, ct)));

        group.MapPut("/processes/{key}", async (string key, ProcessModelRequest request, ProcessModels models, IDbSession session, CancellationToken ct) =>
        {
            var view = await models.SaveAsync(key, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/processes/{key}:publish", async (string key, ProcessModels models, IDbSession session, CancellationToken ct) =>
        {
            var view = await models.PublishAsync(key, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/processes/{key}:test", async (string key, ProcessTestRequest request, ProcessModels models, CancellationToken ct) =>
            Results.Ok(await models.TestRunAsync(key, request, ct)));

        group.MapGet("/processes/{key}/versions/{version:int}/bpmn", async (string key, int version, ProcessModels models, CancellationToken ct) =>
            Results.Text(await models.BpmnAsync(key, version, ct), "application/xml"));

        group.MapPost("/processes/{key}/instances", async (string key, StartProcessRequest request, ProcessModels models, IDbSession session, CancellationToken ct) =>
        {
            var instance = await models.StartAsync(key, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/workflow/instances/{instance.Id}", instance);
        });

        group.MapGet("/instances", async ([AsParameters] InstanceQuery q, ProcessOperations ops, CancellationToken ct) =>
            Results.Ok(await ops.SearchAsync(new InstanceSearch(q.Process, q.Version, q.State, q.BusinessKey, q.StartedBy, q.OrgUnitId, q.From, q.To, q.WithIncident, q.Limit), ct)));

        group.MapGet("/instances/{id:guid}", async (Guid id, ProcessOperations ops, CancellationToken ct) => Results.Ok(await ops.GetAsync(id, ct)));

        group.MapPut("/instances/{id:guid}/variables", async (Guid id, VariablesChange request, ProcessOperations ops, IDbSession session, CancellationToken ct) =>
        {
            var view = await ops.ChangeVariablesAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/instances/{id:guid}:cancel", async (Guid id, OperatorReason request, ProcessOperations ops, IDbSession session, CancellationToken ct) =>
        {
            var view = await ops.CancelAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/incidents/{id:guid}:resolve", async (Guid id, IncidentResolution request, ProcessOperations ops, IDbSession session, CancellationToken ct) =>
        {
            var view = await ops.ResolveIncidentAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapGet("/processes/{key}/monitoring", async (string key, int? version, DateTimeOffset? from, DateTimeOffset? to, ProcessOperations ops, CancellationToken ct) =>
            Results.Ok(await ops.MonitoringAsync(key, version, from, to, ct)));

        group.MapGet("/decisions", async (Decisions.DecisionModels decisions, CancellationToken ct) => Results.Ok(await decisions.ListAsync(ct)));

        group.MapGet("/decisions/{key}", async (string key, Decisions.DecisionModels decisions, CancellationToken ct) => Results.Ok(await decisions.GetAsync(key, ct)));

        group.MapPut("/decisions/{key}", async (string key, Decisions.DecisionModelRequest request, Decisions.DecisionModels decisions, IDbSession session, CancellationToken ct) =>
        {
            var view = await decisions.SaveAsync(key, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/decisions/{key}:publish", async (string key, Decisions.DecisionModels decisions, IDbSession session, CancellationToken ct) =>
        {
            var view = await decisions.PublishAsync(key, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/decisions/{key}:test", async (string key, Decisions.DecisionTestRequest request, Decisions.DecisionModels decisions, CancellationToken ct) =>
            Results.Ok(await decisions.TestAsync(key, request, ct)));

        return app;
    }
}

/// <summary>Moves a service task on when its connector job ends (spec 024 FR-010, spec 017 US2).</summary>
public sealed class ConnectorStepFinished(ProcessEngine engine) : IMessageHandler<ConnectorActionCompleted>
{
    public Task HandleAsync(ConnectorActionCompleted message, CancellationToken ct) => engine.OnConnectorCompletedAsync(message, ct);
}

/// <summary>
/// Correlates a signed inbound webhook whose connection maps it to a process message (spec 024 FR-009, spec 017 US5).
/// The connector runtime has already checked the signature, the time window and the delivery ID; the delivery ID makes
/// the message ID, so a redelivery correlates once. The declared fields become the payload.
/// </summary>
public sealed class InboundMessageReceived(ProcessEngine engine, IEntitlementService entitlements) : IMessageHandler<InboundWebhookReceived>
{
    public async Task HandleAsync(InboundWebhookReceived message, CancellationToken ct)
    {
        if (message.Target != "message" || !await entitlements.IsEnabledAsync(Capabilities.WorkflowBpmn, ct))
        {
            return;
        }
        var payload = new JsonObject();
        foreach (var (name, value) in message.Fields)
        {
            payload[name] = value;
        }
        await engine.CorrelateAsync(message.Name, message.CorrelationKey, $"webhook:{message.ConnectionId:N}:{message.DeliveryId}", payload, "webhook", null, ct);
    }
}

/// <summary>The query string of the instance search.</summary>
public sealed record InstanceQuery(string? Process, int? Version, string? State, string? BusinessKey, Guid? StartedBy, Guid? OrgUnitId, DateTimeOffset? From,
    DateTimeOffset? To, bool? WithIncident, int? Limit);
