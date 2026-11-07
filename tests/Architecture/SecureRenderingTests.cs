using System.Text.RegularExpressions;
using TeamBeats.Modules.Experience.Components;
using Xunit;

namespace TeamBeats.Architecture.Tests;

/// <summary>
/// Spec 026 T015 (FR-025, with spec 032 FR-035): the web app's content security policy runs no inline script, the web
/// code never writes markup from data, and every text a tenant configures goes through the server check that refuses
/// executable content.
/// </summary>
public sealed partial class SecureRenderingTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TeamBeats.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly string[] Skipped = ["node_modules", ".next", "test-results"];

    private static IEnumerable<string> WebSources() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", "Web"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.Ordinal) || f.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".d.ts", StringComparison.Ordinal))
            .Where(f => !Skipped.Any(s => f.Contains($"{Path.DirectorySeparatorChar}{s}{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));

    [GeneratedRegex(@"script-src ([^""`;]*)")]
    private static partial Regex ScriptSource();

    [Fact]
    public void The_content_security_policy_allows_scripts_only_with_a_nonce()
    {
        var proxy = File.ReadAllText(Path.Combine(Root, "src", "Web", "proxy.ts"));
        var match = ScriptSource().Match(proxy);
        Assert.True(match.Success, "proxy.ts must set a script-src directive.");
        var directive = match.Groups[1].Value;
        Assert.Contains("'nonce-${nonce}'", directive, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", directive, StringComparison.Ordinal);
        Assert.DoesNotContain("*", directive, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", proxy, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", proxy, StringComparison.Ordinal);
        // 'unsafe-eval' is for the development server only.
        Assert.Contains("dev ? \" 'unsafe-eval'\" : \"\"", proxy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_app_never_writes_markup_from_data()
    {
        string[] forbidden = ["dangerouslySetInnerHTML", ".innerHTML", ".outerHTML", "insertAdjacentHTML", "document.write", "new Function(", "eval("];
        var violations = WebSources()
            .SelectMany(f => forbidden.Where(token => File.ReadAllText(f).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(Root, f)}: {token}"))
            .ToList();
        Assert.True(violations.Count == 0, "Markup written from data: " + string.Join(", ", violations));
    }

    [Fact]
    public void Configurable_fields_have_no_raw_markup_type_and_text_is_checked_for_executable_content()
    {
        // No html or rich text property type exists in R1. A future rich text type needs a server-side sanitizer
        // (FR-025) and must be added to this test with it.
        Assert.DoesNotContain(PropertyValidator.Types, t => t.Contains("html", StringComparison.OrdinalIgnoreCase) || t.Contains("rich", StringComparison.OrdinalIgnoreCase));
        foreach (var payload in new[] { "<script>alert(1)</script>", "<img src=x onerror=alert(1)>", "x\" onmouseover=alert(1)", "< svg/onload=alert(1)>" })
        {
            Assert.True(PropertyValidator.IsExecutable(payload), payload);
        }
        foreach (var url in new[] { "javascript:alert(1)", "JaVaScRiPt :alert(1)", "data:text/html;base64,PHNjcmlwdD4=", "//evil.example", "http://example.com" })
        {
            Assert.False(PropertyValidator.IsSafeUrl(url), url);
        }
    }
}
