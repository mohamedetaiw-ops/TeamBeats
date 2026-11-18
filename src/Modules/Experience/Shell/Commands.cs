using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Shell;

/// <summary>A command the viewer may use now: its key names its label in the web app, and the link opens the module's page.</summary>
public sealed record CommandView(string Key, string Href, string Icon);

/// <summary>
/// The shell's command entry point (spec 008 FR-008, US4 scenario 8, T020). Modules register their commands through
/// <see cref="IShellCommands"/>; a command is listed only while its capability is on for the tenant and the viewer holds
/// its permission somewhere, so one the viewer cannot use never appears. Opening it still runs the module's own checks.
/// </summary>
public sealed class Commands(IEnumerable<IShellCommands> registrations, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context)
{
    public async Task<IReadOnlyList<CommandView>> ListAsync(CancellationToken ct)
    {
        context.RequireUser();
        var result = new List<CommandView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in registrations.SelectMany(r => r.Commands()))
        {
            if (!seen.Add(command.Key) || !await entitlements.IsEnabledAsync(command.Capability, ct))
            {
                continue;
            }
            if (command.Permission is { } permission && !await authz.HoldsAnywhereAsync(permission, ct))
            {
                continue;
            }
            result.Add(new CommandView(command.Key, command.Href, command.Icon));
        }
        return result;
    }
}
