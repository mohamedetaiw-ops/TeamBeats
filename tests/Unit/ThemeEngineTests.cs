using System.IO.Compression;
using System.Text;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 033: typed token validation, alias cycles, contrast math, resolution order with locks, fallback and CSS size.</summary>
public sealed class ThemeEngineTests
{
    private static readonly Dictionary<string, (int Depth, Guid? Scope)> NoLocks = new();

    private static TokenValue V(string value, string? dark = null) =>
        new(value, dark is null ? null : new Dictionary<string, string> { ["dark"] = dark });

    private static Dictionary<string, string[]> Errors(Dictionary<string, TokenValue> tokens)
    {
        var errors = new ValidationErrors();
        TokenSchema.Validate(tokens, errors);
        try
        {
            errors.ThrowIfAny();
            return [];
        }
        catch (AppException e)
        {
            return e.Errors!.ToDictionary(x => x.Key, x => x.Value);
        }
    }

    [Theory]
    [InlineData("color.accent.base", "url(https://evil.example/x.png)")]
    [InlineData("color.accent.base", "expression(alert(1))")]
    [InlineData("color.accent.base", "#123456; background: url(x)")]
    [InlineData("color.accent.base", "red")]
    [InlineData("space.md", "calc(100vw - 1px)")]
    [InlineData("space.md", "5000px")]
    [InlineData("font.family.arabic", "Comic Sans MS")]
    [InlineData("font.family.latin", "Noto Sans Arabic, url(x)")]
    [InlineData("motion.duration.fast", "10s")]
    [InlineData("motion.easing.standard", "cubic-bezier(0,0,1,1)")]
    [InlineData("shadow.sm", "0px 1px 2px 0px url(x)")]
    [InlineData("font.weight.bold", "750")]
    public void Untyped_or_out_of_range_values_are_refused_with_the_token_name(string token, string value)
    {
        var errors = Errors(new() { [token] = V(value) });
        Assert.True(errors.ContainsKey("tokens." + token), string.Join(", ", errors.Keys));
    }

    [Theory]
    [InlineData("color.accent.base", "#0f766e")]
    [InlineData("color.accent.base", "rgb(15, 118, 110)")]
    [InlineData("space.md", "1.25rem")]
    [InlineData("font.family.arabic", "IBM Plex Sans Arabic")]
    [InlineData("font.family.urdu", "Noto Nastaliq Urdu")]
    [InlineData("motion.duration.fast", "150ms")]
    [InlineData("shadow.sm", "0px 2px 4px 0px #00000033")]
    [InlineData("color.link", "{color.accent.base}")]
    public void Typed_values_are_accepted(string token, string value) =>
        Assert.Empty(Errors(new() { [token] = V(value) }));

    [Fact]
    public void Unknown_tokens_modes_and_cross_type_aliases_are_refused()
    {
        var errors = Errors(new()
        {
            ["color.made_up"] = V("#ffffff"),
            ["space.md"] = new TokenValue("16px", new Dictionary<string, string> { ["dark"] = "8px" }),
            ["color.link"] = V("{space.md}"),
        });
        Assert.Equal(["validation.theme.token_unknown"], errors["tokens.color.made_up"]);
        Assert.Equal(["validation.theme.mode_not_allowed"], errors["tokens.space.md.dark"]);
        Assert.Equal(["validation.theme.alias_invalid"], errors["tokens.color.link"]);
    }

    [Fact]
    public void Alias_cycles_are_found()
    {
        var layer = new ThemeLayer(0, null, "brand", null, null, new Dictionary<string, TokenValue>
        {
            ["color.link"] = V("{color.accent.base}"),
            ["color.accent.base"] = V("{color.link}"),
        });
        var cycles = ThemeEngine.AliasCycles([layer]);
        Assert.Contains("color.link", cycles);
        Assert.Contains("color.accent.base", cycles);
        Assert.Empty(ThemeEngine.AliasCycles([layer with { Tokens = new Dictionary<string, TokenValue> { ["color.link"] = V("{color.accent.base}") } }]));
    }

    [Fact]
    public void Contrast_ratio_matches_known_pairs()
    {
        Assert.Equal(21, ThemeEngine.Ratio("#000000", "#ffffff"), 2);
        Assert.Equal(4.48, ThemeEngine.Ratio("#777777", "#ffffff"), 2);
        Assert.Equal(1, ThemeEngine.Ratio("#4338ca", "#4338ca"), 2);
    }

    [Fact]
    public void Platform_defaults_are_complete_and_pass_every_check()
    {
        var theme = ThemeEngine.Resolve([], NoLocks);
        Assert.Equal(4, theme.Variants.Count);
        Assert.All(theme.Variants.Values, v => Assert.Equal(TokenSchema.All.Count, v.Count));
        Assert.Empty(ThemeEngine.Check(theme, strict: true));
        Assert.Empty(theme.InvalidTokens);
    }

    [Fact]
    public void Shell_pairs_block_and_accent_pairs_warn_unless_strict()
    {
        var layer = new ThemeLayer(0, null, "brand", null, null, new Dictionary<string, TokenValue>
        {
            ["color.shell.text"] = V("#3b3f8f"),
            ["color.accent.base"] = V("#fde68a", "#fde68a"),
        });
        var findings = ThemeEngine.Check(ThemeEngine.Resolve([layer], NoLocks), strict: false);
        var shell = Assert.Single(findings, f => f.Token == "color.shell.text" && f.Mode == "light");
        Assert.Equal(ThemeEngine.Blocking, shell.Severity);
        Assert.Equal("validation.theme.contrast_text", shell.Code);
        Assert.True(shell.Ratio < 4.5);
        var accent = Assert.Single(findings, f => f.Token == "color.accent.text" && f.Mode == "light");
        Assert.Equal(ThemeEngine.Warning, accent.Severity);

        var strict = ThemeEngine.Check(ThemeEngine.Resolve([layer], NoLocks), strict: true);
        Assert.Equal(ThemeEngine.Blocking, strict.Single(f => f.Token == "color.accent.text" && f.Mode == "light").Severity);
    }

    [Fact]
    public void Body_text_below_fourteen_px_blocks()
    {
        var layer = new ThemeLayer(0, null, "brand", null, null, new Dictionary<string, TokenValue>
        {
            ["font.size.md"] = new("16px", new Dictionary<string, string> { ["compact"] = "12px" }),
        });
        var finding = Assert.Single(ThemeEngine.Check(ThemeEngine.Resolve([layer], NoLocks), strict: false));
        Assert.Equal("validation.theme.text_too_small", finding.Code);
        Assert.Equal("compact", finding.Against);
    }

    [Fact]
    public void Resolution_follows_platform_tenant_parent_child_and_locks_win()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var brand = Guid.NewGuid();
        var layers = new[]
        {
            new ThemeLayer(2, child, "override", null, null, new Dictionary<string, TokenValue> { ["color.accent.base"] = V("#9a3412"), ["radius.md"] = V("2px") }),
            new ThemeLayer(0, null, "brand", brand, 3, new Dictionary<string, TokenValue> { ["color.accent.base"] = V("#0f766e"), ["radius.md"] = V("6px") }),
            new ThemeLayer(1, parent, "override", null, null, new Dictionary<string, TokenValue> { ["radius.md"] = V("4px") }),
        };
        var open = ThemeEngine.Resolve(layers, NoLocks);
        Assert.Equal("#9a3412", open.Variants["light.comfortable"]["color.accent.base"]);
        Assert.Equal("2px", open.Variants["dark.compact"]["radius.md"]);
        Assert.Equal(child, open.Provenance["radius.md"].ScopeOrgUnitId);

        var locked = ThemeEngine.Resolve(layers, new Dictionary<string, (int, Guid?)> { ["color.accent.base"] = (1, parent) });
        Assert.Equal("#0f766e", locked.Variants["light.comfortable"]["color.accent.base"]);
        Assert.Equal("#0f766e", locked.Variants["dark.comfortable"]["color.accent.base"]);
        var source = locked.Provenance["color.accent.base"];
        Assert.Equal("brand", source.Layer);
        Assert.Equal(brand, source.BrandId);
        Assert.Equal(3, source.BrandVersion);
        Assert.Equal(parent, source.LockedAt);
        Assert.True(source.LockIgnoredOverride);
        Assert.Equal("platform", locked.Provenance["color.surface.base"].Layer);
    }

    [Fact]
    public void Invalid_values_from_older_data_fall_back_to_the_inherited_value()
    {
        var layers = new[]
        {
            new ThemeLayer(0, null, "brand", null, null, new Dictionary<string, TokenValue> { ["color.accent.base"] = V("#0f766e") }),
            new ThemeLayer(1, Guid.NewGuid(), "override", null, null, new Dictionary<string, TokenValue>
            {
                ["color.accent.base"] = V("url(https://evil.example)"),
                ["color.removed_later"] = V("#000000"),
                ["color.link"] = V("{color.text.primary}"),
                ["color.text.primary"] = V("{color.link}"),
            }),
        };
        var theme = ThemeEngine.Resolve(layers, NoLocks);
        Assert.Equal("#0f766e", theme.Variants["light.comfortable"]["color.accent.base"]);
        Assert.Equal(TokenSchema.ByName["color.link"].Light, theme.Variants["light.comfortable"]["color.link"]);
        Assert.Contains("color.accent.base", theme.InvalidTokens);
        Assert.Contains("color.removed_later", theme.InvalidTokens);
        Assert.Contains("color.link", theme.InvalidTokens);
    }

    [Fact]
    public void Css_holds_only_typed_custom_properties_and_stays_under_thirty_kilobytes_compressed()
    {
        var theme = ThemeEngine.Resolve([], NoLocks);
        foreach (var variant in theme.Variants.Values)
        {
            var css = ThemeEngine.Css(variant);
            Assert.Contains("--tb-color-accent-base:#4338ca;", ThemeEngine.Css(theme.Variants["light.comfortable"]));
            Assert.Contains(":root:lang(ar){--tb-font-family-body:var(--tb-font-family-arabic);}", css);
            Assert.Contains("--tb-font-family-arabic:\"Noto Sans Arabic\", \"Noto Sans Arabic\"", css);
            Assert.DoesNotContain("url(", css);
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(css));
            }
            Assert.True(buffer.Length < 30 * 1024, $"{buffer.Length} bytes");
        }
    }
}
