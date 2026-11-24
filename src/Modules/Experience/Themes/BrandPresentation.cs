using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

/// <summary>The brand as the shell shows it in one language: the title and the logo and lockup addresses (spec 033 R2 T018).</summary>
public sealed record BrandPresentation(string? Title, string? LogoUrl, string? LockupUrl);

/// <summary>
/// The brand's presentation slots (spec 033 FR-005, FR-028). Image slots hold an asset reference; <c>logo</c>, <c>lockup</c>,
/// <c>default_image</c> and <c>sign_in_image</c> may also be given per language as <c>logo.ar</c>. The title is text, given
/// as <c>title</c> and per language as <c>title.ar</c>. A language without its own value takes the one for every language.
/// </summary>
public static class BrandSlots
{
    public const string Title = "title";
    public const int TitleLength = 60;

    public static readonly IReadOnlyList<string> Images = ["logo", "lockup", "favicon", "default_image", "sign_in_image"];

    /// <summary>The image slots that may differ per language; the favicon stays one, as browsers cache it per site.</summary>
    public static readonly IReadOnlyList<string> PerLanguage = ["logo", "lockup", "default_image", "sign_in_image"];

    public static bool IsImage(string slot) =>
        Images.Contains(slot) || (Split(slot) is ({ } baseSlot, { } language) && PerLanguage.Contains(baseSlot) && Locales.IsSupported(language));

    public static bool IsTitle(string slot) =>
        slot == Title || (Split(slot) is ({ } baseSlot, { } language) && baseSlot == Title && Locales.IsSupported(language));

    public static bool IsValidTitle(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= TitleLength && !text.Any(char.IsControl);

    /// <summary>The value of a slot in a language: its own, else the one for every language.</summary>
    public static string? Pick(IReadOnlyDictionary<string, string> assets, string slot, string locale) =>
        assets.TryGetValue($"{slot}.{locale}", out var own) ? own : assets.TryGetValue(slot, out var all) ? all : null;

    public static BrandPresentation Present(IReadOnlyDictionary<string, string> assets, string locale) =>
        new(Pick(assets, Title, locale),
            Pick(assets, "logo", locale) is { } logo ? ThemeService.AssetUrlOf(logo) : null,
            Pick(assets, "lockup", locale) is { } lockup ? ThemeService.AssetUrlOf(lockup) : null);

    private static (string?, string?) Split(string slot)
    {
        var dot = slot.LastIndexOf('.');
        return dot <= 0 ? (null, null) : (slot[..dot], slot[(dot + 1)..]);
    }
}
