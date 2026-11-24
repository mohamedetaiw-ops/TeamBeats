namespace TeamBeats.Modules.Experience.Themes;

/// <summary>
/// The platform high contrast colors (spec 033 R2 T017): black text on white surfaces, a black shell, and deep accents and
/// states that pass 7:1 for text and 3:1 for interface parts. In high contrast mode a color takes the brand's own
/// <c>high_contrast</c> value when a layer sets one, else this value, so every design (Beats, Atlas, Pulse, Studio) has the
/// same readable colors there while keeping its layout, type and spacing. The set covers every color token.
/// </summary>
public static class HighContrast
{
    public static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["color.surface.base"] = "#ffffff",
        ["color.surface.raised"] = "#ffffff",
        ["color.surface.sunken"] = "#f2f2f2",
        ["color.shell.background"] = "#000000",
        ["color.shell.text"] = "#ffffff",
        ["color.nav.background"] = "#ffffff",
        ["color.nav.text"] = "#000000",
        ["color.nav.active"] = "#0000a8",
        ["color.text.primary"] = "#000000",
        ["color.text.secondary"] = "#1f1f1f",
        ["color.text.inverse"] = "#ffffff",
        ["color.accent.base"] = "#0000a8",
        ["color.accent.text"] = "#ffffff",
        ["color.accent.subtle"] = "#e6e6ff",
        ["color.highlight.base"] = "#6b4000",
        ["color.link"] = "#0000c8",
        ["color.border.default"] = "#595959",
        ["color.border.strong"] = "#000000",
        ["color.state.success"] = "#005a2b",
        ["color.state.warning"] = "#6b4000",
        ["color.state.danger"] = "#a00000",
        ["color.state.info"] = "#003f99",
        ["color.focus.ring"] = "#000000",
        ["color.focus.ring_inverse"] = "#ffd400",
        ["color.tint.warm"] = "#ffffff",
        ["color.tint.warm_text"] = "#000000",
        ["color.tint.fresh"] = "#ffffff",
        ["color.tint.fresh_text"] = "#000000",
        ["color.tint.cool"] = "#ffffff",
        ["color.tint.cool_text"] = "#000000",
        ["color.chart.1"] = "#0000a8",
        ["color.chart.2"] = "#8a4b00",
        ["color.chart.3"] = "#005a2b",
        ["color.chart.4"] = "#a0004a",
        ["color.chart.5"] = "#003f99",
        ["color.chart.6"] = "#5a00a8",
        ["color.chart.7"] = "#3d4500",
        ["color.chart.8"] = "#8a2a00",
        ["color.chart.track"] = "#8c8c8c",
        ["color.surface.night"] = "#000000",
        ["color.text.on_night"] = "#ffffff",
        ["color.surface.campaign"] = "#000000",
        ["color.highlight.bright"] = "#ffd400",
    };
}
