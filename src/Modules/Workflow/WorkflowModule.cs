using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
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
        services.AddMessageHandler<WorkingCalendarChanged, CalendarChangeHandler>("workflow.due_recalculation");
        services.AddMessageHandler<ConnectorActionCompleted, ConnectorStepFinished>("workflow.connector_step_finished");
        return services;
    }
}

/// <summary>Moves a service task on when its connector job ends (spec 024 FR-010, spec 017 US2).</summary>
public sealed class ConnectorStepFinished(ProcessEngine engine) : IMessageHandler<ConnectorActionCompleted>
{
    public Task HandleAsync(ConnectorActionCompleted message, CancellationToken ct) => engine.OnConnectorCompletedAsync(message, ct);
}
