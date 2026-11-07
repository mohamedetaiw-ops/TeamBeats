using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.DevFakes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 FR-050: tool box weather through the Open-Meteo connector, against the fake provider. Only the place name
/// leaves the platform; answers are cached per tenant, and a failing provider gives the last good answer as degraded.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class WeatherTests(TenantWorld world)
{
    private FakeOpenMeteo Weather => world.Factory.Weather;

    private async Task<TestTenant> TenantAsync(string locale = "en")
    {
        var t = await world.CreateTenantAsync("wx-" + Guid.NewGuid().ToString("N")[..8], "workplace", locale);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> GetAsync(TestTenant t, string location, bool refresh = false)
    {
        var response = await t["employee"].Client.GetAsync($"/api/v1/weather?location={Uri.EscapeDataString(location)}{(refresh ? "&refresh=true" : "")}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static string State(JsonElement e) => e.GetProperty("state").GetString()!;

    [Fact]
    public async Task An_employee_gets_current_weather_and_todays_range_for_a_place()
    {
        var t = await TenantAsync();
        var answer = await GetAsync(t, " Dubai ");
        Assert.Equal("ok", State(answer));
        var report = answer.GetProperty("report");
        Assert.Equal("Dubai", report.GetProperty("place").GetProperty("name").GetString());
        Assert.Equal("United Arab Emirates", report.GetProperty("place").GetProperty("country").GetString());
        Assert.Equal(31.4, report.GetProperty("temperatureC").GetDouble());
        Assert.Equal("clear", report.GetProperty("condition").GetString());
        Assert.Equal(34.0, report.GetProperty("highC").GetDouble());
        Assert.Equal(26.5, report.GetProperty("lowC").GetDouble());
        Assert.NotEqual(JsonValueKind.Null, answer.GetProperty("asOf").ValueKind);
    }

    [Fact]
    public async Task Place_names_come_in_the_viewers_language()
    {
        var t = await TenantAsync("ar");
        var report = (await GetAsync(t, "London")).GetProperty("report");
        Assert.Equal("لندن", report.GetProperty("place").GetProperty("name").GetString());
        Assert.Equal("rain", report.GetProperty("condition").GetString());
    }

    [Fact]
    public async Task An_unknown_place_is_not_found()
    {
        var t = await TenantAsync();
        var answer = await GetAsync(t, "Atlantis-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal("not_found", State(answer));
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("report").ValueKind);
    }

    [Fact]
    public async Task Answers_are_cached_per_tenant_so_another_tenant_asks_the_provider_itself()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        await GetAsync(a, "Cairo");
        var before = Weather.Requests;
        Assert.Equal("ok", State(await GetAsync(a, "cairo")));
        Assert.Equal(before, Weather.Requests);
        Assert.Equal("ok", State(await GetAsync(b, "Cairo")));
        Assert.Equal(before + 2, Weather.Requests);
    }

    [Fact]
    public async Task A_failing_provider_gives_the_last_good_answer_as_degraded_or_unavailable_without_one()
    {
        var t = await TenantAsync();
        var first = await GetAsync(t, "Riyadh");
        Weather.Unavailable = true;
        try
        {
            var degraded = await GetAsync(t, "Riyadh", refresh: true);
            Assert.Equal("degraded", State(degraded));
            Assert.Equal(first.GetProperty("asOf").GetDateTimeOffset(), degraded.GetProperty("asOf").GetDateTimeOffset());
            Assert.Equal(29.2, degraded.GetProperty("report").GetProperty("temperatureC").GetDouble());

            var unavailable = await GetAsync(t, "Dubai", refresh: true);
            Assert.Equal("unavailable", State(unavailable));
            Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("report").ValueKind);

            // While the provider is failing, asking again waits a moment instead of calling it each time.
            var before = Weather.Requests;
            Assert.Equal("unavailable", State(await GetAsync(t, "Dubai")));
            Assert.Equal(before, Weather.Requests);
        }
        finally
        {
            Weather.Unavailable = false;
        }
        Assert.Equal("ok", State(await GetAsync(t, "Riyadh", refresh: true)));
    }

    [Fact]
    public async Task A_missing_or_too_long_place_is_refused()
    {
        var t = await TenantAsync();
        foreach (var url in new[] { "/api/v1/weather", "/api/v1/weather?location=", "/api/v1/weather?location=%20%20", $"/api/v1/weather?location={new string('x', 101)}" })
        {
            var response = await t["employee"].Client.GetAsync(url);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("validation.weather.location_length", problem.GetProperty("errors").GetProperty("location")[0].GetProperty("key").GetString());
        }
    }

    [Fact]
    public async Task Weather_needs_a_signed_in_user()
    {
        var t = await TenantAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key).GetAsync("/api/v1/weather?location=Dubai")).StatusCode);
    }
}
