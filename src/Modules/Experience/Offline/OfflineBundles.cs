using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Offline;

/// <summary>The tenant's offline limits as the device applies them (spec 030 section 6).</summary>
public sealed record OfflineLimits(int MaxItems, long MaxBytes, int Days);

/// <summary>
/// Everything a device keeps for reading offline (spec 030 US3), for one person and session. <see cref="Owner"/> names the
/// tenant and person the cache belongs to; <see cref="Key"/> encrypts it on the device and changes with every session, so a
/// cache from an ended session cannot be read with a new one. <see cref="SyncedAt"/> and <see cref="ExpiresAt"/> are server
/// time, so a changed device clock does not extend the cache.
/// </summary>
public sealed record OfflineBundle(string Owner, string Key, DateTimeOffset SyncedAt, DateTimeOffset ExpiresAt, OfflineLimits Limits, IReadOnlyList<OfflineItem> Items);

/// <summary>The key that derives each session's offline key (<c>Mobile:OfflineKey</c>, base64, at least 32 bytes) from the secret manager.</summary>
public sealed class OfflineKeySource
{
    public const string Setting = "Mobile:OfflineKey";

    public OfflineKeySource(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[Setting];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var bytes = Convert.FromBase64String(configured.Trim());
                Secret = bytes.Length >= 32 ? bytes : null;
            }
            catch (FormatException)
            {
                Secret = null;
            }
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            // A new secret per process: caches made before a restart are refreshed at the next sync.
            Secret = RandomNumberGenerator.GetBytes(32);
        }
    }

    public byte[]? Secret { get; }
}

/// <summary>
/// Builds the offline bundle (spec 030 US3, FR-009 to FR-011). The sources decide eligibility on the server: only targeted
/// critical announcements and mandatory policies the person may read now, classified up to Internal. The bundle keeps the
/// newest items within the tenant's item and size limits; anything withdrawn or no longer readable is absent, so the next
/// sync prunes it from the device.
/// </summary>
public sealed class OfflineBundles(
    IEnumerable<IOfflineSource> sources,
    ISecurityPolicy policies,
    IEntitlementService entitlements,
    ITenantContext context,
    IClock clock,
    OfflineKeySource keys)
{
    public async Task<OfflineBundle> BundleAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileOffline, ct);
        var user = context.RequireUser();
        var tenant = context.RequireTenant();
        if (keys.Secret is not { } secret || context.SessionId is not { } sessionId)
        {
            // No key in the secret manager, or a caller without a browser session (an API token): nothing to keep on a device.
            throw AppException.Conflict("conflict.offline.unavailable");
        }
        var policy = await policies.GetAsync(ct);
        var limits = new OfflineLimits(policy.OfflineMaxItems, policy.OfflineMaxMb * 1024L * 1024L, policy.OfflineDays);
        var now = clock.UtcNow;

        var candidates = new List<OfflineItem>();
        if (limits.MaxItems > 0)
        {
            foreach (var source in sources)
            {
                candidates.AddRange(await source.OfflineItemsAsync(limits.MaxItems, ct));
            }
        }
        var items = new List<OfflineItem>();
        long bytes = 0;
        foreach (var item in candidates.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id))
        {
            if (items.Count >= limits.MaxItems)
            {
                break;
            }
            var size = JsonSerializer.SerializeToUtf8Bytes(item, JsonDefaults.Options).LongLength;
            if (bytes + size > limits.MaxBytes)
            {
                continue;
            }
            bytes += size;
            items.Add(item);
        }

        var key = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"offline:{tenant:N}:{user:N}:{sessionId:N}"));
        return new OfflineBundle($"{tenant}:{user}", Base64Url(key), now, now.AddDays(limits.Days), limits, items);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
