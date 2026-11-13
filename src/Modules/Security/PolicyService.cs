using System.Net;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

public sealed record SecurityPolicyView(
    IReadOnlyList<string> IpAllow,
    IReadOnlyList<string> IpBlock,
    IReadOnlyList<string> AdminIpAllow,
    IReadOnlyDictionary<string, IReadOnlyList<string>> NetworkZones,
    bool RequireMfa,
    bool MfaClaimSeen,
    int StepUpMinutes,
    int IdleMinutes,
    int AbsoluteHours,
    int? MaxSessions,
    int RememberDeviceDays,
    bool ScopedRulesAvailable,
    string? YourAddress,
    int Version,
    int OfflineMaxItems,
    int OfflineMaxMb,
    int OfflineDays,
    IReadOnlyList<string> GuestAllowDomains,
    IReadOnlyList<string> GuestBlockDomains)
{
    public string ETag => PolicyService.ETag(Version);
}

public sealed record SecurityPolicyRequest(
    IReadOnlyList<string>? IpAllow,
    IReadOnlyList<string>? IpBlock,
    IReadOnlyList<string>? AdminIpAllow,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? NetworkZones,
    bool? RequireMfa,
    int? StepUpMinutes,
    int? IdleMinutes,
    int? AbsoluteHours,
    int? MaxSessions,
    bool? NoSessionLimit,
    int? RememberDeviceDays,
    string? Confirmation,
    int? OfflineMaxItems = null,
    int? OfflineMaxMb = null,
    int? OfflineDays = null,
    IReadOnlyList<string>? GuestAllowDomains = null,
    IReadOnlyList<string>? GuestBlockDomains = null);

/// <summary>How many members would be refused by proposed tenant rules, from their sign-ins in the last 30 days (spec 026 section 10).</summary>
public sealed record LockoutPreview(int PeopleOutside, string? YourAddress, bool YouWouldBeRefused);

/// <summary>
/// The tenant security policy (spec 026 US1 and US2): read by tenant administrators and security officers, changed by
/// tenant administrators. Every change is audited with before and after values and published as setting names. A change
/// that would refuse the administrator's own address needs that address typed back as confirmation (US1 scenario 3);
/// break-glass (spec 001) is the recovery path.
/// </summary>
public sealed partial class PolicyService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    PolicyStore store,
    ISignInHistory signIns)
{
    public const int MaxZones = 20;
    public const int MaxDomains = 200;
    public const string Provider = "entra";

    public static string ETag(int version) => $"\"v{version}\"";

    [GeneratedRegex("^[a-z0-9_]{1,40}$")]
    private static partial Regex ZoneName();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex DomainPattern();

    private string? Caller() =>
        IPAddress.TryParse(context.SourceIp, out var ip) ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : null;

    private async Task DemandReadAsync(CancellationToken ct)
    {
        foreach (var permission in new[] { Permissions.SecurityManage, Permissions.SecurityView })
        {
            if ((await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                return;
            }
        }
        throw AppException.Denied();
    }

    private async Task<bool> MfaClaimSeenAsync(CancellationToken ct) =>
        await session.ScalarAsync<bool>("select exists (select 1 from security.mfa_claim_mapping where provider = @p and seen_at is not null)", new { p = Provider }, ct);

    public async Task<SecurityPolicyView> GetAsync(CancellationToken ct)
    {
        await DemandReadAsync(ct);
        var policy = await store.ReadAsync(ct) ?? SecurityPolicySnapshot.Default;
        return await ViewAsync(policy, ct);
    }

    private async Task<SecurityPolicyView> ViewAsync(SecurityPolicySnapshot p, CancellationToken ct) =>
        new(p.IpAllow, p.IpBlock, p.AdminIpAllow, p.NetworkZones, p.RequireMfa, await MfaClaimSeenAsync(ct), p.StepUpMinutes, p.IdleMinutes,
            p.AbsoluteHours, p.MaxSessions, p.RememberDeviceDays, await entitlements.IsEnabledAsync(Capabilities.SecurityIpRulesScoped, ct), Caller(),
            Math.Max(p.Version, 1), p.OfflineMaxItems, p.OfflineMaxMb, p.OfflineDays, p.GuestAllowDomains ?? [], p.GuestBlockDomains ?? []);

    /// <summary>Guest domain rules (section 14): lower case domain names, no duplicates, at most 200.</summary>
    private static List<string> Domains(IReadOnlyList<string>? input, IReadOnlyList<string>? current, string field, ValidationErrors errors)
    {
        if (input is null)
        {
            return [.. current ?? []];
        }
        var result = new List<string>();
        if (input.Count > MaxDomains)
        {
            errors.Add(field, "validation.security.too_many");
            return result;
        }
        foreach (var (text, index) in input.Select((t, i) => (t, i)))
        {
            var domain = (text ?? "").Trim().TrimStart('@').ToLowerInvariant();
            if (domain.Length > 253 || !DomainPattern().IsMatch(domain))
            {
                errors.Add($"{field}[{index}]", "validation.security.domain");
                continue;
            }
            if (!result.Contains(domain, StringComparer.Ordinal))
            {
                result.Add(domain);
            }
        }
        return result;
    }

    private static List<string> Ranges(IReadOnlyList<string>? input, IReadOnlyList<string> current, string field, ValidationErrors errors)
    {
        if (input is null)
        {
            return [.. current];
        }
        var result = new List<string>();
        if (input.Count > NetworkRules.MaxEntries)
        {
            errors.Add(field, "validation.security.too_many");
            return result;
        }
        foreach (var (text, index) in input.Select((t, i) => (t, i)))
        {
            if (!NetworkRules.TryParseRange(text, out var range))
            {
                errors.Add($"{field}[{index}]", "validation.security.range");
                continue;
            }
            var canonical = NetworkRules.Canonical(range);
            if (!result.Contains(canonical, StringComparer.Ordinal))
            {
                result.Add(canonical);
            }
        }
        return result;
    }

    private static int Number(int? input, int current, int min, int max, string field, ValidationErrors errors)
    {
        var value = input ?? current;
        if (value < min || value > max)
        {
            errors.Add(field, "validation.security.out_of_range");
        }
        return value;
    }

    public async Task<SecurityPolicyView> UpdateAsync(string? ifMatch, SecurityPolicyRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.SecurityIpRules, ct);
        await session.ExecAsync("insert into security.policy (tenant_id) values (platform.current_tenant_id()) on conflict do nothing", null, ct);
        await session.ExecAsync("select 1 from security.policy for update", null, ct); // Serializes concurrent changes.
        var current = await store.ReadAsync(ct) ?? SecurityPolicySnapshot.Default;
        if (ifMatch is not null && ifMatch != ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.security.policy_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(current.Version) });
        }

        var errors = new ValidationErrors();
        var allow = Ranges(request.IpAllow, current.IpAllow, "ipAllow", errors);
        var block = Ranges(request.IpBlock, current.IpBlock, "ipBlock", errors);
        var admin = Ranges(request.AdminIpAllow, current.AdminIpAllow, "adminIpAllow", errors);
        var zones = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var zoneInput = request.NetworkZones ?? current.NetworkZones;
        if (zoneInput.Count > MaxZones)
        {
            errors.Add("networkZones", "validation.security.too_many");
        }
        foreach (var (name, ranges) in zoneInput)
        {
            if (!ZoneName().IsMatch(name))
            {
                errors.Add($"networkZones.{name}", "validation.security.zone_name");
                continue;
            }
            zones[name] = Ranges(ranges, [], $"networkZones.{name}", errors);
        }
        var requireMfa = request.RequireMfa ?? current.RequireMfa;
        if (requireMfa && !current.RequireMfa && !await MfaClaimSeenAsync(ct))
        {
            // Section 6: MFA can be required only after a sign-in has shown the identity provider's MFA claim.
            errors.Add("requireMfa", "validation.security.mfa_not_seen");
        }
        var stepUp = Number(request.StepUpMinutes, current.StepUpMinutes, 1, 60, "stepUpMinutes", errors);
        var idle = Number(request.IdleMinutes, current.IdleMinutes, 5, 1440, "idleMinutes", errors);
        var absolute = Number(request.AbsoluteHours, current.AbsoluteHours, 1, 72, "absoluteHours", errors);
        int? maxSessions = request.NoSessionLimit == true ? null : request.MaxSessions ?? current.MaxSessions;
        if (maxSessions is < 1 or > 20)
        {
            errors.Add("maxSessions", "validation.security.out_of_range");
        }
        var remember = Number(request.RememberDeviceDays, current.RememberDeviceDays, 0, 90, "rememberDeviceDays", errors);
        var offlineItems = Number(request.OfflineMaxItems, current.OfflineMaxItems, 0, 200, "offlineMaxItems", errors);
        var offlineMb = Number(request.OfflineMaxMb, current.OfflineMaxMb, 1, 200, "offlineMaxMb", errors);
        var offlineDays = Number(request.OfflineDays, current.OfflineDays, 1, 30, "offlineDays", errors);
        var guestAllow = Domains(request.GuestAllowDomains, current.GuestAllowDomains, "guestAllowDomains", errors);
        var guestBlock = Domains(request.GuestBlockDomains, current.GuestBlockDomains, "guestBlockDomains", errors);
        errors.ThrowIfAny();

        // Admin area and per role network rules are in Workplace Plus and above (section 9).
        if ((admin.Count > 0 && !admin.SequenceEqual(current.AdminIpAllow)) || (zones.Count > 0 && !SameZones(zones, current.NetworkZones)))
        {
            await entitlements.DemandAsync(Capabilities.SecurityIpRulesScoped, ct);
        }

        var next = new SecurityPolicySnapshot(allow, block, admin, zones, requireMfa, stepUp, idle, absolute, maxSessions, remember, current.Version + 1,
            offlineItems, offlineMb, offlineDays, guestAllow, guestBlock);
        var caller = Caller();
        var callerAddress = caller is null ? null : IPAddress.Parse(caller);
        if (NetworkRules.Refusal(next, callerAddress, adminArea: true) is not null && NetworkRules.Refusal(current, callerAddress, adminArea: true) is null
            && !string.Equals(request.Confirmation?.Trim(), caller ?? "unknown", StringComparison.Ordinal))
        {
            throw new AppException(ProblemCodes.ValidationFailed, 400, null,
                new Dictionary<string, string[]> { ["confirmation"] = ["validation.security.lockout_confirm"] },
                new Dictionary<string, object?> { ["yourAddress"] = caller });
        }

        var changed = Changed(current, next);
        if (changed.Count == 0)
        {
            return await ViewAsync(current, ct);
        }
        await session.ExecAsync(
            """
            update security.policy set ip_allow = @allow::cidr[], ip_block = @block::cidr[], admin_ip_allow = @admin::cidr[], network_zones = @zones,
              require_mfa = @mfa, step_up_minutes = @stepUp, idle_minutes = @idle, absolute_hours = @absolute, max_sessions = @maxSessions,
              remember_device_days = @remember, offline_max_items = @offlineItems, offline_max_mb = @offlineMb, offline_days = @offlineDays,
              guest_allow_domains = @guestAllow, guest_block_domains = @guestBlock, version = @version, updated_at = @now, updated_by = @actor
            """,
            new
            {
                allow = allow.ToArray(), block = block.ToArray(), admin = admin.ToArray(), zones = Jsonb.From(zones), mfa = requireMfa, stepUp, idle, absolute,
                maxSessions, remember, offlineItems, offlineMb, offlineDays, guestAllow = guestAllow.ToArray(), guestBlock = guestBlock.ToArray(),
                version = next.Version, now = clock.UtcNow, actor = context.ActorId,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityPolicyChanged, "security_policy", null,
            Changes: new { settings = changed, before = Values(current), after = Values(next) }), ct);
        if (changed.Any(c => c.StartsWith("offline", StringComparison.Ordinal)))
        {
            // Spec 030 section 11: the offline limits have their own audit entry, with the limits and the actor.
            await audit.WriteAsync(new AuditEntry(AuditActions.MobileOfflinePolicyChanged, "security_policy", null,
                Changes: new
                {
                    before = new { maxItems = current.OfflineMaxItems, maxMb = current.OfflineMaxMb, days = current.OfflineDays },
                    after = new { maxItems = next.OfflineMaxItems, maxMb = next.OfflineMaxMb, days = next.OfflineDays },
                }), ct);
        }
        await outbox.EnqueueAsync(new SecurityPolicyChanged(next.Version, changed), ct);
        var tenant = context.RequireTenant();
        session.OnCommitted(() => store.Forget(tenant));
        return await ViewAsync(next, ct);
    }

    private static bool SameZones(IReadOnlyDictionary<string, IReadOnlyList<string>> a, IReadOnlyDictionary<string, IReadOnlyList<string>> b) =>
        a.Count == b.Count && a.All(z => b.TryGetValue(z.Key, out var other) && other.SequenceEqual(z.Value));

    private static object Values(SecurityPolicySnapshot p) => new
    {
        ipAllow = p.IpAllow, ipBlock = p.IpBlock, adminIpAllow = p.AdminIpAllow, networkZones = p.NetworkZones, requireMfa = p.RequireMfa,
        stepUpMinutes = p.StepUpMinutes, idleMinutes = p.IdleMinutes, absoluteHours = p.AbsoluteHours, maxSessions = p.MaxSessions,
        rememberDeviceDays = p.RememberDeviceDays, offlineMaxItems = p.OfflineMaxItems, offlineMaxMb = p.OfflineMaxMb, offlineDays = p.OfflineDays,
        guestAllowDomains = p.GuestAllowDomains ?? [], guestBlockDomains = p.GuestBlockDomains ?? [],
    };

    private static List<string> Changed(SecurityPolicySnapshot a, SecurityPolicySnapshot b)
    {
        var result = new List<string>();
        if (!a.IpAllow.SequenceEqual(b.IpAllow)) result.Add("ipAllow");
        if (!a.IpBlock.SequenceEqual(b.IpBlock)) result.Add("ipBlock");
        if (!a.AdminIpAllow.SequenceEqual(b.AdminIpAllow)) result.Add("adminIpAllow");
        if (!SameZones(a.NetworkZones, b.NetworkZones)) result.Add("networkZones");
        if (a.RequireMfa != b.RequireMfa) result.Add("requireMfa");
        if (a.StepUpMinutes != b.StepUpMinutes) result.Add("stepUpMinutes");
        if (a.IdleMinutes != b.IdleMinutes) result.Add("idleMinutes");
        if (a.AbsoluteHours != b.AbsoluteHours) result.Add("absoluteHours");
        if (a.MaxSessions != b.MaxSessions) result.Add("maxSessions");
        if (a.RememberDeviceDays != b.RememberDeviceDays) result.Add("rememberDeviceDays");
        if (a.OfflineMaxItems != b.OfflineMaxItems) result.Add("offlineMaxItems");
        if (a.OfflineMaxMb != b.OfflineMaxMb) result.Add("offlineMaxMb");
        if (a.OfflineDays != b.OfflineDays) result.Add("offlineDays");
        if (!(a.GuestAllowDomains ?? []).SequenceEqual(b.GuestAllowDomains ?? [])) result.Add("guestAllowDomains");
        if (!(a.GuestBlockDomains ?? []).SequenceEqual(b.GuestBlockDomains ?? [])) result.Add("guestBlockDomains");
        return result;
    }

    /// <summary>Members whose sign-ins in the last 30 days came from an address the proposed tenant rules refuse.</summary>
    public async Task<LockoutPreview> PreviewAsync(string? ipAllow, string? ipBlock, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        static string[] Split(string? s) => (s ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var errors = new ValidationErrors();
        var allow = Ranges(Split(ipAllow), [], "ipAllow", errors);
        var block = Ranges(Split(ipBlock), [], "ipBlock", errors);
        errors.ThrowIfAny();
        var proposed = SecurityPolicySnapshot.Default with { IpAllow = allow, IpBlock = block };
        var addresses = await signIns.AddressesSinceAsync(clock.UtcNow.AddDays(-30), ct);
        var outside = addresses.Where(a => IPAddress.TryParse(a.Address, out var ip) && NetworkRules.Refusal(proposed, ip, false) is not null)
            .Select(a => a.UserId).Distinct().Count();
        var caller = Caller();
        return new LockoutPreview(outside, caller, NetworkRules.Refusal(proposed, caller is null ? null : IPAddress.Parse(caller), false) is not null);
    }
}
