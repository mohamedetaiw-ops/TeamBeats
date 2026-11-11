using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Webhooks;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 FR-022 (T021): the KPI feed contract. A signed inbound webhook with the target <c>kpi</c> maps its body to
/// metric, value, unit, period, location and source time; one reading per delivery; a later measurement of the same
/// period replaces the value and an older one is ignored; wrong readings are refused; readings stay in their tenant
/// and need the package.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class KpiFeedTests(TenantWorld world)
{
    private const string Base = "/api/v1/integrations/pack-connections";
    private const string Secret = "fictional-kpi-value";

    private static readonly Dictionary<string, string> Fields = new()
    {
        ["metric"] = "$.kpi.name", ["value"] = "$.kpi.value", ["unit"] = "$.kpi.unit", ["period_start"] = "$.period.from", ["period_end"] = "$.period.to",
        ["source_time"] = "$.measuredAt", ["location"] = "$.site",
    };

    private async Task<(TestTenant T, Guid Connection)> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("kpi-" + Guid.NewGuid().ToString("N")[..8], package);
        var reference = SecretReferences.For(t.Id, "kpi-hook");
        world.Factory.Secrets.Set(reference, Secret);
        var created = await t["admin"].Client.PostAsJsonAsync(Base, new
        {
            kind = "webhook",
            displayName = "Plant KPIs",
            settings = new { auth = new { type = "hmac_sha256", secretRef = reference }, inbound = new { target = "kpi", name = "plant.kpis", fields = Fields } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return (t, id);
    }

    private Task<HttpResponseMessage> DeliverAsync(TestTenant t, Guid connection, object body, string deliveryId)
    {
        var json = JsonSerializer.Serialize(body);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, InboundWebhooks.PathFor(connection)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryHeader, deliveryId);
        request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, WebhookSignature.Compute(Secret, ts, json));
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private static object Reading(object value, string measuredAt, string site = "RUH-01", string from = "2026-11-10", string to = "2026-11-10") => new
    {
        kpi = new { name = "units_produced", value, unit = "units" },
        period = new { from, to },
        measuredAt,
        site,
        ignored = "not mapped",
    };

    private Task<IReadOnlyList<KpiReading>> ReadAsync(TestTenant t, KpiQuery query) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IKpiFeed>().ReadAsync(query));

    private static async Task AcceptedAsync(Task<HttpResponseMessage> delivery)
    {
        var response = await delivery;
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_signed_reading_is_stored_once_per_period_and_a_later_measurement_replaces_it()
    {
        var (t, c) = await SetupAsync();
        await AcceptedAsync(DeliverAsync(t, c, Reading(1250, "2026-11-10T18:00:00+03:00"), "d-1"));
        await AcceptedAsync(DeliverAsync(t, c, Reading("1310.5", "2026-11-10T21:00:00+03:00"), "d-2"));
        // An older measurement arriving late changes nothing.
        await AcceptedAsync(DeliverAsync(t, c, Reading(900, "2026-11-10T12:00:00+03:00"), "d-3"));
        await AcceptedAsync(DeliverAsync(t, c, Reading(800, "2026-11-10T09:00:00Z", site: "JED-02"), "d-4"));
        // The same delivery twice is a duplicate.
        Assert.Equal(HttpStatusCode.Conflict, (await DeliverAsync(t, c, Reading(1, "2026-11-10T09:00:00Z"), "d-4")).StatusCode);

        var all = await ReadAsync(t, new KpiQuery("units_produced", null, null, null));
        Assert.Equal(2, all.Count);
        var riyadh = Assert.Single(await ReadAsync(t, new KpiQuery(null, "RUH-01", null, null)));
        Assert.Equal((1310.5m, "units", "plant.kpis"), (riyadh.Value, riyadh.Unit, riyadh.Feed));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero), riyadh.SourceTime);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), riyadh.PeriodStart);
        Assert.Equal(3L, await world.Database.OutboxCountAsync(nameof(KpiReadingReceived), t.Id));
        Assert.Equal(0L, await world.Database.OutboxCountAsync(nameof(InboundWebhookReceived), t.Id));
        Assert.Empty(await ReadAsync(t, new KpiQuery(null, null, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), null)));
    }

    [Fact]
    public async Task Wrong_readings_and_mappings_are_refused_and_readings_stay_in_their_tenant_and_package()
    {
        var (t, c) = await SetupAsync();
        var wrong = new[]
        {
            Reading("lots", "2026-11-10T18:00:00Z"),
            Reading(1, "2026-11-10T18:00:00"),
            Reading(1, DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture)),
            Reading(1, "2026-11-10T18:00:00Z", from: "2026-11-10", to: "2026-11-09"),
            Reading(1, "2026-11-10T18:00:00Z", site: new string('x', 101)),
        };
        for (var i = 0; i < wrong.Length; i++)
        {
            var refused = await DeliverAsync(t, c, wrong[i], $"bad-{i}");
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("validation.webhook.kpi_reading", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Empty(await ReadAsync(t, new KpiQuery(null, null, null, null)));

        var reference = SecretReferences.For(t.Id, "kpi-hook");
        var missing = await t["admin"].Client.PostAsJsonAsync(Base, new
        {
            kind = "webhook",
            displayName = "Bad",
            settings = new
            {
                auth = new { type = "hmac_sha256", secretRef = reference },
                inbound = new { target = "kpi", name = "plant.kpis", fields = new Dictionary<string, string> { ["metric"] = "$.m", ["salary"] = "$.s" } },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("validation.webhook.kpi_fields", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await AcceptedAsync(DeliverAsync(t, c, Reading(5, "2026-11-10T18:00:00Z"), "ok-1"));
        var other = await world.CreateTenantAsync("kpi-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Empty(await ReadAsync(other, new KpiQuery(null, null, null, null)));
        Assert.Equal(HttpStatusCode.NotFound, (await DeliverAsync(other, c, Reading(5, "2026-11-10T19:00:00Z"), "x-1")).StatusCode);
        var workplace = await world.CreateTenantAsync("kpi-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Empty(await ReadAsync(workplace, new KpiQuery(null, null, null, null)));
    }
}
