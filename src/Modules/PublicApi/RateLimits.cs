using System.Collections.Concurrent;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The installation's settings of the public API limits (section 6): the limit for all apps of one tenant, and the
/// number of API instances behind the balancer, by which each instance divides every limit it counts.
/// </summary>
public sealed record PublicApiLimits(int TenantCallsPerMinute = ApiRules.TenantCallsPerMinute, int Instances = 1)
{
    public const string Section = "PublicApi:Limits";

    /// <summary>The limit that applies to a tenant: its own when it lowered it, never above the installation's.</summary>
    public int ForTenant(int? lowered) => lowered is { } value ? Math.Min(value, TenantCallsPerMinute) : TenantCallsPerMinute;

    /// <summary>This instance's share of a limit, at least one call; the headers still show the whole limit.</summary>
    public int Share(int limit) => Math.Max(1, (int)Math.Ceiling(limit / (double)Math.Max(1, Instances)));
}

/// <summary>The answer of one limit: whether the call may go on, and what the rate limit headers say.</summary>
public readonly record struct LimitResult(bool Allowed, int Limit, int Remaining, int ResetSeconds);

/// <summary>
/// Fixed one-minute windows per partition (FR-017): per app, per app and person, per tenant, and token requests per
/// app. Windows start on the minute, so every instance counts the same minute. Counters live in this instance; with
/// several instances behind a balancer each counts its share of the limit (<see cref="PublicApiLimits.Share"/>).
/// </summary>
public sealed class PublicRateLimits(IClock clock)
{
    private const int SweepAbove = 10_000;
    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);

    private sealed class Window(long minute)
    {
        public long Minute = minute;
        public int Count;
    }

    public static string App(Guid tenant, Guid app) => $"app:{tenant:N}:{app:N}";

    public static string AppUser(Guid tenant, Guid app, Guid user) => $"user:{tenant:N}:{app:N}:{user:N}";

    public static string Tenant(Guid tenant) => $"tenant:{tenant:N}";

    public static string Token(Guid tenant, string clientId) => $"token:{tenant:N}:{clientId}";

    public LimitResult Take(string partition, int limit) => Take(partition, limit, limit);

    /// <summary>Counts one call in the partition and says whether it is within this instance's share of the limit; a refused call is counted too.</summary>
    public LimitResult Take(string partition, int limit, int share)
    {
        var now = clock.UtcNow;
        var minute = now.ToUnixTimeSeconds() / 60;
        var reset = (int)(60 - now.ToUnixTimeSeconds() % 60);
        if (windows.Count > SweepAbove)
        {
            Sweep(minute);
        }
        var window = windows.GetOrAdd(partition, _ => new Window(minute));
        int count;
        lock (window)
        {
            if (window.Minute != minute)
            {
                window.Minute = minute;
                window.Count = 0;
            }
            count = ++window.Count;
        }
        // The headers speak of the whole limit; the remaining calls are estimated from this instance's count.
        var used = share >= limit ? count : (int)Math.Min(limit, (long)count * limit / Math.Max(1, share));
        return new LimitResult(count <= share, limit, Math.Max(0, limit - used), reset);
    }

    private void Sweep(long minute)
    {
        foreach (var (key, window) in windows)
        {
            if (window.Minute < minute)
            {
                windows.TryRemove(key, out _);
            }
        }
    }
}
