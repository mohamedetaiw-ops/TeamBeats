using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// Retry, circuit and retention defaults of spec 017 section 6. Calls inside a person's request get 3 attempts within
/// 10 seconds; durable jobs get 5 attempts over about 30 minutes. Every delay carries 20% jitter either way.
/// </summary>
public static class RuntimePolicy
{
    public const int InlineAttempts = 3;
    public static readonly TimeSpan InlineBudget = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan[] InlineDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)];

    public const int JobAttempts = 5;
    public static readonly TimeSpan JobAttemptTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan[] JobDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(18)];

    /// <summary>A job on a disabled connection, or one outside the package, waits and is looked at again after this.</summary>
    public static readonly TimeSpan PausedRecheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RateLimitedRecheck = TimeSpan.FromSeconds(5);

    // Circuit breaker: opens after 50% failures over 20 calls in 5 minutes; half open after 2 minutes.
    public static readonly TimeSpan CircuitWindow = TimeSpan.FromMinutes(5);
    public const int CircuitMinimumCalls = 20;
    public static readonly TimeSpan CircuitHalfOpenAfter = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan DeadLetterRetention = TimeSpan.FromDays(14);
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan AttemptRetention = TimeSpan.FromDays(1);

    public static TimeSpan Jitter(TimeSpan delay) => delay * (0.8 + (Random.Shared.NextDouble() * 0.4));

    public static TimeSpan JobDelay(int attemptsSoFar) => Jitter(JobDelays[Math.Clamp(attemptsSoFar - 1, 0, JobDelays.Length - 1)]);
}

/// <summary>
/// Per-connection rate limits (spec 017 FR-005), a token bucket per connection in this process: the configured calls per
/// minute, with bursts of up to a sixth of a minute's calls. Bursts beyond it wait briefly or are retried later.
/// </summary>
public sealed class ConnectorRateLimits
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Connection), (int PerMinute, TokenBucketRateLimiter Limiter)> limiters = new();

    public RateLimiter For(Guid tenantId, Guid connectionId, int perMinute)
    {
        var entry = limiters.AddOrUpdate((tenantId, connectionId),
            _ => (perMinute, Create(perMinute)),
            (_, existing) => existing.PerMinute == perMinute ? existing : (perMinute, Create(perMinute)));
        return entry.Limiter;
    }

    private static TokenBucketRateLimiter Create(int perMinute) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(1, perMinute / 6),
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromMinutes(1) / perMinute,
        QueueLimit = 1000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });
}

/// <summary>Telemetry <c>integration.call</c> (spec 017 section 13): connection kind, action, status and latency, never content.</summary>
public static class ConnectorTelemetry
{
    public const string InstrumentName = "integration.call";
    private static readonly Meter Meter = new(Microsoft365.GraphTelemetry.MeterName);
    private static readonly Histogram<double> Calls = Meter.CreateHistogram<double>(InstrumentName, "ms");

    public static void Record(Guid tenantId, string kind, string action, string status, double milliseconds) =>
        Calls.Record(milliseconds,
            new KeyValuePair<string, object?>("tenant_id", tenantId.ToString()),
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("status", status));
}
