using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamBeats.DevFakes;

/// <summary>
/// In-memory Open-Meteo geocoding and forecast (spec 008 FR-050), so weather works in development, staging and tests
/// without calling the real service. A few fictional readings for real cities, with Arabic names when asked. Any other
/// place is not found. <see cref="Unavailable"/> makes every call fail, for the degraded and unavailable states.
/// </summary>
public sealed class FakeOpenMeteo : HttpMessageHandler
{
    public sealed record Place(string En, string Ar, string Country, string CountryAr, double Latitude, double Longitude, double Temperature, int Code, double High, double Low);

    public static readonly IReadOnlyList<Place> Places =
    [
        new("Dubai", "دبي", "United Arab Emirates", "الإمارات العربية المتحدة", 25.07725, 55.30927, 31.4, 0, 34.0, 26.5),
        new("Riyadh", "الرياض", "Saudi Arabia", "المملكة العربية السعودية", 24.68773, 46.72185, 29.2, 1, 33.1, 21.8),
        new("Cairo", "القاهرة", "Egypt", "مصر", 30.06263, 31.24967, 24.6, 3, 27.0, 17.2),
        new("London", "لندن", "United Kingdom", "المملكة المتحدة", 51.50853, -0.12574, 11.3, 61, 13.0, 8.4),
        new("Abu Dhabi", "أبوظبي", "United Arab Emirates", "الإمارات العربية المتحدة", 24.45118, 54.39696, 30.8, 1, 33.5, 25.9),
        new("Sharjah", "الشارقة", "United Arab Emirates", "الإمارات العربية المتحدة", 25.33737, 55.41206, 30.9, 0, 33.8, 25.7),
        new("Jeddah", "جدة", "Saudi Arabia", "المملكة العربية السعودية", 21.54238, 39.19797, 32.1, 2, 34.6, 26.8),
        new("Doha", "الدوحة", "Qatar", "قطر", 25.28545, 51.53096, 30.5, 0, 33.2, 25.1),
        new("Kuwait City", "مدينة الكويت", "Kuwait", "الكويت", 29.36972, 47.97833, 27.4, 1, 31.0, 20.6),
        new("Manama", "المنامة", "Bahrain", "البحرين", 26.22787, 50.58565, 29.0, 2, 31.4, 24.2),
        new("Muscat", "مسقط", "Oman", "عُمان", 23.58413, 58.40778, 31.0, 0, 33.9, 26.3),
        new("Amman", "عمّان", "Jordan", "الأردن", 31.95522, 35.94503, 19.7, 3, 23.4, 12.1),
        new("Alexandria", "الإسكندرية", "Egypt", "مصر", 31.20176, 29.91582, 23.8, 2, 25.9, 18.3),
        new("New York", "نيويورك", "United States", "الولايات المتحدة", 40.71427, -74.00597, 9.6, 3, 12.2, 5.1),
    ];

    public bool Unavailable { get; set; }

    public int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        if (Unavailable)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
        var uri = request.RequestUri!;
        var query = Query(uri);
        if (uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal))
        {
            var name = query.GetValueOrDefault("name") ?? "";
            var ar = query.GetValueOrDefault("language") == "ar";
            var hit = Places.FirstOrDefault(p => string.Equals(p.En, name, StringComparison.OrdinalIgnoreCase) || p.Ar == name);
            return Task.FromResult(hit is null
                ? Json(new { generationtime_ms = 0.1 })
                : Json(new { results = new[] { new { name = ar ? hit.Ar : hit.En, country = ar ? hit.CountryAr : hit.Country, latitude = hit.Latitude, longitude = hit.Longitude } } }));
        }
        if (uri.AbsolutePath.EndsWith("/forecast", StringComparison.Ordinal))
        {
            var lat = double.Parse(query.GetValueOrDefault("latitude") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var lon = double.Parse(query.GetValueOrDefault("longitude") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
            var hit = Places.FirstOrDefault(p => Math.Abs(p.Latitude - lat) < 0.001 && Math.Abs(p.Longitude - lon) < 0.001);
            return Task.FromResult(hit is null
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : Json(new
                {
                    current = new { temperature_2m = hit.Temperature, weather_code = hit.Code },
                    daily = new { temperature_2m_max = new[] { hit.High }, temperature_2m_min = new[] { hit.Low } },
                }));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "", StringComparer.Ordinal);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
