using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 T021: renderer telemetry. Render failures and Core Web Vitals are recorded as metrics tagged with the
/// session's tenant; events outside the declared shape, such as free text, are dropped; a session is required.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PageTelemetryTests(TenantWorld world)
{
    [Fact]
    public async Task Render_failures_and_web_vitals_are_recorded_for_the_session_tenant_and_free_text_is_dropped()
    {
        var recorded = new ConcurrentBag<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "TeamBeats.Experience" && instrument.Name.StartsWith("experience.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => recorded.Add((i.Name, v, Tags(tags))));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => recorded.Add((i.Name, v, Tags(tags))));
        listener.Start();

        var t = world.A;
        var page = Guid.NewGuid();
        var response = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/telemetry", new
        {
            events = new object[]
            {
                new { name = "placement.render_failed", pageId = page, release = 3, componentRef = "reference.alpha.kpi@1", placementId = "main-1", kind = "error" },
                new { name = "page.web_vitals", pageId = page, template = "T04", layout = "L04", deviceClass = "mobile", lcp = 1800.5, inp = 120.0, cls = 0.02 },
                new { name = "placement.render_failed", componentRef = "reference.alpha.kpi@1", placementId = "Salary of Jane Doe", kind = "error" },
                new { name = "page.web_vitals", template = "T04", layout = "L04", deviceClass = "watch", lcp = 1000.0 },
                new { name = "page.web_vitals", template = "T04", layout = "L04", deviceClass = "desktop", lcp = -1.0 },
                new { name = "user.typed", template = "<script>" },
            },
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());

        listener.RecordObservableInstruments();
        var tenant = t.Id.ToString();
        var failure = Assert.Single(recorded, r => r.Instrument == "experience.placement.render_failed" && (string?)r.Tags["tenant_id"] == tenant);
        Assert.Equal("reference.alpha.kpi@1", failure.Tags["component"]);
        Assert.Equal("error", failure.Tags["kind"]);
        var lcp = Assert.Single(recorded, r => r.Instrument == "experience.web_vitals.lcp" && (string?)r.Tags["tenant_id"] == tenant);
        Assert.Equal(1800.5, lcp.Value);
        Assert.Equal("mobile", lcp.Tags["device_class"]);
        Assert.DoesNotContain(recorded, r => r.Tags.Values.Any(v => v is string s && (s.Contains("Jane", StringComparison.Ordinal) || s.Contains('<'))));
        Assert.Equal(4, recorded.Where(r => r.Instrument == "experience.telemetry.dropped" && (string?)r.Tags["tenant_id"] == tenant).Sum(r => r.Value));
    }

    [Fact]
    public async Task Telemetry_needs_a_session_and_takes_at_most_twenty_events()
    {
        var anonymous = await world.Factory.TenantClient(world.A.Key).PostAsJsonAsync("/api/v1/experience/telemetry", new { events = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var many = Enumerable.Range(0, 21).Select(_ => new { name = "page.web_vitals", template = "T01", layout = "L01", deviceClass = "desktop" });
        var tooMany = await world.A["admin"].Client.PostAsJsonAsync("/api/v1/experience/telemetry", new { events = many });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }
        return result;
    }
}
