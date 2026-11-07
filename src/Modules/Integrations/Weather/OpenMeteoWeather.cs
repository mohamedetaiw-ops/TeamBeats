using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Weather;

/// <summary>
/// Weather settings (<c>Integrations:Weather</c>). The defaults are Open-Meteo's free endpoints, which need no key. A
/// commercial plan sets its own base URLs and an <c>ApiKey</c> here; another provider means another <see cref="IWeather"/>
/// adapter, chosen by <c>Provider</c>.
/// </summary>
public sealed class WeatherOptions
{
    public const string Section = "Integrations:Weather";
    public const string OpenMeteo = "open-meteo";

    public string Provider { get; set; } = OpenMeteo;
    public string GeocodingBaseUrl { get; set; } = "https://geocoding-api.open-meteo.com/v1";
    public string ForecastBaseUrl { get; set; } = "https://api.open-meteo.com/v1";
    public string? ApiKey { get; set; }

    public static WeatherOptions From(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<WeatherOptions>() ?? new WeatherOptions();
}

/// <summary>
/// Open-Meteo adapter for the weather port (spec 008 FR-050, decision 2026-11-07). It looks the place up, then reads current
/// weather and today's range. Answers are cached per tenant, language and place for 15 minutes; the last good answer is
/// kept for 6 hours and returned as degraded when the provider fails. A refresh skips the fresh copy, for the retry button. Only the place name leaves the platform.
/// </summary>
public sealed class OpenMeteoWeather(IHttpClientFactory http, IMemoryCache cache, ITenantContext context, IClock clock, IConfiguration configuration,
    ILogger<OpenMeteoWeather> logger) : IWeather
{
    public const string ClientName = "open-meteo";
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan LastKnown = TimeSpan.FromHours(6);
    public static readonly TimeSpan FailurePause = TimeSpan.FromMinutes(1);

    private sealed record Entry(WeatherResult Result);

    public async Task<WeatherResult> TodayAsync(string location, string locale, bool refresh = false, CancellationToken ct = default)
    {
        var place = location.Trim();
        var key = $"weather:{context.RequireTenant()}:{locale}:{place.ToLowerInvariant()}";
        if (!refresh && cache.Get<Entry>(key + ":fresh") is { } fresh)
        {
            return fresh.Result;
        }
        try
        {
            var result = await FetchAsync(place, locale, ct);
            cache.Set(key + ":fresh", new Entry(result), Freshness);
            if (result.State == WeatherStates.Ok)
            {
                cache.Set(key + ":last", new Entry(result), LastKnown);
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
            && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Weather provider failed: {Kind}", ex.GetType().Name);
            var answer = cache.Get<Entry>(key + ":last") is { } last
                ? last.Result with { State = WeatherStates.Degraded }
                : new WeatherResult(WeatherStates.Unavailable, null, null);
            // A short pause before asking a failing provider again, so an outage does not multiply calls.
            cache.Set(key + ":fresh", new Entry(answer), FailurePause);
            return answer;
        }
    }

    private async Task<WeatherResult> FetchAsync(string place, string locale, CancellationToken ct)
    {
        var options = WeatherOptions.From(configuration);
        var client = http.CreateClient(ClientName);
        var key = string.IsNullOrEmpty(options.ApiKey) ? "" : "&apikey=" + Uri.EscapeDataString(options.ApiKey);
        var lang = locale == "ar" ? "ar" : "en";

        using var found = await GetJsonAsync(client,
            $"{options.GeocodingBaseUrl}/search?name={Uri.EscapeDataString(place)}&count=1&language={lang}&format=json{key}", ct);
        if (!found.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return new WeatherResult(WeatherStates.NotFound, clock.UtcNow, null);
        }
        var hit = results[0];
        var where = new WeatherPlace(hit.GetProperty("name").GetString()!, hit.TryGetProperty("country", out var c) ? c.GetString() : null,
            hit.GetProperty("latitude").GetDouble(), hit.GetProperty("longitude").GetDouble());

        var lat = where.Latitude.ToString(CultureInfo.InvariantCulture);
        var lon = where.Longitude.ToString(CultureInfo.InvariantCulture);
        using var forecast = await GetJsonAsync(client,
            $"{options.ForecastBaseUrl}/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code"
            + $"&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1{key}", ct);
        var current = forecast.RootElement.GetProperty("current");
        var daily = forecast.RootElement.TryGetProperty("daily", out var d) ? d : (JsonElement?)null;
        double? First(string name) => daily is { } dd && dd.TryGetProperty(name, out var list) && list.GetArrayLength() > 0
            && list[0].ValueKind == JsonValueKind.Number ? list[0].GetDouble() : null;
        var now = clock.UtcNow;
        var report = new WeatherReport(where, current.GetProperty("temperature_2m").GetDouble(),
            Condition(current.GetProperty("weather_code").GetInt32()), First("temperature_2m_max"), First("temperature_2m_min"), now);
        return new WeatherResult(WeatherStates.Ok, now, report);
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(body, cancellationToken: ct);
    }

    /// <summary>WMO weather interpretation codes, grouped into the conditions the component names.</summary>
    public static string Condition(int code) => code switch
    {
        0 => WeatherConditions.Clear,
        1 or 2 => WeatherConditions.PartlyCloudy,
        3 => WeatherConditions.Cloudy,
        45 or 48 => WeatherConditions.Fog,
        >= 51 and <= 57 => WeatherConditions.Drizzle,
        (>= 61 and <= 67) or (>= 80 and <= 82) => WeatherConditions.Rain,
        (>= 71 and <= 77) or 85 or 86 => WeatherConditions.Snow,
        >= 95 and <= 99 => WeatherConditions.Thunderstorm,
        _ => WeatherConditions.Unknown,
    };
}
