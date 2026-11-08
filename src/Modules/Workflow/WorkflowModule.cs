using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
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
        return services;
    }
}
