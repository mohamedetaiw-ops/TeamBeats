using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Hr;

/// <summary>This module's commands in the shell's command entry point (spec 008 FR-008, T020). Its pages check access again.</summary>
internal sealed class LeaveCommands : IShellCommands
{
    public IEnumerable<ShellCommand> Commands() =>
    [
        new("request_leave", "/leave", "leave", Capabilities.HrLeave),
    ];
}
