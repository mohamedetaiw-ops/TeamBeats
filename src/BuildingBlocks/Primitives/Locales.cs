namespace TeamBeats.Primitives;

/// <summary>Product-supported locales (spec 007 FR-001) and their direction (FR-002).</summary>
public static class Locales
{
    public static readonly IReadOnlyList<string> Supported = ["ar", "en", "fr", "ur", "hi"];

    /// <summary>Locales whose catalogues must be complete (spec 007 open question 1: ar and en in R1).</summary>
    public static readonly IReadOnlyList<string> LaunchComplete = ["ar", "en"];

    public const string Fallback = "en";

    public static bool IsSupported(string? locale) => locale is not null && Supported.Contains(locale);

    public static bool IsRightToLeft(string locale) => locale is "ar" or "ur";
}
