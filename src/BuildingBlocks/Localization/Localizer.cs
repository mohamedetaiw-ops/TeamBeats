using System.Collections.Frozen;
using System.Text.Json;
using TeamBeats.Primitives;

namespace TeamBeats.Localization;

/// <summary>
/// Locale resolution (spec 007 US1 scenario 4): user preference if the tenant enabled it, else the tenant
/// default, else <c>en</c>. The same rule is used by the web app and by the backend for Problem Details and emails.
/// </summary>
public static class LocaleResolver
{
    public static string Resolve(string? userPreference, IReadOnlyCollection<string>? tenantEnabled, string? tenantDefault)
    {
        if (userPreference is not null && tenantEnabled is not null && tenantEnabled.Contains(userPreference) && Locales.IsSupported(userPreference))
        {
            return userPreference;
        }

        if (tenantDefault is not null && Locales.IsSupported(tenantDefault))
        {
            return tenantDefault;
        }

        return Locales.Fallback;
    }

    public static string Direction(string locale) => Locales.IsRightToLeft(locale) ? "rtl" : "ltr";
}

public interface ILocalizer
{
    /// <summary>Text for a key in the locale, falling back to <c>en</c>, then to the key itself.</summary>
    string Get(string key, string locale);

    bool Has(string key, string locale);

    IReadOnlyCollection<string> Keys(string locale);
}

/// <summary>Backend resource catalogues in <c>catalogues/{locale}.json</c>; the key namespace is shared with the web app.</summary>
public sealed class JsonCatalogueLocalizer : ILocalizer
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _catalogues;

    public JsonCatalogueLocalizer()
    {
        var assembly = typeof(JsonCatalogueLocalizer).Assembly;
        var map = new Dictionary<string, FrozenDictionary<string, string>>();
        foreach (var locale in Locales.Supported)
        {
            using var stream = assembly.GetManifestResourceStream($"catalogues.{locale}.json");
            if (stream is null)
            {
                map[locale] = FrozenDictionary<string, string>.Empty;
                continue;
            }

            var doc = JsonDocument.Parse(stream);
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith('_'))
                {
                    continue;
                }
                entries[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            map[locale] = entries.ToFrozenDictionary(StringComparer.Ordinal);
        }
        _catalogues = map.ToFrozenDictionary();
    }

    public string Get(string key, string locale)
    {
        if (_catalogues.TryGetValue(locale, out var cat) && cat.TryGetValue(key, out var text))
        {
            return text;
        }
        if (_catalogues[Locales.Fallback].TryGetValue(key, out var fallback))
        {
            return fallback;
        }
        return key;
    }

    public bool Has(string key, string locale) => _catalogues.TryGetValue(locale, out var cat) && cat.ContainsKey(key);

    public IReadOnlyCollection<string> Keys(string locale) =>
        _catalogues.TryGetValue(locale, out var cat) ? cat.Keys : [];
}
