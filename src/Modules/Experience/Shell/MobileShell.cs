using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Shell;

/// <summary>
/// What the store app may do with this workspace (spec 030 R3, MOB-021 to MOB-023): whether the workspace takes the
/// app at all, the app lock and its minutes, the sign-in hosts the app may open in place, and whether native push can
/// be registered. It is read before sign-in, so it says nothing a sign-in page does not already show.
/// </summary>
public sealed record MobileShellConfig(bool Available, string AppLock, int AppLockMinutes, IReadOnlyList<string> SignInHosts, bool NativePush);

public sealed class MobileShell(IEntitlementService entitlements, ISecurityPolicy policies, ISignInMethods signIn, INativePush native)
{
    public static readonly MobileShellConfig Unavailable =
        new(false, SecurityPolicySnapshot.DefaultAppLock, SecurityPolicySnapshot.DefaultAppLockMinutes, [], false);

    public async Task<MobileShellConfig> ConfigAsync(TenantInfo tenant, CancellationToken ct)
    {
        if (tenant.Status != TenantLifecycleStatus.Active || !await entitlements.IsEnabledAsync(Capabilities.MobileNativeShell, ct))
        {
            return Unavailable;
        }
        var policy = await policies.GetAsync(ct);
        var methods = await signIn.CurrentAsync(ct);
        var push = native.Available && await entitlements.IsEnabledAsync(Capabilities.MobilePush, ct);
        return new MobileShellConfig(true, policy.AppLock, policy.AppLockMinutes, methods.SignInHosts ?? [], push);
    }
}
