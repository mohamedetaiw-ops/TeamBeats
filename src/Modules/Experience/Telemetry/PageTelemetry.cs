using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Telemetry;

/// <summary>
/// One event from the web renderer (spec 032 section 13): <c>placement.render_failed</c> with the component, placement,
/// failure kind and release, or <c>page.web_vitals</c> with the template, layout, device class and the three Core Web
/// Vitals. Events carry identifiers and numbers only, never page content or user data.
/// </summary>
public sealed record TelemetryEvent(string? Name, Guid? PageId, int? Release, string? ComponentRef, string? PlacementId, string? Kind,
    string? Template, string? Layout, string? DeviceClass, double? Lcp, double? Inp, double? Cls);

public sealed record TelemetryBatch(IReadOnlyList<TelemetryEvent>? Events);

/// <summary>
/// Accepts frontend telemetry (FR-058, FR-066) and records it as OpenTelemetry metrics tagged with the tenant from the
/// session, plus a log line per render failure with the page key and correlation ID. Anything outside the declared shape
/// is dropped and counted, never stored, so free text cannot reach telemetry.
/// </summary>
public sealed partial class PageTelemetry(ITenantContext context, ILogger<PageTelemetry> logger)
{
    public const int MaxEvents = 20;

    public const string RenderFailed = "placement.render_failed";
    public const string WebVitals = "page.web_vitals";

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "error", "missing", "undeclared_event", "invalid_payload",
    };

    public static readonly IReadOnlySet<string> DeviceClasses = new HashSet<string>(StringComparer.Ordinal) { "mobile", "tablet", "desktop" };

    private static readonly Meter Meter = new("TeamBeats.Experience");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("experience.placement.render_failed");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("experience.telemetry.dropped");
    private static readonly Histogram<double> LcpMs = Meter.CreateHistogram<double>("experience.web_vitals.lcp", "ms");
    private static readonly Histogram<double> InpMs = Meter.CreateHistogram<double>("experience.web_vitals.inp", "ms");
    private static readonly Histogram<double> ClsScore = Meter.CreateHistogram<double>("experience.web_vitals.cls");

    /// <returns>How many events were accepted.</returns>
    public int Record(TelemetryBatch batch)
    {
        var events = batch.Events ?? [];
        if (events.Count > MaxEvents)
        {
            throw AppException.Validation("events", "validation.telemetry.too_many");
        }
        var tenant = context.RequireTenant().ToString();
        var accepted = 0;
        foreach (var e in events)
        {
            if (e.Name == RenderFailed && e.ComponentRef is { } component && ComponentRef().IsMatch(component)
                && e.PlacementId is { } placement && Identifier().IsMatch(placement) && e.Kind is { } kind && Kinds.Contains(kind))
            {
                Failures.Add(1, new("tenant_id", tenant), new("component", component), new("kind", kind));
                logger.LogWarning("Placement {Placement} of {Component} failed with {Kind} on page {Page} release {Release}, correlation {Correlation}",
                    placement, component, kind, e.PageId, e.Release, Correlation.Id);
                accepted++;
            }
            else if (e.Name == WebVitals && e.Template is { } template && Identifier().IsMatch(template) && e.Layout is { } layout
                && Identifier().IsMatch(layout) && e.DeviceClass is { } device && DeviceClasses.Contains(device)
                && InRange(e.Lcp, 600_000) && InRange(e.Inp, 600_000) && InRange(e.Cls, 100))
            {
                var tags = new TagList { { "tenant_id", tenant }, { "template", template }, { "layout", layout }, { "device_class", device } };
                if (e.Lcp is { } lcp)
                {
                    LcpMs.Record(lcp, tags);
                }
                if (e.Inp is { } inp)
                {
                    InpMs.Record(inp, tags);
                }
                if (e.Cls is { } cls)
                {
                    ClsScore.Record(cls, tags);
                }
                accepted++;
            }
            else
            {
                Dropped.Add(1, new KeyValuePair<string, object?>("tenant_id", tenant));
            }
        }
        return accepted;
    }

    private static bool InRange(double? value, double max) => value is null || (value >= 0 && value <= max && double.IsFinite(value.Value));

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9_]*)+@\d{1,3}$")]
    private static partial Regex ComponentRef();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex Identifier();
}
