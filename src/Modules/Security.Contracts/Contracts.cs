using TeamBeats.Primitives;

namespace TeamBeats.Modules.Security.Contracts;

/// <summary>
/// The tenant security policy (spec 026 US1 and US2) as other modules read it: network rules, MFA assurance and session
/// rules. Addresses and ranges are CIDR text. Identity enforces the session rules; the network rules are enforced in the
/// request pipeline by the Security module itself.
/// </summary>
public sealed record SecurityPolicySnapshot(
    IReadOnlyList<string> IpAllow,
    IReadOnlyList<string> IpBlock,
    IReadOnlyList<string> AdminIpAllow,
    IReadOnlyDictionary<string, IReadOnlyList<string>> NetworkZones,
    bool RequireMfa,
    int StepUpMinutes,
    int IdleMinutes,
    int AbsoluteHours,
    int? MaxSessions,
    int RememberDeviceDays,
    int Version)
{
    /// <summary>Section 6 defaults: no network rules, step-up after 10 minutes, idle 30 minutes, absolute 12 hours.</summary>
    public static readonly SecurityPolicySnapshot Default = new([], [], [], new Dictionary<string, IReadOnlyList<string>>(), false, 10, 30, 12, null, 0, 0);
}

/// <summary>The current tenant's policy, cached per tenant and refreshed when it changes.</summary>
public interface ISecurityPolicy
{
    Task<SecurityPolicySnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>Setting names only, never values.</summary>
public sealed record SecurityPolicyChanged(int Version, IReadOnlyList<string> Settings) : IIntegrationEvent;
