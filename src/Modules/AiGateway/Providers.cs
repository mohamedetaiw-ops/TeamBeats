using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.AiGateway;

/// <summary>What the gateway sends to an adapter after every policy step passed. Provider DTOs stay inside the adapter.</summary>
public sealed record ProviderChat(
    string Model,
    IReadOnlyList<AiMessage> Messages,
    IReadOnlyList<AiTool>? Tools,
    JsonElement? ResponseSchema,
    int? MaxTokens,
    double? Temperature);

public sealed record ProviderReply(string Content, IReadOnlyList<AiToolCall> ToolCalls, AiUsage Usage, string FinishReason, bool Partial = false);

/// <summary>A streamed piece; the adapter ends the stream with one part that carries <see cref="Final"/>.</summary>
public sealed record ProviderStreamPart(string? Delta, ProviderReply? Final);

public sealed record ProviderEmbedding(IReadOnlyList<float[]> Vectors, AiUsage Usage);

/// <summary>A provider call failed. Retryable failures (timeouts, 429, 5xx) are tried once more before the next model.</summary>
public sealed class ProviderException(string message, bool retryable, Exception? inner = null) : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// A model provider adapter (spec 013 FR-004). Adapters live in their own projects and are the only code that knows a
/// provider's URL, SDK or wire format. The gateway calls them only after every policy step passed.
/// </summary>
public interface IModelProvider
{
    /// <summary>The catalogue's provider key, for example <c>openrouter</c>.</summary>
    string Key { get; }

    /// <summary>False when the installation cannot reach the provider by design (egress blocked, no key configured).</summary>
    bool Reachable { get; }

    Task<ProviderReply> ChatAsync(ProviderChat request, CancellationToken ct);

    IAsyncEnumerable<ProviderStreamPart> StreamAsync(ProviderChat request, CancellationToken ct);

    Task<ProviderEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct);
}

/// <summary>
/// One circuit breaker per provider (FR-015, plan): it opens after half of at least 20 calls in the last minute failed,
/// lets one trial call through after 30 seconds, and closes when that call succeeds. Opening and closing are logged at
/// error and information level for operator alerts.
/// </summary>
public sealed class ProviderCircuits(IClock clock, ILogger<ProviderCircuits> logger)
{
    public const int MinimumCalls = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(30);

    private sealed class Circuit
    {
        public readonly Queue<(DateTimeOffset At, bool Failed)> Calls = new();
        public DateTimeOffset? OpenedAt;
        public bool TrialRunning;
    }

    private readonly ConcurrentDictionary<string, Circuit> circuits = new(StringComparer.Ordinal);

    public string State(string provider)
    {
        var c = circuits.GetOrAdd(provider, _ => new Circuit());
        lock (c)
        {
            if (c.OpenedAt is null)
            {
                return "closed";
            }
            return clock.UtcNow - c.OpenedAt.Value >= OpenFor ? "half_open" : "open";
        }
    }

    /// <summary>Whether a call may go to the provider now. In half-open state only one trial call is let through.</summary>
    public bool Allow(string provider)
    {
        var c = circuits.GetOrAdd(provider, _ => new Circuit());
        lock (c)
        {
            if (c.OpenedAt is null)
            {
                return true;
            }
            if (clock.UtcNow - c.OpenedAt.Value < OpenFor || c.TrialRunning)
            {
                return false;
            }
            c.TrialRunning = true;
            return true;
        }
    }

    public void Record(string provider, bool failed)
    {
        var c = circuits.GetOrAdd(provider, _ => new Circuit());
        var now = clock.UtcNow;
        lock (c)
        {
            if (c.OpenedAt is not null)
            {
                if (!c.TrialRunning)
                {
                    return;
                }
                c.TrialRunning = false;
                if (failed)
                {
                    c.OpenedAt = now;
                    logger.LogError("AI provider circuit stays open for {Provider} after a failed trial call", provider);
                }
                else
                {
                    c.OpenedAt = null;
                    c.Calls.Clear();
                    logger.LogInformation("AI provider circuit closed for {Provider} (AiProviderCircuitClosed)", provider);
                }
                return;
            }
            c.Calls.Enqueue((now, failed));
            while (c.Calls.Count > 0 && now - c.Calls.Peek().At > Window)
            {
                c.Calls.Dequeue();
            }
            var failures = c.Calls.Count(x => x.Failed);
            if (c.Calls.Count >= MinimumCalls && failures * 2 >= c.Calls.Count)
            {
                c.OpenedAt = now;
                c.Calls.Clear();
                logger.LogError("AI provider circuit opened for {Provider}: {Failures} of the last calls failed (AiProviderCircuitOpened)", provider, failures);
            }
        }
    }

    /// <summary>Test and operator reset.</summary>
    public void Reset() => circuits.Clear();
}
