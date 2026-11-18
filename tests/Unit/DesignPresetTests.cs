using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 033 FR-029 to FR-031 and spec 036: the four designs are complete, valid, accessible and resolve in order.</summary>
public sealed class DesignPresetTests
{
    private static readonly Dictionary<string, (int Depth, Guid? Scope)> NoLocks = new();

    private static ThemeLayer Brand(int depth, Dictionary<string, TokenValue> tokens) => new(depth, null, "brand", Guid.NewGuid(), 1, tokens);

    private static Dictionary<string, TokenValue> Preset(string key) => new() { [DesignPresets.PresetToken] = new(key) };

    public static TheoryData<string> Keys() => [.. DesignPresets.Keys];

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_design_value_is_typed_and_passes_validation(string key)
    {
        var errors = new ValidationErrors();
        TokenSchema.Validate(DesignPresets.Tokens(key), errors);
        var thrown = Record.Exception(errors.ThrowIfAny);
        Assert.Null(thrown);
        Assert.DoesNotContain(DesignPresets.PresetToken, DesignPresets.Tokens(key).Keys);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Every_design_passes_every_contrast_and_text_size_check_in_light_and_dark(string key)
    {
        var theme = ThemeEngine.Resolve([Brand(0, Preset(key))], NoLocks);
        Assert.Empty(theme.InvalidTokens);
        var findings = ThemeEngine.Check(theme, strict: true);
        Assert.True(findings.Count == 0, string.Join("; ", findings.Select(f => $"{f.Mode} {f.Token}/{f.Against} {f.Ratio}")));
    }

    [Fact]
    public void The_designs_are_four_distinct_looks()
    {
        var values = DesignPresets.Keys
            .Select(k => ThemeEngine.Resolve([Brand(0, Preset(k))], NoLocks).Variants["light.comfortable"])
            .ToList();
        foreach (var token in new[] { "color.accent.base", "font.family.latin", "radius.md", "color.surface.base", "color.text.primary" })
        {
            Assert.Equal(4, values.Select(v => v[token]).Distinct().Count());
        }
        Assert.Equal("beats", values[0]["design.preset"]);
        Assert.Equal("rail", values[0]["layout.navigation"]);
        Assert.Equal("top", values[1]["layout.navigation"]);
        Assert.Equal("sidebar", values[2]["layout.navigation"]);
        Assert.Equal("sidebar", values[3]["layout.navigation"]);
        Assert.Equal("studio", values[3]["design.preset"]);
    }

    /// <summary>Spec 036 DLS-110: each design declares its descriptor, and the four are the ones the spec lists.</summary>
    [Theory]
    [InlineData("beats", "comfortable", "balanced", "editorial", "tinted", "comfortable", "underline", "raised", "night")]
    [InlineData("atlas", "comfortable", "typographic", "compact", "outlined", "comfortable", "underline", "outlined", "light")]
    [InlineData("pulse", "comfortable", "image_led", "editorial", "tinted", "comfortable", "segmented", "raised", "night")]
    [InlineData("studio", "compact", "typographic", "compact", "plain", "dense", "segmented", "outlined", "light")]
    public void Every_design_declares_its_descriptor(string key, params string[] expected)
    {
        var theme = ThemeEngine.Resolve([Brand(0, Preset(key))], NoLocks);
        Assert.All(theme.Variants.Values, values => Assert.Equal(expected, DesignPresets.DescriptorTokens.Select(t => values[t]).ToArray()));
        foreach (var token in DesignPresets.DescriptorTokens)
        {
            Assert.Equal(TokenTypes.Choice, TokenSchema.ByName[token].Type);
            Assert.Contains(theme.Variants["light.comfortable"][token], TokenSchema.ByName[token].Choices!);
        }
    }

    [Fact]
    public void A_tenant_overrides_a_variant_and_a_lock_keeps_the_parent_value()
    {
        var parent = Preset(DesignPresets.Studio);
        var child = new Dictionary<string, TokenValue> { ["variant.briefing"] = new("night"), [DesignPresets.DensityToken] = new("comfortable") };
        var layers = new List<ThemeLayer> { Brand(0, parent), Brand(1, child) };
        var open = ThemeEngine.Resolve(layers, NoLocks).Variants["light.comfortable"];
        Assert.Equal("night", open["variant.briefing"]);
        Assert.Equal("comfortable", open[DesignPresets.DensityToken]);
        Assert.Equal("dense", open["variant.work_rows"]);

        var locks = new Dictionary<string, (int Depth, Guid? Scope)> { ["variant.briefing"] = (0, null) };
        var locked = ThemeEngine.Resolve(layers, locks).Variants["light.comfortable"];
        Assert.Equal("light", locked["variant.briefing"]);
        Assert.Equal("comfortable", locked[DesignPresets.DensityToken]);
    }

    [Fact]
    public void Beats_is_the_platform_default()
    {
        Assert.Empty(DesignPresets.Tokens(DesignPresets.Beats));
        var none = ThemeEngine.Resolve([], NoLocks).Variants["light.comfortable"];
        var beats = ThemeEngine.Resolve([Brand(0, Preset("beats"))], NoLocks).Variants["light.comfortable"];
        Assert.Equal(none, beats);
    }

    [Fact]
    public void Brand_values_win_over_the_design_and_provenance_names_the_design_layer()
    {
        var tokens = Preset(DesignPresets.Atlas);
        tokens["color.accent.base"] = new("#1d4ed8");
        var theme = ThemeEngine.Resolve([Brand(0, tokens)], NoLocks);
        var light = theme.Variants["light.comfortable"];
        Assert.Equal("#1d4ed8", light["color.accent.base"]);
        Assert.Equal("IBM Plex Sans", light["font.family.latin"]);
        Assert.Equal("design", theme.Provenance["font.family.latin"].Layer);
        Assert.Equal("brand", theme.Provenance["color.accent.base"].Layer);
    }

    [Fact]
    public void A_child_scope_can_pick_another_design_unless_the_parent_locked_it()
    {
        var layers = new List<ThemeLayer> { Brand(0, Preset(DesignPresets.Atlas)), Brand(1, Preset(DesignPresets.Pulse)) };
        Assert.Equal(DesignPresets.Pulse, ThemeEngine.EffectivePreset(layers, NoLocks));
        Assert.Equal("Cairo", ThemeEngine.Resolve(layers, NoLocks).Variants["light.comfortable"]["font.family.latin"]);

        var locked = new Dictionary<string, (int Depth, Guid? Scope)> { [DesignPresets.PresetToken] = (0, null) };
        Assert.Equal(DesignPresets.Atlas, ThemeEngine.EffectivePreset(layers, locked));
        var theme = ThemeEngine.Resolve(layers, locked);
        Assert.Equal("IBM Plex Sans", theme.Variants["light.comfortable"]["font.family.latin"]);
        Assert.Equal("atlas", theme.Variants["light.comfortable"]["design.preset"]);
    }

    [Theory]
    [InlineData("design.preset", "corporate")]
    [InlineData("design.preset", "url(x)")]
    [InlineData("layout.navigation", "floating")]
    [InlineData("density.default", "tiny")]
    [InlineData("imagery.style", "stock")]
    [InlineData("variant.news_lead", "hero")]
    [InlineData("variant.stat_tiles", "rings")]
    [InlineData("variant.work_rows", "cards")]
    [InlineData("variant.section_tabs", "pills")]
    [InlineData("variant.page_cards", "glass")]
    [InlineData("variant.briefing", "day")]
    public void Unknown_choices_are_refused(string token, string value)
    {
        var errors = new ValidationErrors();
        TokenSchema.Validate(new Dictionary<string, TokenValue> { [token] = new(value) }, errors);
        var e = Assert.Throws<AppException>(errors.ThrowIfAny);
        Assert.Equal("validation.theme.choice_invalid", e.Errors!["tokens." + token].Single());
    }

    [Fact]
    public void An_unknown_design_from_older_data_falls_back_to_the_default_design()
    {
        var theme = ThemeEngine.Resolve([Brand(0, Preset("retired"))], NoLocks);
        Assert.Contains(DesignPresets.PresetToken, theme.InvalidTokens);
        Assert.Equal("rail", theme.Variants["light.comfortable"]["layout.navigation"]);
    }

    [Fact]
    public void A_locked_navigation_keeps_the_locking_scopes_design_when_a_child_picks_another_design()
    {
        var layers = new List<ThemeLayer> { Brand(0, Preset(DesignPresets.Atlas)), Brand(1, Preset(DesignPresets.Pulse)) };
        var scope = Guid.NewGuid();
        var locks = new Dictionary<string, (int Depth, Guid? Scope)> { ["layout.navigation"] = (0, scope) };
        var theme = ThemeEngine.Resolve(layers, locks);
        var values = theme.Variants["light.comfortable"];
        Assert.Equal("top", values["layout.navigation"]);
        // Tokens that are not locked still follow the child's design.
        Assert.Equal("pulse", values["design.preset"]);
        Assert.Equal("Cairo", values["font.family.latin"]);
        Assert.Equal(scope, theme.Provenance["layout.navigation"].LockedAt);
        Assert.True(theme.Provenance["layout.navigation"].LockIgnoredOverride);

        // Without the lock the child's design decides.
        Assert.Equal("sidebar", ThemeEngine.Resolve(layers, NoLocks).Variants["light.comfortable"]["layout.navigation"]);
    }

    [Fact]
    public void A_lock_on_a_unit_takes_the_design_effective_at_that_unit_in_every_mode()
    {
        var layers = new List<ThemeLayer>
        {
            Brand(0, Preset(DesignPresets.Atlas)), Brand(1, Preset(DesignPresets.Studio)), Brand(2, Preset(DesignPresets.Pulse)),
        };
        var locks = new Dictionary<string, (int Depth, Guid? Scope)> { ["color.nav.active"] = (1, null) };
        var theme = ThemeEngine.Resolve(layers, locks);
        var studio = DesignPresets.Tokens(DesignPresets.Studio)["color.nav.active"];
        Assert.Equal(studio.Value, theme.Variants["light.comfortable"]["color.nav.active"]);
        Assert.Equal(studio.Modes!["dark"], theme.Variants["dark.comfortable"]["color.nav.active"]);
        Assert.Equal("Cairo", theme.Variants["light.comfortable"]["font.family.latin"]);
    }

    [Fact]
    public void A_lock_reaching_back_to_the_default_design_restores_the_platform_value()
    {
        var layers = new List<ThemeLayer> { Brand(0, new()), Brand(1, Preset(DesignPresets.Atlas)) };
        var locks = new Dictionary<string, (int Depth, Guid? Scope)> { ["layout.navigation"] = (0, null) };
        var theme = ThemeEngine.Resolve(layers, locks);
        Assert.Equal("rail", theme.Variants["light.comfortable"]["layout.navigation"]);
        Assert.Equal("platform", theme.Provenance["layout.navigation"].Layer);
    }

    [Fact]
    public void A_value_set_at_or_above_the_lock_still_wins_over_the_design()
    {
        var tenant = Preset(DesignPresets.Atlas);
        tenant["layout.navigation"] = new("rail");
        var layers = new List<ThemeLayer> { Brand(0, tenant), Brand(1, Preset(DesignPresets.Pulse)) };
        var locks = new Dictionary<string, (int Depth, Guid? Scope)> { ["layout.navigation"] = (0, null) };
        Assert.Equal("rail", ThemeEngine.Resolve(layers, locks).Variants["light.comfortable"]["layout.navigation"]);
    }

    [Theory]
    [InlineData("design.preset", "{layout.navigation}")]
    [InlineData("layout.navigation", "{design.preset}")]
    [InlineData("density.default", "{variant.work_rows}")]
    [InlineData("variant.news_lead", "{variant.stat_tiles}")]
    public void A_choice_alias_must_only_reach_values_the_receiving_token_allows(string token, string alias)
    {
        var errors = new ValidationErrors();
        TokenSchema.Validate(new Dictionary<string, TokenValue> { [token] = new(alias) }, errors);
        var e = Assert.Throws<AppException>(errors.ThrowIfAny);
        Assert.Equal("validation.theme.alias_invalid", e.Errors!["tokens." + token].Single());
    }

    [Fact]
    public void A_preset_alias_from_older_data_is_reported_and_never_resolves_to_a_navigation_value()
    {
        var theme = ThemeEngine.Resolve([Brand(0, new() { [DesignPresets.PresetToken] = new("{layout.navigation}") })], NoLocks);
        Assert.Contains(DesignPresets.PresetToken, theme.InvalidTokens);
        foreach (var (_, values) in theme.Variants)
        {
            Assert.Equal(DesignPresets.Beats, values[DesignPresets.PresetToken]);
            Assert.Equal("rail", values["layout.navigation"]);
        }
    }

    /// <summary>
    /// The current menu link (shell.css, DLS-061) is the navigation text over a tint of the navigation accent at the
    /// pressed opacity, on the navigation background, at every width including the expanded phone and tablet menu.
    /// </summary>
    [Theory]
    [MemberData(nameof(Keys))]
    public void The_current_menu_link_is_legible_in_every_design_and_mode(string key)
    {
        var theme = ThemeEngine.Resolve([Brand(0, Preset(key))], NoLocks);
        foreach (var mode in new[] { "light", "dark" })
        {
            var v = theme.Variants[$"{mode}.comfortable"];
            var alpha = double.Parse(v["state.pressed.opacity"], System.Globalization.CultureInfo.InvariantCulture);
            var tint = Blend(v["color.nav.active"], v["color.nav.background"], alpha);
            Assert.True(ThemeEngine.Ratio(v["color.nav.text"], tint) >= 4.5, $"{key} {mode} current link");
            // The phone bottom bar marks the current page with the accent on the raised surface.
            Assert.True(ThemeEngine.Ratio(v["color.accent.base"], v["color.surface.raised"]) >= 4.5, $"{key} {mode} bottom bar");
        }
    }

    private static string Blend(string top, string bottom, double alpha)
    {
        var t = TokenSchema.ParseColor(top)!.Value;
        var b = TokenSchema.ParseColor(bottom)!.Value;
        int Mix(int x, int y) => (int)Math.Round((x * alpha) + (y * (1 - alpha)));
        return $"#{Mix(t.R, b.R):x2}{Mix(t.G, b.G):x2}{Mix(t.B, b.B):x2}";
    }
}
