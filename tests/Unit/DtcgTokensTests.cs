using System.Text.Json.Nodes;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 033 R2 T022 (US8 scenario 2, FR-014): a DTCG 2025.10 token file keeps every value, alias and mode through a round trip.</summary>
public sealed class DtcgTokensTests
{
    /// <summary>Every platform token with its dark, high contrast and compact values, the most a brand can hold.</summary>
    private static Dictionary<string, TokenValue> Everything() => TokenSchema.All.ToDictionary(t => t.Name, t =>
    {
        var modes = new Dictionary<string, string>();
        if (t.Dark is not null)
        {
            modes["dark"] = t.Dark;
        }
        if (t.Compact is not null)
        {
            modes["compact"] = t.Compact;
        }
        return new TokenValue(t.Light, modes.Count > 0 ? modes : null);
    });

    private static Dictionary<string, TokenValue> RoundTrip(Dictionary<string, TokenValue> tokens)
    {
        var text = DtcgTokens.Export(tokens, "Acme").ToJsonString();
        var (back, problems) = DtcgTokens.Import(JsonNode.Parse(text));
        Assert.Empty(problems);
        return back;
    }

    [Fact]
    public void Every_platform_token_and_mode_survives_a_round_trip_and_stays_valid()
    {
        var tokens = Everything();
        var back = RoundTrip(tokens);
        Assert.Equal(tokens.Keys.Order(), back.Keys.Order());
        foreach (var (name, value) in tokens)
        {
            Assert.Equal(Normalize(value.Value!), back[name].Value);
            Assert.Equal(value.Modes?.ToDictionary(m => m.Key, m => Normalize(m.Value)) ?? [], back[name].Modes ?? new Dictionary<string, string>());
        }
        var errors = new ValidationErrors();
        TokenSchema.Validate(back, errors);
        Assert.False(errors.Any);
    }

    // Shadows written with an opaque six digit color come back with the alpha written out.
    private static string Normalize(string value) =>
        value.Split(' ') is { Length: 5 } parts && parts[4].Length == 7 ? value + "ff" : value;

    [Fact]
    public void Values_take_the_formats_typed_forms_and_names_become_groups()
    {
        var tokens = new Dictionary<string, TokenValue>
        {
            ["color.accent.base"] = new("#0F766E", new Dictionary<string, string> { ["dark"] = "#5eead4", ["high_contrast"] = "#000000" }),
            ["radius.md"] = new("{radius.sm}"),
            ["motion.duration.fast"] = new("100ms"),
            ["motion.easing.standard"] = new("standard"),
            ["design.preset"] = new("atlas"),
        };
        var file = DtcgTokens.Export(tokens, "Acme");
        Assert.Equal(DtcgTokens.Schema, file["$schema"]!.GetValue<string>());
        var accent = file["color"]!["accent"]!["base"]!;
        Assert.Equal("color", accent["$type"]!.GetValue<string>());
        Assert.Equal("srgb", accent["$value"]!["colorSpace"]!.GetValue<string>());
        Assert.Equal("#0f766e", accent["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("#5eead4", accent["$extensions"]![DtcgTokens.Extension]!["modes"]!["dark"]!["hex"]!.GetValue<string>());
        // An alias stays the format's own reference.
        Assert.Equal("{radius.sm}", file["radius"]!["md"]!["$value"]!.GetValue<string>());
        Assert.Equal(100m, file["motion"]!["duration"]!["fast"]!["$value"]!["value"]!.GetValue<decimal>());
        Assert.Equal("ms", file["motion"]!["duration"]!["fast"]!["$value"]!["unit"]!.GetValue<string>());
        Assert.Equal(4, file["motion"]!["easing"]!["standard"]!["$value"]!.AsArray().Count);
        Assert.Equal("choice", file["design"]!["preset"]!["$extensions"]![DtcgTokens.Extension]!["type"]!.GetValue<string>());

        var back = RoundTrip(tokens);
        Assert.Equal("#0f766e", back["color.accent.base"].Value);
        Assert.Equal("#000000", back["color.accent.base"].Modes!["high_contrast"]);
        Assert.Equal("{radius.sm}", back["radius.md"].Value);
        Assert.Equal("standard", back["motion.easing.standard"].Value);
        Assert.Equal("atlas", back["design.preset"].Value);
    }

    [Fact]
    public void A_file_from_another_tool_is_read_with_group_types_seconds_and_font_stacks()
    {
        var file = JsonNode.Parse("""
            {
              "color": { "$type": "color", "accent": { "base": { "$value": { "colorSpace": "srgb", "components": [0, 0.5, 1] } } } },
              "motion": { "duration": { "slow": { "$type": "duration", "$value": { "value": 0.3, "unit": "s" } } } },
              "font": { "family": { "latin": { "$type": "fontFamily", "$value": ["Inter", "sans-serif"] } } }
            }
            """);
        var (tokens, problems) = DtcgTokens.Import(file);
        Assert.Empty(problems);
        Assert.Equal("#0080ff", tokens["color.accent.base"].Value);
        Assert.Equal("300ms", tokens["motion.duration.slow"].Value);
        Assert.Equal("Inter", tokens["font.family.latin"].Value);
    }

    [Fact]
    public void Unknown_tokens_wrong_types_and_values_outside_the_types_are_reported()
    {
        var file = JsonNode.Parse("""
            {
              "color": {
                "accent": { "base": { "$type": "dimension", "$value": { "value": 4, "unit": "px" } } },
                "surface": { "base": { "$type": "color", "$value": { "colorSpace": "display-p3", "components": [1, 0, 0] } } }
              },
              "layout": { "css": { "$value": "body { display: none }" } },
              "motion": { "easing": { "standard": { "$type": "cubicBezier", "$value": [0.1, 0.2, 0.3, 0.4] } } },
              "radius": { "sm": "4px" }
            }
            """);
        var (_, problems) = DtcgTokens.Import(file);
        Assert.Contains(("color.accent.base", "validation.theme.dtcg_type_mismatch"), problems);
        Assert.Contains(("color.surface.base", "validation.theme.color_invalid"), problems);
        Assert.Contains(("layout.css", "validation.theme.token_unknown"), problems);
        Assert.Contains(("motion.easing.standard", "validation.theme.easing_invalid"), problems);
        Assert.Contains(("radius.sm", "validation.theme.dtcg_invalid"), problems);
        Assert.Contains(("file", "validation.theme.dtcg_invalid"), DtcgTokens.Import(JsonNode.Parse("[1]")).Problems);
    }
}
