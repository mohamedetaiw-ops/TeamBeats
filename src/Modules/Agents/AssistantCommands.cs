using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Agents;

/// <summary>This module's commands in the shell's command entry point (spec 008 FR-008, T020). Its pages check access again.</summary>
internal sealed class AssistantCommands : IShellCommands
{
    public IEnumerable<ShellCommand> Commands() =>
    [
        new("ask_assistant", "/assistant", "spark", Capabilities.AiAssistant),
    ];
}
