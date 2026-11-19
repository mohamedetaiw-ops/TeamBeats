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
    int Version,
    int OfflineMaxItems = SecurityPolicySnapshot.DefaultOfflineItems,
    int OfflineMaxMb = SecurityPolicySnapshot.DefaultOfflineMb,
    int OfflineDays = SecurityPolicySnapshot.DefaultOfflineDays,
    IReadOnlyList<string>? GuestAllowDomains = null,
    IReadOnlyList<string>? GuestBlockDomains = null)
{
    /// <summary>
    /// Spec 026 US3 and section 14: whether the tenant's guest domain rules refuse an email domain. An entry covers the
    /// domain and its subdomains. Blocked wins over allowed; an empty allowed list allows every domain not blocked.
    /// Returns null when allowed, otherwise the validation key to show.
    /// </summary>
    public string? GuestDomainRefusal(string domain)
    {
        static bool Covers(string entry, string d) =>
            d.Equals(entry, StringComparison.OrdinalIgnoreCase) || d.EndsWith("." + entry, StringComparison.OrdinalIgnoreCase);
        var block = GuestBlockDomains ?? [];
        var allow = GuestAllowDomains ?? [];
        if (block.Any(b => Covers(b, domain)))
        {
            return "validation.spaces.guest_domain_blocked";
        }
        return allow.Count > 0 && !allow.Any(a => Covers(a, domain)) ? "validation.spaces.guest_domain_not_allowed" : null;
    }

    /// <summary>Spec 030 section 6: a device keeps up to 50 items and 50 MB for 7 days unless the tenant says otherwise.</summary>
    public const int DefaultOfflineItems = 50;
    public const int DefaultOfflineMb = 50;
    public const int DefaultOfflineDays = 7;

    /// <summary>Section 6 defaults: no network rules, step-up after 10 minutes, idle 30 minutes, absolute 12 hours.</summary>
    public static readonly SecurityPolicySnapshot Default = new([], [], [], new Dictionary<string, IReadOnlyList<string>>(), false, 10, 30, 12, null, 0, 0);
}

/// <summary>The current tenant's policy, cached per tenant and refreshed when it changes.</summary>
public interface ISecurityPolicy
{
    Task<SecurityPolicySnapshot> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Authentication assurance (spec 026 US2 scenario 1): whether a sign-in token shows multi-factor authentication under
/// the tenant's claim mapping for the provider (default: <c>amr</c> contains <c>mfa</c>). A sign-in that shows it is
/// recorded, so the tenant can then require MFA (section 6).
/// </summary>
public interface IAuthenticationAssurance
{
    Task<bool> CheckMfaAsync(string provider, IReadOnlyList<string> amr, string? acr, CancellationToken ct);
}

/// <summary>A security alert was raised (spec 026 US5); kind and ID only.</summary>
/// <summary>Open security alerts of the current tenant, for the admin shell badge (spec 026 US5).</summary>
public interface ISecurityAlertSummary
{
    Task<int> OpenCountAsync(CancellationToken ct = default);
}

public sealed record SecurityAlertRaised(Guid AlertId, string Kind) : IIntegrationEvent;

/// <summary>Setting names only, never values.</summary>
public sealed record SecurityPolicyChanged(int Version, IReadOnlyList<string> Settings) : IIntegrationEvent;

/// <summary>The tenant's thresholds for app alerts (spec 026 US5 scenario 5), for the modules that watch for them.</summary>
public sealed record AlertThresholds(int ApiDeniedCount, double ApiVolumeFactor);

public interface ISecurityAlertThresholds
{
    Task<AlertThresholds> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// Security posture trends for the analytics security dashboard (spec 021 section 2): alerts raised per ISO week and per
/// kind in the period (people: distinct subjects), alerts still open, and the keys of the posture checks that are weaker
/// than the recommended baseline now, out of the checks that apply. No subject is named.
/// </summary>
public sealed record SecurityTrendSummary(long Alerts, long OpenAlerts, IReadOnlyList<GroupFigure> AlertWeeks,
    IReadOnlyList<GroupFigure> AlertKinds, IReadOnlyList<string> WeakerChecks, int Checks);

/// <summary>Security trend figures for analytics; the caller decides who may see them. Current tenant only.</summary>
public interface ISecurityTrendFigures
{
    Task<SecurityTrendSummary> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
