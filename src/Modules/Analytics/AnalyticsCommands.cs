using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Analytics;

/// <summary>This module's commands in the shell's command entry point (spec 008 FR-008, T020). The workspace checks access again.</summary>
internal sealed class AnalyticsCommands : IShellCommands
{
    public IEnumerable<ShellCommand> Commands() =>
    [
        new("open_analytics", "/analytics", "trend-up", Capabilities.AnalyticsCore, Permissions.AnalyticsView),
    ];
}
