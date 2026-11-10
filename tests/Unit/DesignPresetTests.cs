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
}
