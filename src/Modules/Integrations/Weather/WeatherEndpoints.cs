using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Weather;

/// <summary>
/// Today's weather for one place (spec 008 FR-050), for the tool box weather component. Any signed-in employee may ask;
/// the answer names the provider's state so the component can show stale or missing data honestly.
/// </summary>
public static class WeatherEndpoints
{
    public const int MaxLocationLength = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/weather").RequireTenantUser().WithTags("Integrations: weather");

        api.MapGet("", async (string? location, bool? refresh, IWeather weather, RequestLocale locale, CancellationToken ct) =>
        {
            var place = location?.Trim() ?? "";
            var errors = new ValidationErrors();
            if (place.Length is 0 or > MaxLocationLength)
            {
                errors.Add("location", "validation.weather.location_length");
            }
            errors.ThrowIfAny();
            return Results.Ok(await weather.TodayAsync(place, locale.Locale, refresh == true, ct));
        });
    }
}
