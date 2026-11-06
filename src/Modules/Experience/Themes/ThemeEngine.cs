using System.Globalization;
using System.Text;

namespace TeamBeats.Modules.Experience.Themes;

/// <summary>One layer of the resolution order: platform, tenant brand and overrides, then each org unit from root to target.</summary>
/// <param name="Depth">0 for the tenant, 1 for the root unit and so on. The platform layer is -1.</param>
public sealed record ThemeLayer(int Depth, Guid? ScopeOrgUnitId, string Kind, Guid? BrandId, int? BrandVersion,
    IReadOnlyDictionary<string, TokenValue> Tokens);

/// <summary>Where a value came from (FR-018): the scope and brand version that set it, or the lock that kept it.</summary>
public sealed record TokenSource(string Layer, Guid? ScopeOrgUnitId, Guid? BrandId, int? BrandVersion, Guid? LockedAt, bool LockIgnoredOverride);

public sealed record ResolvedTheme(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Variants,
    IReadOnlyDictionary<string, TokenSource> Provenance,
    IReadOnlyList<string> InvalidTokens);

public sealed record ContrastFinding(string Code, string Severity, string Token, string Against, string Mode, double Ratio, double Required);

/// <summary>
/// Pure theme logic (spec 033 FR-004, FR-009): deterministic resolution with locks winning, alias resolution with cycle
/// detection, contrast checks and CSS compilation. No storage here.
/// </summary>
public static class ThemeEngine
{
    public const string Blocking = "blocking";
    public const string Warning = "warning";

    /// <summary>Variant keys, mode then density, for example <c>dark.compact</c>.</summary>
    public static IEnumerable<(string Key, string Mode, string Density)> VariantKeys() =>
        from m in ThemeModes.Modes from d in ThemeModes.Densities select ($"{m}.{d}", m, d);

    /// <summary>
    /// Resolves the effective tokens. <paramref name="locks"/> maps a token name to the depth of the scope that locked it; a
    /// layer deeper than the lock cannot change the token, even if older data holds an override (spec 033 resolver).
    /// Values that fail their type from older data are skipped and reported, so the inherited value stays (spec 032 FR-066).
    /// </summary>
    public static ResolvedTheme Resolve(IReadOnlyList<ThemeLayer> layers, IReadOnlyDictionary<string, (int Depth, Guid? Scope)> locks)
    {
        var raw = new Dictionary<string, Dictionary<string, string>>();
        var provenance = new Dictionary<string, TokenSource>();
        var invalid = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in TokenSchema.All)
        {
            raw[token.Name] = VariantKeys().ToDictionary(v => v.Key, v => PlatformValue(token, v.Mode, v.Density));
            provenance[token.Name] = new TokenSource("platform", null, null, null, null, false);
        }

        foreach (var layer in layers.OrderBy(l => l.Depth).ThenBy(l => l.Kind == "brand" ? 0 : 1))
        {
            foreach (var (name, value) in layer.Tokens)
            {
                if (!TokenSchema.ByName.TryGetValue(name, out var token))
                {
                    invalid.Add(name);
                    continue;
                }
                if (locks.TryGetValue(name, out var lockAt) && layer.Depth > lockAt.Depth)
                {
                    provenance[name] = provenance[name] with { LockedAt = lockAt.Scope, LockIgnoredOverride = true };
                    continue;
                }
                var values = new Dictionary<string, string>();
                var ok = true;
                foreach (var (key, mode, density) in VariantKeys())
                {
                    var v = Pick(value, mode, density);
                    if (v is null || TokenSchema.Check(token, v) is not null)
                    {
                        ok = false;
                        break;
                    }
                    values[key] = v;
                }
                if (!ok)
                {
                    invalid.Add(name);
                    continue;
                }
                raw[name] = values;
                provenance[name] = new TokenSource(layer.Kind, layer.ScopeOrgUnitId, layer.BrandId, layer.BrandVersion,
                    locks.TryGetValue(name, out var l) ? l.Scope : null, false);
            }
        }

        var variants = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var (key, mode, density) in VariantKeys())
        {
            var resolved = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in TokenSchema.All)
            {
                var value = Dealias(token.Name, key, raw, []);
                if (value is null)
                {
                    // A cycle from older data: the platform value keeps the page usable.
                    invalid.Add(token.Name);
                    value = Dealias(token.Name, key, PlatformOnly(), []) ?? PlatformValue(token, mode, density);
                }
                resolved[token.Name] = value;
            }
            variants[key] = resolved;
        }
        return new ResolvedTheme(variants, provenance, invalid.ToList());
    }

    /// <summary>Alias cycles in a candidate token set, by token name. Used at save time to refuse them.</summary>
    public static IReadOnlyList<string> AliasCycles(IReadOnlyList<ThemeLayer> layers)
    {
        var raw = new Dictionary<string, Dictionary<string, string>>();
        foreach (var token in TokenSchema.All)
        {
            raw[token.Name] = VariantKeys().ToDictionary(v => v.Key, v => PlatformValue(token, v.Mode, v.Density));
        }
        foreach (var layer in layers.OrderBy(l => l.Depth))
        {
            foreach (var (name, value) in layer.Tokens.Where(t => raw.ContainsKey(t.Key)))
            {
                raw[name] = VariantKeys().ToDictionary(v => v.Key, v => Pick(value, v.Mode, v.Density) ?? raw[name][v.Key]);
            }
        }
        return TokenSchema.All.Where(t => VariantKeys().Any(v => Dealias(t.Name, v.Key, raw, []) is null)).Select(t => t.Name).ToList();
    }

    private static Dictionary<string, Dictionary<string, string>> PlatformOnly() =>
        TokenSchema.All.ToDictionary(t => t.Name, t => VariantKeys().ToDictionary(v => v.Key, v => PlatformValue(t, v.Mode, v.Density)));

    private static string? Dealias(string name, string variant, Dictionary<string, Dictionary<string, string>> raw, HashSet<string> seen)
    {
        if (!seen.Add(name) || !raw.TryGetValue(name, out var values))
        {
            return null;
        }
        var value = values[variant];
        return TokenSchema.IsAlias(value, out var target) ? Dealias(target, variant, raw, seen) : value;
    }

    private static string PlatformValue(TokenDefinition token, string mode, string density) =>
        (mode == ThemeModes.Dark ? token.Dark : null) ?? (density == ThemeModes.Compact ? token.Compact : null) ?? token.Light;

    private static string? Pick(TokenValue value, string mode, string density)
    {
        if (mode == ThemeModes.Dark && value.Modes?.TryGetValue(ThemeModes.Dark, out var dark) == true)
        {
            return dark;
        }
        if (density == ThemeModes.Compact && value.Modes?.TryGetValue(ThemeModes.Compact, out var compact) == true)
        {
            return compact;
        }
        return value.Value;
    }

    // Contrast pairs (FR-009). Shell and navigation pairs block by default; page accent pairs warn unless the brand is strict.
    private sealed record Pair(string Foreground, string Background, double Required, bool Shell, string Code);

    private static readonly Pair[] Pairs =
    [
        new("color.text.primary", "color.surface.base", 4.5, true, "validation.theme.contrast_text"),
        new("color.text.primary", "color.surface.raised", 4.5, true, "validation.theme.contrast_text"),
        new("color.text.secondary", "color.surface.base", 4.5, true, "validation.theme.contrast_text"),
        new("color.shell.text", "color.shell.background", 4.5, true, "validation.theme.contrast_text"),
        new("color.nav.text", "color.nav.background", 4.5, true, "validation.theme.contrast_text"),
        new("color.nav.active", "color.nav.background", 3, true, "validation.theme.contrast_ui"),
        new("color.border.strong", "color.surface.base", 3, true, "validation.theme.contrast_ui"),
        new("color.focus.ring", "color.surface.base", 3, true, "validation.theme.contrast_focus"),
        new("color.focus.ring_inverse", "color.shell.background", 3, true, "validation.theme.contrast_focus"),
        new("color.focus.ring", "color.nav.background", 3, true, "validation.theme.contrast_focus"),
        new("color.accent.text", "color.accent.base", 4.5, false, "validation.theme.contrast_text"),
        new("color.link", "color.surface.base", 4.5, false, "validation.theme.contrast_text"),
        new("color.text.inverse", "color.accent.base", 3, false, "validation.theme.contrast_large_text"),
        new("color.accent.base", "color.surface.base", 3, false, "validation.theme.contrast_ui"),
        new("color.state.danger", "color.surface.base", 3, false, "validation.theme.contrast_ui"),
        new("color.state.success", "color.surface.base", 3, false, "validation.theme.contrast_ui"),
        new("color.state.warning", "color.surface.base", 3, false, "validation.theme.contrast_ui"),
        new("color.state.info", "color.surface.base", 3, false, "validation.theme.contrast_ui"),
    ];

    public const int MinimumBodyPx = 14;

    /// <summary>Contrast, focus and minimum text size findings for every mode, at comfortable and compact density.</summary>
    public static IReadOnlyList<ContrastFinding> Check(ResolvedTheme theme, bool strict)
    {
        var findings = new List<ContrastFinding>();
        foreach (var mode in ThemeModes.Modes)
        {
            var values = theme.Variants[$"{mode}.{ThemeModes.Comfortable}"];
            foreach (var pair in Pairs)
            {
                var ratio = Ratio(values[pair.Foreground], values[pair.Background]);
                if (ratio < pair.Required)
                {
                    findings.Add(new ContrastFinding(pair.Code, pair.Shell || strict ? Blocking : Warning,
                        pair.Foreground, pair.Background, mode, Math.Round(ratio, 2), pair.Required));
                }
            }
        }
        foreach (var density in ThemeModes.Densities)
        {
            var size = theme.Variants[$"{ThemeModes.Light}.{density}"]["font.size.md"];
            var px = size.EndsWith("rem", StringComparison.Ordinal)
                ? double.Parse(size[..^3], CultureInfo.InvariantCulture) * 16
                : double.Parse(size[..^2], CultureInfo.InvariantCulture);
            if (px < MinimumBodyPx)
            {
                findings.Add(new ContrastFinding("validation.theme.text_too_small", Blocking, "font.size.md", density, ThemeModes.Light, px, MinimumBodyPx));
            }
        }
        return findings;
    }

    /// <summary>WCAG 2.2 contrast ratio between two colors.</summary>
    public static double Ratio(string a, string b)
    {
        var la = Luminance(TokenSchema.ParseColor(a)!.Value);
        var lb = Luminance(TokenSchema.ParseColor(b)!.Value);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary>
    /// Compiles one variant into CSS custom properties. Values were typed on the way in; they are written as they are, except
    /// fonts (quoted with their fallback stack) and easings (named curve to its function). The body font follows the
    /// document language, so Arabic and Urdu pages use their own stacks (FR-007).
    /// </summary>
    /// <summary>CSS custom properties of a resolved variant, by property name, as the stylesheet writes them.</summary>
    public static IReadOnlyDictionary<string, string> Properties(IReadOnlyDictionary<string, string> values)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in TokenSchema.All)
        {
            var value = values[token.Name];
            properties[token.CssName] = token.Type switch
            {
                TokenTypes.FontFamily => $"\"{value}\", {TokenSchema.FontFallbacks[token.Script!]}",
                TokenTypes.Easing => TokenSchema.Easings[value],
                _ => value,
            };
        }
        return properties;
    }

    public static string Css(IReadOnlyDictionary<string, string> values)
    {
        var css = new StringBuilder(":root{");
        var properties = Properties(values);
        foreach (var token in TokenSchema.All)
        {
            css.Append(token.CssName).Append(':').Append(properties[token.CssName]).Append(';');
        }
        css.Append("--tb-font-family-body:var(--tb-font-family-latin);}");
        css.Append(":root:lang(ar){--tb-font-family-body:var(--tb-font-family-arabic);}");
        css.Append(":root:lang(ur){--tb-font-family-body:var(--tb-font-family-urdu);}");
        css.Append(":root:lang(hi){--tb-font-family-body:var(--tb-font-family-devanagari);}");
        return css.ToString();
    }
}
