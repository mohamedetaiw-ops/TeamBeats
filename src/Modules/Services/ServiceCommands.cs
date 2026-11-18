using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>This module's commands in the shell's command entry point (spec 008 FR-008, T020). Its pages check access again.</summary>
internal sealed class ServiceCommands : IShellCommands
{
    public IEnumerable<ShellCommand> Commands() =>
    [
        new("new_request", "/services", "plus", Capabilities.WorkflowCatalogue),
        new("my_tasks", "/tasks", "tasks", Capabilities.WorkflowCatalogue),
    ];
}
