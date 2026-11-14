using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

/// <summary>Token value types (spec 033 plan, token model). Anything outside these types is refused, so free CSS cannot enter.</summary>
public static class TokenTypes
{
    public const string Color = "color";
    public const string Dimension = "dimension";
    public const string FontFamily = "font_family";
    public const string FontWeight = "font_weight";
    public const string Number = "number";
    public const string Duration = "duration";
    public const string Easing = "easing";
    public const string Shadow = "shadow";

    /// <summary>A keyword from a fixed list, such as the design preset or the navigation style (spec 033 FR-029).</summary>
    public const string Choice = "choice";
}

/// <summary>
/// One token value as stored and sent: <c>$value</c> applies to every mode and density, <c>$modes</c> holds the values that
/// differ (<c>dark</c> for colors and shadows, <c>compact</c> for spacing and type sizes). Close to DTCG 2025.10 so the R2
/// export is a projection.
/// </summary>
public sealed record TokenValue(
    [property: JsonPropertyName("$value")] string? Value,
    [property: JsonPropertyName("$modes")] IReadOnlyDictionary<string, string>? Modes = null);

public sealed record TokenDefinition(string Name, string Type, string Light, string? Dark = null, string? Compact = null, string? Script = null,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>CSS custom property name, <c>--tb-</c> plus the token name with dots and underscores as hyphens.</summary>
    public string CssName => "--tb-" + Name.Replace('.', '-').Replace('_', '-');

    public bool AllowsDark => Type is TokenTypes.Color or TokenTypes.Shadow;

    public bool AllowsCompact => Name.StartsWith("space.", StringComparison.Ordinal) || Name.StartsWith("font.size.", StringComparison.Ordinal);
}

public static class ThemeModes
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string Comfortable = "comfortable";
    public const string Compact = "compact";

    public static readonly IReadOnlyList<string> Modes = [Light, Dark];
    public static readonly IReadOnlyList<string> Densities = [Comfortable, Compact];
}

/// <summary>
/// The platform token schema and default values (spec 033 FR-003, FR-006, FR-007). Platform defaults are complete, so a
/// partial brand never leaves a token unset. Fonts come only from the approved list per writing system.
/// </summary>
public static partial class TokenSchema
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ApprovedFonts = new Dictionary<string, IReadOnlyList<string>>
    {
        ["latin"] = ["Readex Pro", "Alexandria", "Inter", "IBM Plex Sans", "Cairo", "Noto Sans", "Source Sans 3", "Roboto"],
        ["arabic"] = ["Readex Pro", "Alexandria", "Noto Sans Arabic", "IBM Plex Sans Arabic", "Cairo", "Tajawal"],
        ["urdu"] = ["Noto Nastaliq Urdu", "Noto Sans Arabic"],
        ["devanagari"] = ["Noto Sans Devanagari", "Hind"],
    };

    /// <summary>Fallbacks appended to every family so a missing font never leaves glyphs missing (US3 scenario 2).</summary>
    public static readonly IReadOnlyDictionary<string, string> FontFallbacks = new Dictionary<string, string>
    {
        ["latin"] = "\"Noto Sans\", system-ui, sans-serif",
        ["arabic"] = "\"Noto Sans Arabic\", \"Noto Sans\", system-ui, sans-serif",
        ["urdu"] = "\"Noto Nastaliq Urdu\", \"Noto Sans Arabic\", system-ui, serif",
        ["devanagari"] = "\"Noto Sans Devanagari\", \"Noto Sans\", system-ui, sans-serif",
    };

    public static readonly IReadOnlyDictionary<string, string> Easings = new Dictionary<string, string>
    {
        ["standard"] = "cubic-bezier(0.2, 0, 0, 1)",
        ["emphasized"] = "cubic-bezier(0.3, 0, 0, 1)",
        ["decelerate"] = "cubic-bezier(0, 0, 0, 1)",
        ["accelerate"] = "cubic-bezier(0.3, 0, 1, 1)",
        ["linear"] = "linear",
    };

    private static TokenDefinition C(string name, string light, string dark) => new(name, TokenTypes.Color, light, dark);

    private static TokenDefinition D(string name, string value, string? compact = null) => new(name, TokenTypes.Dimension, value, Compact: compact);

    private static TokenDefinition Choice(string name, params string[] choices) => new(name, TokenTypes.Choice, choices[0], Choices: choices);

    public static readonly IReadOnlyList<TokenDefinition> All =
    [
        // The design language (docs/design-system/designs.md): the preset supplies a complete look, and the navigation style
        // is the structural part of it that CSS cannot express as a value.
        Choice("design.preset", DesignPresets.Beats, DesignPresets.Atlas, DesignPresets.Pulse, DesignPresets.Studio),
        Choice("layout.navigation", "rail", "top", "sidebar"),
        C("color.surface.base", "#f7f8fb", "#0f0d24"),
        C("color.surface.raised", "#ffffff", "#1b1838"),
        C("color.surface.sunken", "#eef0f5", "#0a0918"),
        C("color.shell.background", "#ffffff", "#16133d"),
        C("color.shell.text", "#17152e", "#f5f4fb"),
        C("color.nav.background", "#ffffff", "#16133d"),
        C("color.nav.text", "#17152e", "#e6e4f5"),
        C("color.nav.active", "#4338ca", "#b9b2ff"),
        C("color.text.primary", "#17152e", "#f5f4fb"),
        C("color.text.secondary", "#55526e", "#c9c6dc"),
        C("color.text.inverse", "#ffffff", "#17152e"),
        C("color.accent.base", "#4338ca", "#b9b2ff"),
        C("color.accent.text", "#ffffff", "#17152e"),
        C("color.accent.subtle", "#eef0ff", "#2a2563"),
        C("color.highlight.base", "#8f5b00", "#f4b740"),
        C("color.link", "#4338ca", "#c9c3ff"),
        C("color.border.default", "#e6e8f0", "#2e2a52"),
        C("color.border.strong", "#6e6a89", "#9a96b8"),
        C("color.state.success", "#157347", "#5ad19a"),
        C("color.state.warning", "#8f5b00", "#f4b740"),
        C("color.state.danger", "#b42318", "#f97066"),
        C("color.state.info", "#2f5bd3", "#9db7ff"),
        C("color.focus.ring", "#4338ca", "#f4b740"),
        C("color.focus.ring_inverse", "#4338ca", "#f4b740"),
        // Decorative tints (docs/design-system/foundations/colour.md): tinted tiles that tell categories apart, never state.
        C("color.tint.warm", "#fdf0d5", "#3d2e0f"),
        C("color.tint.warm_text", "#8f5b00", "#f4b740"),
        C("color.tint.fresh", "#e3f4ea", "#14352a"),
        C("color.tint.fresh_text", "#157347", "#5ad19a"),
        C("color.tint.cool", "#e6edfc", "#1c2a52"),
        C("color.tint.cool_text", "#2f5bd3", "#9db7ff"),
        // Chart series (spec 036 DLS-040): eight categorical colors, each at least 3:1 on the surfaces, and the empty track.
        C("color.chart.1", "#3b2fc9", "#b9b2ff"),
        C("color.chart.2", "#b36b00", "#f4b740"),
        C("color.chart.3", "#0e7c5f", "#4fd1a5"),
        C("color.chart.4", "#c2255c", "#ff8fb1"),
        C("color.chart.5", "#2f6fd3", "#8cb8ff"),
        C("color.chart.6", "#7a45d6", "#c9a7ff"),
        C("color.chart.7", "#5f6b0b", "#c4d45a"),
        C("color.chart.8", "#b4441c", "#ff9a70"),
        C("color.chart.track", "#e9e7f3", "#2e2a52"),
        // The night surface of AI and personal summaries, dark in both modes, with its text and bright accent.
        C("color.surface.night", "#16133d", "#1f1a52"),
        C("color.text.on_night", "#ffffff", "#f5f4fb"),
        C("color.surface.campaign", "#146c43", "#14532d"),
        C("color.highlight.bright", "#f4b740", "#f4b740"),
        new("font.family.latin", TokenTypes.FontFamily, "Readex Pro", Script: "latin"),
        new("font.family.arabic", TokenTypes.FontFamily, "Readex Pro", Script: "arabic"),
        new("font.family.heading_latin", TokenTypes.FontFamily, "Alexandria", Script: "latin"),
        new("font.family.heading_arabic", TokenTypes.FontFamily, "Alexandria", Script: "arabic"),
        new("font.family.urdu", TokenTypes.FontFamily, "Noto Nastaliq Urdu", Script: "urdu"),
        new("font.family.devanagari", TokenTypes.FontFamily, "Noto Sans Devanagari", Script: "devanagari"),
        D("font.size.xs", "12px", "12px"),
        D("font.size.sm", "14px", "13px"),
        D("font.size.md", "16px", "14px"),
        D("font.size.lg", "18px", "16px"),
        D("font.size.xl", "24px", "20px"),
        D("font.size.xxl", "32px", "28px"),
        new("font.weight.regular", TokenTypes.FontWeight, "400"),
        new("font.weight.medium", TokenTypes.FontWeight, "500"),
        new("font.weight.semibold", TokenTypes.FontWeight, "600"),
        new("font.weight.bold", TokenTypes.FontWeight, "700"),
        new("font.line_height.body", TokenTypes.Number, "1.5"),
        new("font.line_height.heading", TokenTypes.Number, "1.25"),
        D("space.xs", "4px", "2px"),
        D("space.sm", "8px", "4px"),
        D("space.md", "16px", "12px"),
        D("space.lg", "24px", "16px"),
        D("space.xl", "32px", "24px"),
        D("space.xxl", "48px", "32px"),
        D("radius.sm", "8px"),
        D("radius.md", "12px"),
        D("radius.lg", "16px"),
        D("radius.full", "999px"),
        D("border.width.thin", "1px"),
        D("border.width.thick", "2px"),
        D("border.width.focus", "2px"),
        new("shadow.sm", TokenTypes.Shadow, "0px 1px 2px 0px #17152e14", "0px 1px 2px 0px #00000066"),
        new("shadow.md", TokenTypes.Shadow, "0px 8px 24px 0px #17152e1f", "0px 8px 24px 0px #00000080"),
        new("motion.duration.fast", TokenTypes.Duration, "100ms"),
        new("motion.duration.normal", TokenTypes.Duration, "200ms"),
        new("motion.duration.slow", TokenTypes.Duration, "300ms"),
        new("motion.easing.standard", TokenTypes.Easing, "standard"),
        new("motion.easing.emphasized", TokenTypes.Easing, "emphasized"),
        new("state.hover.opacity", TokenTypes.Number, "0.08"),
        new("state.pressed.opacity", TokenTypes.Number, "0.12"),
        new("state.disabled.opacity", TokenTypes.Number, "0.38"),
    ];

    public static readonly IReadOnlyDictionary<string, TokenDefinition> ByName = All.ToDictionary(t => t.Name);

    /// <summary>Shell settings that can be locked besides tokens (FR-019).</summary>
    public static readonly IReadOnlyList<string> ShellSettings =
        ["shell_layout", "navigation", "legal_footer", "security_banner", "accessibility_minimums", "sign_in_presentation"];

    /// <summary>Default lock set for new tenants (spec 033 section 6).</summary>
    public static readonly IReadOnlyList<string> DefaultLocks = ["legal_footer", "security_banner", "accessibility_minimums", "sign_in_presentation"];

    /// <summary>Security and accessibility requirements cannot be unlocked by any tenant setting (section 6).</summary>
    public static readonly IReadOnlyList<string> PermanentLocks = ["security_banner", "accessibility_minimums"];

    public static bool IsLockable(string setting) => ByName.ContainsKey(setting) || ShellSettings.Contains(setting);

    /// <summary>Checks one raw value against the token's type. Returns a resource key, or null when valid.</summary>
    public static string? Check(TokenDefinition token, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
        {
            return "validation.theme.token_value_invalid";
        }
        if (AliasPattern().Match(value) is { Success: true } alias)
        {
            return ByName.TryGetValue(alias.Groups[1].Value, out var target) && target.Type == token.Type
                ? null
                : "validation.theme.alias_invalid";
        }
        return token.Type switch
        {
            TokenTypes.Color => ParseColor(value) is null ? "validation.theme.color_invalid" : null,
            TokenTypes.Dimension => DimensionOk(value) ? null : "validation.theme.dimension_invalid",
            TokenTypes.FontFamily => ApprovedFonts[token.Script!].Contains(value) ? null : "validation.theme.font_not_approved",
            TokenTypes.FontWeight => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w is >= 100 and <= 900 && w % 100 == 0
                ? null : "validation.theme.number_invalid",
            TokenTypes.Number => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 10
                ? null : "validation.theme.number_invalid",
            TokenTypes.Duration => DurationPattern().Match(value) is { Success: true } d && int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture) <= 2000
                ? null : "validation.theme.duration_invalid",
            TokenTypes.Easing => Easings.ContainsKey(value) ? null : "validation.theme.easing_invalid",
            TokenTypes.Shadow => ShadowOk(value) ? null : "validation.theme.shadow_invalid",
            TokenTypes.Choice => token.Choices!.Contains(value) ? null : "validation.theme.choice_invalid",
            _ => "validation.theme.token_value_invalid",
        };
    }

    /// <summary>Validates a set of token values from a brand or an override. Errors are keyed by token name.</summary>
    public static void Validate(IReadOnlyDictionary<string, TokenValue>? tokens, ValidationErrors errors, string prefix = "tokens")
    {
        if (tokens is null)
        {
            return;
        }
        if (tokens.Count > All.Count)
        {
            errors.Add(prefix, "validation.theme.too_many_tokens");
            return;
        }
        foreach (var (name, value) in tokens)
        {
            var field = $"{prefix}.{name}";
            if (!ByName.TryGetValue(name, out var token))
            {
                errors.Add(field, "validation.theme.token_unknown");
                continue;
            }
            var valueError = value is null ? "validation.theme.token_value_invalid" : Check(token, value.Value);
            if (valueError is not null)
            {
                errors.Add(field, valueError);
                continue;
            }
            foreach (var (mode, modeValue) in value!.Modes ?? new Dictionary<string, string>())
            {
                var allowed = (mode == ThemeModes.Dark && token.AllowsDark) || (mode == ThemeModes.Compact && token.AllowsCompact);
                if (!allowed)
                {
                    errors.Add($"{field}.{mode}", "validation.theme.mode_not_allowed");
                }
                else if (Check(token, modeValue) is { } modeError)
                {
                    errors.Add($"{field}.{mode}", modeError);
                }
            }
        }
    }

    public static bool IsAlias(string value, out string target)
    {
        var m = AliasPattern().Match(value);
        target = m.Success ? m.Groups[1].Value : "";
        return m.Success;
    }

    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c> (shadows only use alpha) or <c>rgb(r, g, b)</c>.</summary>
    public static (int R, int G, int B)? ParseColor(string value)
    {
        var hex = HexColor().Match(value);
        if (hex.Success)
        {
            var h = hex.Groups[1].Value;
            if (h.Length == 3)
            {
                h = string.Concat(h.Select(c => $"{c}{c}"));
            }
            return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16));
        }
        var rgb = RgbColor().Match(value);
        if (rgb.Success)
        {
            var parts = new[] { rgb.Groups[1], rgb.Groups[2], rgb.Groups[3] }.Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
            return parts.All(p => p <= 255) ? (parts[0], parts[1], parts[2]) : null;
        }
        return null;
    }

    private static bool DimensionOk(string value)
    {
        var m = DimensionPattern().Match(value);
        if (!m.Success)
        {
            return false;
        }
        var n = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value == "px" ? n <= 999 : n <= 64;
    }

    private static bool ShadowOk(string value)
    {
        if (value == "none")
        {
            return true;
        }
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5 && parts[..4].All(p => DimensionPattern().IsMatch(p) || SignedPx().IsMatch(p))
            && HexAlpha().IsMatch(parts[4]);
    }

    [GeneratedRegex(@"^\{([a-z0-9_.]+)\}$")]
    private static partial Regex AliasPattern();

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();

    [GeneratedRegex("^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")]
    private static partial Regex HexAlpha();

    [GeneratedRegex(@"^rgb\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$")]
    private static partial Regex RgbColor();

    [GeneratedRegex(@"^(\d{1,3}(?:\.\d{1,3})?)(px|rem)$")]
    private static partial Regex DimensionPattern();

    [GeneratedRegex(@"^-\d{1,3}px$")]
    private static partial Regex SignedPx();

    [GeneratedRegex(@"^(\d{1,4})ms$")]
    private static partial Regex DurationPattern();
}
