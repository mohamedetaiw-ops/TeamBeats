namespace TeamBeats.Localization;

/// <summary>The resolved locale for the current request or message (spec 007 FR-002: per rendered locale).</summary>
public sealed class RequestLocale
{
    public string Locale { get; set; } = Primitives.Locales.Fallback;

    public string Direction => LocaleResolver.Direction(Locale);
}
