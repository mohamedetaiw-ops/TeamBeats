using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// Telemetry <c>integration.graph_call</c> (spec 018 section 13): one measurement per Graph read an employee asks for,
/// with the feature, the outcome, the latency and whether Microsoft throttled any attempt. It never carries content,
/// subjects, addresses or tokens. Measurements are tagged with the session's tenant.
/// </summary>
public static class GraphTelemetry
{
    public const string MeterName = "TeamBeats.Integrations";
    public const string InstrumentName = "integration.graph_call";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Calls = Meter.CreateHistogram<double>(InstrumentName, "ms");
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>What the HTTP adapter learns during one read; shared by reference so retries deeper down can mark it.</summary>
    public sealed class Scope
    {
        public bool Throttled { get; set; }
    }

    /// <summary>Called by the adapter when Microsoft answers 429.</summary>
    public static void MarkThrottled()
    {
        if (Current.Value is { } scope)
        {
            scope.Throttled = true;
        }
    }

    /// <summary>Runs <paramref name="fetch"/> and records it, with status <c>ok</c> or the failure kind.</summary>
    public static async Task<T> MeasureAsync<T>(Guid tenantId, string feature, Func<Task<T>> fetch)
    {
        var scope = new Scope();
        var previous = Current.Value;
        Current.Value = scope;
        var started = Stopwatch.GetTimestamp();
        var status = "ok";
        try
        {
            return await fetch();
        }
        catch (GraphCallException ex)
        {
            status = ex.Kind;
            throw;
        }
        catch (Exception)
        {
            status = "error";
            throw;
        }
        finally
        {
            Current.Value = previous;
            Calls.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("tenant_id", tenantId.ToString()),
                new KeyValuePair<string, object?>("feature", feature),
                new KeyValuePair<string, object?>("status", status),
                new KeyValuePair<string, object?>("throttled", scope.Throttled));
        }
    }
}
