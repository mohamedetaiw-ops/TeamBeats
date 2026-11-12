using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>
/// Reads the tenant security policy with a short per-tenant cache (spec 026 T003): the network check runs on every
/// request and must stay under 2 ms. A change made here clears the entry at commit; other instances pick it up when
/// their entry expires.
/// </summary>
public sealed class PolicyStore(IDbSession session, TenantContextAccessor context, IMemoryCache cache) : ISecurityPolicy
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private static string Key(Guid tenant) => $"security.policy:{tenant}";

    public Task<SecurityPolicySnapshot> GetAsync(CancellationToken ct = default) => ForTenantAsync(context.RequireTenant(), ct);

    /// <summary>The policy of a tenant, read under that tenant even when the caller is not signed in yet.</summary>
    public async Task<SecurityPolicySnapshot> ForTenantAsync(Guid tenant, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(tenant), out SecurityPolicySnapshot? cached) && cached is not null)
        {
            return cached;
        }
        var previous = context.TenantId;
        context.SetTenant(tenant);
        try
        {
            var policy = await ReadAsync(ct) ?? SecurityPolicySnapshot.Default;
            cache.Set(Key(tenant), policy, CacheFor);
            return policy;
        }
        finally
        {
            context.SetTenant(previous);
        }
    }

    /// <summary>The stored row for the current tenant, or null when the tenant never saved a policy.</summary>
    public async Task<SecurityPolicySnapshot?> ReadAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select ip_allow::text[], ip_block::text[], admin_ip_allow::text[], network_zones::text, require_mfa, step_up_minutes,
                   idle_minutes, absolute_hours, max_sessions, remember_device_days, version, offline_max_items, offline_max_mb, offline_days
            from security.policy
            """, null,
            r => new SecurityPolicySnapshot(
                r.GetFieldValue<string[]>(0), r.GetFieldValue<string[]>(1), r.GetFieldValue<string[]>(2), Zones(r.GetString(3)),
                r.GetBoolean(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.GetInt32(9), r.GetInt32(10),
                r.GetInt32(11), r.GetInt32(12), r.GetInt32(13)), ct);

    public void Forget(Guid tenant) => cache.Remove(Key(tenant));

    /// <summary>The tenant's alert thresholds as stored, or null for the defaults.</summary>
    public Task<string?> AlertSettingsJsonAsync(CancellationToken ct) =>
        session.ScalarAsync<string?>("select alert_settings::text from security.policy", null, ct);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Zones(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, string[]>>(json) ?? []).ToDictionary(z => z.Key, z => (IReadOnlyList<string>)z.Value, StringComparer.Ordinal);
}
