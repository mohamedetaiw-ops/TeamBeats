using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 R3 (US6 scenario 2, FR-010) on the spec 017 KPI feed contract: a tenant admin configures KPI tiles, and a
/// location dashboard shows each tile's latest reading for the location with its source and times. A tile with a
/// permission is left out for viewers without it, the tile set is versioned and audited, the package decides, and
/// nothing crosses tenants. Readings are written straight into the feed's table so the figures are exact. All figures
/// are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class KpiTileTests(TenantWorld world)
{
    private const string Tiles = "/api/v1/analytics/kpi-tiles";
    private const string Dubai = "/api/v1/analytics/scopes/location/dubai";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A tenant with six people at the Dubai location, a tenant-wide analytics viewer and a connection the readings come through.</summary>
    private async Task<(TestTenant T, Guid Connection)> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("kpi-" + Guid.NewGuid().ToString("N")[..8], package);
        var unit = await world.CreateUnitAsync(t, "Plant", null, "department");
        for (var i = 1; i <= 6; i++)
        {
            await world.SeedUserAsync(t, "p" + i, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, "p" + i, "Plant");
        }
        await world.SeedUserAsync(t, "viewer", MembershipTypes.Employee);
        await world.AssignAsync(t, "viewer", BuiltInRoles.AnalyticsViewer, "tenant", null);
        await OkAsync(t["admin"].Client.GetAsync("/api/v1/people"));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set location_key = 'dubai' where user_id = any(@u)",
            ("u", Enumerable.Range(1, 6).Select(i => t["p" + i].Id).ToArray()));
        Guid connection = Guid.Empty;
        if (package != "workplace")
        {
            var host = $"erp-{Guid.NewGuid():N}"[..20] + ".rest.test";
            await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host }));
            connection = (await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/pack-connections", new
            {
                kind = "custom_rest",
                displayName = "Plant ERP",
                settings = new { baseUrl = $"https://{host}", auth = new { type = "none" } },
            }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        }
        _ = unit;
        return (t, connection);
    }

    private Task ReadingAsync(TestTenant t, Guid connection, string metric, string location, DateTimeOffset start, decimal value, string unit = "AED") =>
        world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.kpi_reading (tenant_id, connection_id, feed, metric, location, period_start, period_end, value, unit, source_time, received_at)
            values (@tenant, @connection, 'erp.kpi', @metric, @location, @start, @end, @value, @unit, @start, now())
            """, ("tenant", t.Id), ("connection", connection), ("metric", metric), ("location", location), ("start", start), ("end", start.AddMonths(1).AddSeconds(-1)),
            ("value", value), ("unit", unit));

    private static object Tile(string metric, string en, string ar, string? permission = null) =>
        new { metric, names = new Dictionary<string, string> { ["en"] = en, ["ar"] = ar }, permission };

    private static Dictionary<string, JsonElement> Kpis(JsonElement view) =>
        view.GetProperty("kpis").EnumerateArray().ToDictionary(k => k.GetProperty("metric").GetString()!, k => k);

    [Fact]
    public async Task A_location_dashboard_shows_each_tiles_latest_reading_with_its_source_and_hides_tiles_the_viewer_may_not_see()
    {
        var (t, connection) = await FreshAsync();
        var set = await OkAsync(t["admin"].Client.PutAsJsonAsync(Tiles, new
        {
            version = 0,
            tiles = new[] { Tile("monthly_sales", "Monthly sales", "المبيعات الشهرية"), Tile("safety_incidents", "Safety incidents", "حوادث السلامة", Permissions.HrManage), Tile("energy_use", "Energy use", "استهلاك الطاقة") },
        }));
        Assert.Equal(1, set.GetProperty("version").GetInt32());
        var month = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        await ReadingAsync(t, connection, "monthly_sales", "dubai", month.AddMonths(-1), 410000m);
        await ReadingAsync(t, connection, "monthly_sales", "dubai", month, 452000.5m);
        await ReadingAsync(t, connection, "monthly_sales", "riyadh", month, 990000m);
        await ReadingAsync(t, connection, "safety_incidents", "dubai", month, 2m, "incidents");

        var admin = Kpis(await OkAsync(t["admin"].Client.GetAsync(Dubai)));
        Assert.Equal(3, admin.Count);
        // The latest period for this location, never another location's figure.
        Assert.Equal((452000.5m, "AED", "Plant ERP"), (admin["monthly_sales"].GetProperty("value").GetDecimal(), admin["monthly_sales"].GetProperty("unit").GetString(),
            admin["monthly_sales"].GetProperty("source").GetString()));
        Assert.Equal(month, admin["monthly_sales"].GetProperty("periodStart").GetDateTimeOffset());
        Assert.NotEqual(JsonValueKind.Null, admin["monthly_sales"].GetProperty("receivedAt").ValueKind);
        Assert.Equal(2m, admin["safety_incidents"].GetProperty("value").GetDecimal());
        // A tile with no reading yet says so.
        Assert.Equal(JsonValueKind.Null, admin["energy_use"].GetProperty("value").ValueKind);

        // A tenant-wide analytics viewer without the tile's permission does not see it.
        var viewer = Kpis(await OkAsync(t["viewer"].Client.GetAsync(Dubai)));
        Assert.Equal(["energy_use", "monthly_sales"], viewer.Keys.Order(StringComparer.Ordinal));
        // The viewer reads the tile set but cannot change it.
        Assert.False((await OkAsync(t["viewer"].Client.GetAsync(Tiles))).GetProperty("canChange").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["viewer"].Client.PutAsJsonAsync(Tiles, new { version = 1, tiles = Array.Empty<object>() })).StatusCode);

        // Each change is audited.
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.AnalyticsKpiTileConfigured)));
    }

    [Fact]
    public async Task The_tile_set_is_checked_and_versioned_the_package_decides_and_nothing_crosses_tenants()
    {
        var (t, connection) = await FreshAsync();
        var bad = await t["admin"].Client.PutAsJsonAsync(Tiles, new
        {
            version = 0,
            tiles = new object[] { Tile("sales", "Sales", "المبيعات"), Tile("sales", "Sales again", ""), Tile("bad metric!", "X", "س", "no.such_permission") },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var text = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "validation.analytics.kpi_metric", "validation.analytics.kpi_name", "validation.analytics.kpi_permission" })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }
        var tooMany = Enumerable.Range(1, 13).Select(i => Tile($"m{i}", "M", "م")).ToArray();
        Assert.Contains("validation.analytics.kpi_tiles_count", await (await t["admin"].Client.PutAsJsonAsync(Tiles, new { version = 0, tiles = tooMany })).Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await OkAsync(t["admin"].Client.PutAsJsonAsync(Tiles, new { version = 0, tiles = new[] { Tile("sales", "Sales", "المبيعات") } }));
        // A change made on an old version is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PutAsJsonAsync(Tiles, new { version = 0, tiles = new[] { Tile("sales", "Sales", "المبيعات") } })).StatusCode);
        await ReadingAsync(t, connection, "sales", "dubai", DateTimeOffset.UtcNow.AddDays(-3), 10m);

        // Another tenant with the same location key sees no tiles and none of these readings.
        var (other, _) = await FreshAsync();
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Dubai))).GetProperty("kpis").EnumerateArray());
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Tiles))).GetProperty("tiles").EnumerateArray());

        // Without analytics.kpi_tiles the dashboard has no KPI tiles and the tile set is not offered.
        var (plus, _) = await FreshAsync("workplace_plus");
        Assert.Equal(JsonValueKind.Null, (await OkAsync(plus["admin"].Client.GetAsync(Dubai))).GetProperty("kpis").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await plus["admin"].Client.GetAsync(Tiles)).StatusCode);
    }
}
