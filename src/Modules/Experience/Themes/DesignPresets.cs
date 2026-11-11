namespace TeamBeats.Modules.Experience.Themes;

/// <summary>
/// The four TeamBeats designs (spec 033 FR-029 to FR-031, spec 036, docs/design-system/designs.md). A design is a complete
/// set of token values that sits between the platform defaults and the brand in the resolution order, so a tenant picks a
/// whole look with one choice and can still adjust any token on top of it. Beats is the platform default itself, so it adds
/// nothing. Every preset passes the contrast checks with no finding in light and dark (unit tests).
/// </summary>
public static class DesignPresets
{
    public const string Beats = "beats";
    public const string Atlas = "atlas";
    public const string Pulse = "pulse";
    public const string Studio = "studio";

    public const string PresetToken = "design.preset";

    public static readonly IReadOnlyList<string> Keys = [Beats, Atlas, Pulse, Studio];

    private static TokenValue V(string value, string? dark = null) =>
        new(value, dark is null ? null : new Dictionary<string, string> { [ThemeModes.Dark] = dark });

    private static TokenValue S(string value, string? compact) =>
        new(value, compact is null ? null : new Dictionary<string, string> { [ThemeModes.Compact] = compact });

    /// <summary>Atlas: formal and institutional. Deep green header, top navigation, square corners, borders over shadows, IBM Plex.</summary>
    private static readonly Dictionary<string, TokenValue> AtlasTokens = new()
    {
        ["layout.navigation"] = V("top"),
        ["color.surface.base"] = V("#f6f7f5", "#0e1513"),
        ["color.surface.raised"] = V("#ffffff", "#16201d"),
        ["color.surface.sunken"] = V("#eceeea", "#0a0f0d"),
        ["color.shell.background"] = V("#0b3d2e", "#0b2a21"),
        ["color.shell.text"] = V("#ffffff", "#eaf3ef"),
        ["color.nav.background"] = V("#ffffff", "#16201d"),
        ["color.nav.text"] = V("#1d2522", "#e5ece9"),
        ["color.nav.active"] = V("#0b6e4f", "#6fd3a8"),
        ["color.text.primary"] = V("#1d2522", "#eef3f1"),
        ["color.text.secondary"] = V("#4f5b57", "#b9c6c1"),
        ["color.text.inverse"] = V("#ffffff", "#0b1f18"),
        ["color.accent.base"] = V("#0b6e4f", "#6fd3a8"),
        ["color.accent.text"] = V("#ffffff", "#0b1f18"),
        ["color.accent.subtle"] = V("#e3f1eb", "#173a2e"),
        ["color.highlight.base"] = V("#8a6a1f", "#d8b45a"),
        ["color.link"] = V("#0a5c42", "#8fe0bd"),
        ["color.border.default"] = V("#d9ddd8", "#2a3632"),
        ["color.border.strong"] = V("#6b7570", "#8c9993"),
        ["color.state.success"] = V("#1a7f37", "#5fd38a"),
        ["color.state.warning"] = V("#8a5a00", "#e8b44a"),
        ["color.state.danger"] = V("#b3261e", "#f2867f"),
        ["color.state.info"] = V("#1f5fa8", "#8cb8ef"),
        ["color.focus.ring"] = V("#0b6e4f", "#d8b45a"),
        ["color.focus.ring_inverse"] = V("#d8b45a", "#d8b45a"),
        ["color.tint.warm"] = V("#f6eed9", "#3a3018"),
        ["color.tint.warm_text"] = V("#7a5b12", "#d8b45a"),
        ["color.tint.fresh"] = V("#e1f2e6", "#173a2a"),
        ["color.tint.fresh_text"] = V("#166c30", "#5fd38a"),
        ["color.tint.cool"] = V("#e2ecf7", "#1a2c42"),
        ["color.tint.cool_text"] = V("#1f5fa8", "#8cb8ef"),
        ["color.surface.night"] = V("#0b3d2e", "#12382c"),
        ["color.text.on_night"] = V("#ffffff", "#eaf3ef"),
        ["color.surface.campaign"] = V("#0b5d3b", "#0f4a31"),
        ["color.highlight.bright"] = V("#d8b45a", "#d8b45a"),
        ["font.family.latin"] = V("IBM Plex Sans"),
        ["font.family.arabic"] = V("IBM Plex Sans Arabic"),
        ["font.family.heading_latin"] = V("IBM Plex Sans"),
        ["font.family.heading_arabic"] = V("IBM Plex Sans Arabic"),
        ["font.weight.bold"] = V("600"),
        ["font.size.xl"] = S("22px", "20px"),
        ["font.size.xxl"] = S("28px", "24px"),
        ["radius.sm"] = V("2px"),
        ["radius.md"] = V("4px"),
        ["radius.lg"] = V("6px"),
        ["radius.full"] = V("4px"),
        ["shadow.sm"] = new("none"),
        ["shadow.md"] = new("0px 2px 6px 0px #1d25221a", new Dictionary<string, string> { [ThemeModes.Dark] = "0px 2px 6px 0px #00000080" }),
    };

    /// <summary>Pulse: bold and editorial. Warm canvas, dark labelled sidebar, raspberry accent, pill controls, large type, Cairo.</summary>
    private static readonly Dictionary<string, TokenValue> PulseTokens = new()
    {
        ["layout.navigation"] = V("sidebar"),
        ["color.surface.base"] = V("#fff8f3", "#140f12"),
        ["color.surface.raised"] = V("#ffffff", "#1f181c"),
        ["color.surface.sunken"] = V("#fbeee5", "#0c090b"),
        ["color.shell.background"] = V("#ffffff", "#1f181c"),
        ["color.shell.text"] = V("#1f1520", "#fbf1f4"),
        ["color.nav.background"] = V("#1f1520", "#0c090b"),
        ["color.nav.text"] = V("#f7eef2", "#f0e4ea"),
        ["color.nav.active"] = V("#ffb4a2", "#ffb4a2"),
        ["color.text.primary"] = V("#1f1520", "#fbf1f4"),
        ["color.text.secondary"] = V("#5e4f58", "#cdbcc5"),
        ["color.text.inverse"] = V("#ffffff", "#2a0a17"),
        ["color.accent.base"] = V("#c2185b", "#ff8fb8"),
        ["color.accent.text"] = V("#ffffff", "#2a0a17"),
        ["color.accent.subtle"] = V("#fde3ec", "#4a1730"),
        ["color.highlight.base"] = V("#b0461b", "#ffa070"),
        ["color.link"] = V("#a3154d", "#ff9fc3"),
        ["color.border.default"] = V("#f0dfd6", "#3a2c33"),
        ["color.border.strong"] = V("#7a6670", "#a08c96"),
        ["color.state.success"] = V("#13795b", "#4fd1a5"),
        ["color.state.warning"] = V("#8f5600", "#f5b544"),
        ["color.state.danger"] = V("#b42318", "#ff8a80"),
        ["color.state.info"] = V("#2456c9", "#9fbaff"),
        ["color.focus.ring"] = V("#e0306f", "#ffb4a2"),
        ["color.focus.ring_inverse"] = V("#c2185b", "#ffb4a2"),
        ["color.tint.warm"] = V("#ffe7dc", "#4a2418"),
        ["color.tint.warm_text"] = V("#9a3c15", "#ffa070"),
        ["color.tint.fresh"] = V("#dff4ec", "#123a2e"),
        ["color.tint.fresh_text"] = V("#13795b", "#4fd1a5"),
        ["color.tint.cool"] = V("#e5ecfd", "#1d2a50"),
        ["color.tint.cool_text"] = V("#2456c9", "#9fbaff"),
        ["color.surface.night"] = V("#1f1520", "#2a1d24"),
        ["color.text.on_night"] = V("#ffffff", "#fbf1f4"),
        ["color.surface.campaign"] = V("#a3324f", "#7a2440"),
        ["color.highlight.bright"] = V("#ffb4a2", "#ffb4a2"),
        ["font.family.latin"] = V("Cairo"),
        ["font.family.arabic"] = V("Cairo"),
        ["font.family.heading_latin"] = V("Cairo"),
        ["font.family.heading_arabic"] = V("Cairo"),
        ["font.size.xl"] = S("26px", "22px"),
        ["font.size.xxl"] = S("40px", "32px"),
        ["font.line_height.heading"] = V("1.15"),
        ["radius.sm"] = V("10px"),
        ["radius.md"] = V("18px"),
        ["radius.lg"] = V("28px"),
        ["shadow.sm"] = new("0px 2px 4px 0px #1f152014", new Dictionary<string, string> { [ThemeModes.Dark] = "0px 2px 4px 0px #00000080" }),
        ["shadow.md"] = new("0px 12px 32px 0px #1f152029", new Dictionary<string, string> { [ThemeModes.Dark] = "0px 12px 32px 0px #000000a0" }),
        ["motion.duration.normal"] = V("240ms"),
    };

    /// <summary>
    /// Studio: clean and neutral, in the manner of modern product dashboards. White and grey surfaces, one blue accent,
    /// hairline borders, very soft shadows, a light labelled sidebar, Inter. The gradient finish of its buttons is in
    /// <c>components/ui/designs.css</c>, mixed from these tokens.
    /// </summary>
    private static readonly Dictionary<string, TokenValue> StudioTokens = new()
    {
        ["layout.navigation"] = V("sidebar"),
        ["color.surface.base"] = V("#f7f7f7", "#121212"),
        ["color.surface.raised"] = V("#ffffff", "#1c1c1c"),
        ["color.surface.sunken"] = V("#ebebeb", "#0a0a0a"),
        ["color.shell.background"] = V("#ffffff", "#171717"),
        ["color.shell.text"] = V("#0a0a0a", "#fafafa"),
        ["color.nav.background"] = V("#ffffff", "#171717"),
        ["color.nav.text"] = V("#404040", "#d4d4d4"),
        ["color.nav.active"] = V("#155dfc", "#51a2ff"),
        ["color.text.primary"] = V("#0a0a0a", "#fafafa"),
        ["color.text.secondary"] = V("#525252", "#a1a1a1"),
        ["color.text.inverse"] = V("#ffffff", "#0a0a0a"),
        ["color.accent.base"] = V("#155dfc", "#51a2ff"),
        ["color.accent.text"] = V("#ffffff", "#0a1633"),
        ["color.accent.subtle"] = V("#dbeafe", "#162456"),
        ["color.highlight.base"] = V("#00786f", "#00d5be"),
        ["color.link"] = V("#1447e6", "#8ec5ff"),
        ["color.border.default"] = V("#e5e5e5", "#2e2e2e"),
        ["color.border.strong"] = V("#858585", "#8a8a8a"),
        ["color.state.success"] = V("#016630", "#05df72"),
        ["color.state.warning"] = V("#973c00", "#ffb900"),
        ["color.state.danger"] = V("#c10007", "#ff6467"),
        ["color.state.info"] = V("#1447e6", "#51a2ff"),
        ["color.focus.ring"] = V("#2b7fff", "#51a2ff"),
        ["color.focus.ring_inverse"] = V("#155dfc", "#51a2ff"),
        ["color.tint.warm"] = V("#fef3c6", "#3b2a05"),
        ["color.tint.warm_text"] = V("#973c00", "#ffd230"),
        ["color.tint.fresh"] = V("#ecfcca", "#1f2e06"),
        ["color.tint.fresh_text"] = V("#3c6300", "#9ae600"),
        ["color.tint.cool"] = V("#dbeafe", "#162456"),
        ["color.tint.cool_text"] = V("#193cb8", "#8ec5ff"),
        ["color.surface.night"] = V("#0a0a0a", "#262626"),
        ["color.text.on_night"] = V("#ffffff", "#fafafa"),
        ["color.surface.campaign"] = V("#1447e6", "#193cb8"),
        ["color.highlight.bright"] = V("#9ae600", "#9ae600"),
        ["font.family.latin"] = V("Inter"),
        ["font.family.arabic"] = V("IBM Plex Sans Arabic"),
        ["font.family.heading_latin"] = V("Inter"),
        ["font.family.heading_arabic"] = V("IBM Plex Sans Arabic"),
        ["font.weight.bold"] = V("600"),
        ["font.size.xl"] = S("22px", "20px"),
        ["font.size.xxl"] = S("30px", "26px"),
        ["radius.sm"] = V("8px"),
        ["radius.md"] = V("10px"),
        ["radius.lg"] = V("20px"),
        ["shadow.sm"] = new("0px 1px 2px 0px #0000000f", new Dictionary<string, string> { [ThemeModes.Dark] = "0px 1px 2px 0px #00000052" }),
        ["shadow.md"] = new("0px 4px 12px 0px #0000001a", new Dictionary<string, string> { [ThemeModes.Dark] = "0px 4px 12px 0px #00000066" }),
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, TokenValue>> ByKey =
        new Dictionary<string, IReadOnlyDictionary<string, TokenValue>>
        {
            [Beats] = new Dictionary<string, TokenValue>(),
            [Atlas] = AtlasTokens,
            [Pulse] = PulseTokens,
            [Studio] = StudioTokens,
        };

    /// <summary>The token values a design sets on top of the platform defaults. An unknown key gives the default design.</summary>
    public static IReadOnlyDictionary<string, TokenValue> Tokens(string key) => ByKey.TryGetValue(key, out var tokens) ? tokens : ByKey[Beats];
}
