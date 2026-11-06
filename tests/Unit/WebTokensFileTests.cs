using System.Text;
using TeamBeats.Modules.Experience.Themes;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 033 T010: the web app reads only <c>--tb-*</c> variables generated from the token schema. The platform
/// default stylesheet in the web app is generated here and checked on every run, so a token added, renamed or
/// changed in the schema cannot drift from the frontend. Regenerate with TEAMBEATS_UPDATE_TOKENS=1.
/// </summary>
public sealed class WebTokensFileTests
{
    private static readonly Dictionary<string, (int Depth, Guid? Scope)> NoLocks = new();

    private static string TokensPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TeamBeats.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "Web", "app", "tokens.css");
    }

    /// <summary>One declaration per line so a token change reads as a one-line diff.</summary>
    private static string Pretty(string css, string indent) =>
        css.Replace("{", "{\n" + indent + "  ").Replace(";", ";\n" + indent + "  ").Replace("  }", "}\n" + indent);

    internal static string Generate()
    {
        var theme = ThemeEngine.Resolve([], NoLocks);
        var file = new StringBuilder();
        file.Append("/* Generated from the token schema (spec 033). Do not edit: run the unit tests with TEAMBEATS_UPDATE_TOKENS=1. */\n");
        file.Append("/* Platform defaults, used before a tenant theme loads and on signed-out pages. */\n");
        file.Append(Pretty(ThemeEngine.Css(theme.Variants["light.comfortable"]), "").TrimEnd()).Append('\n');
        file.Append("\n@media (prefers-color-scheme: dark) {\n  ");
        file.Append(Pretty(ThemeEngine.Css(theme.Variants["dark.comfortable"]), "  ").TrimEnd()).Append("\n}\n");
        return file.ToString();
    }

    [Fact]
    public void Web_tokens_file_matches_the_token_schema()
    {
        var expected = Generate();
        var path = TokensPath();
        if (Environment.GetEnvironmentVariable("TEAMBEATS_UPDATE_TOKENS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.True(File.Exists(path), "Missing src/Web/app/tokens.css. Run the unit tests with TEAMBEATS_UPDATE_TOKENS=1 and commit the file.");
        Assert.True(expected == File.ReadAllText(path).ReplaceLineEndings("\n"),
            "The token schema changed. Regenerate src/Web/app/tokens.css with TEAMBEATS_UPDATE_TOKENS=1 and commit it.");
    }

    [Fact]
    public void Every_token_has_a_custom_property_in_both_modes()
    {
        var css = Generate();
        foreach (var token in TokenSchema.All)
        {
            var count = css.Split(token.CssName + ":").Length - 1;
            Assert.True(count == 2, $"{token.CssName} appears {count} times");
        }

        Assert.DoesNotContain("url(", css);
    }
}
