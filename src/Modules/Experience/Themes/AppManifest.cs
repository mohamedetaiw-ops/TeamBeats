using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Themes;

public sealed record ManifestIcon(string Src, string Sizes, string? Type, string Purpose);

/// <summary>The web app manifest (spec 030 FR-001), named as the W3C fields so it serializes as the browser reads it.</summary>
public sealed record WebManifest(string Id, string Name, string Short_name, string Lang, string Dir, string Start_url, string Scope, string Display,
    string Theme_color, string Background_color, IReadOnlyList<ManifestIcon> Icons);

/// <summary>
/// The installable app (spec 030 US1, FR-001): a manifest per tenant host with the tenant's name and the brand of the
/// viewer's organization (spec 033), so a tenant with two brands installs each organization's own icon and colours. The
/// app icon is the brand's favicon when it has one, and otherwise a mark drawn from the tenant's initials in the brand
/// colour. The tenant comes from the session, never from the request.
/// </summary>
public sealed partial class AppManifest(ThemeService themes, ITenantSettingsReader tenant, IOrganizationHierarchy hierarchy, ITenantContext context,
    IEntitlementService entitlements, RequestLocale locale)
{
    public const int ShortNameLength = 12;

    /// <summary>The scope whose brand the viewer's app follows: their primary unit, or the tenant.</summary>
    private async Task<string> ScopeAsync(CancellationToken ct)
    {
        var memberships = await hierarchy.GetActiveMembershipsAsync(context.ActorId ?? throw Primitives.AppException.Denied(), ct);
        return (memberships.FirstOrDefault(m => m.IsPrimary) ?? memberships.FirstOrDefault())?.OrgUnitId.ToString() ?? "tenant";
    }

    private async Task<(string Name, string Accent, string Surface, string? Favicon)> BrandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobilePwa, ct);
        var theme = await themes.ThemeAsync(await ScopeAsync(ct), ct);
        var light = theme.Variants.FirstOrDefault(v => v.Mode == ThemeModes.Light && v.Density == ThemeModes.Comfortable) ?? theme.Variants[0];
        var branding = await tenant.GetBrandingAsync(ct);
        var name = string.IsNullOrWhiteSpace(branding.DisplayName) ? "TeamBeats" : branding.DisplayName.Trim();
        return (name, Color(light.Tokens, "color.accent.base", "#4338ca"), Color(light.Tokens, "color.surface.base", "#f7f8fb"),
            theme.Assets.TryGetValue("favicon", out var favicon) ? ThemeService.AssetUrlOf(favicon) : null);
    }

    public async Task<WebManifest> ManifestAsync(CancellationToken ct)
    {
        var (name, accent, surface, favicon) = await BrandAsync(ct);
        var icons = new List<ManifestIcon>();
        if (favicon is not null)
        {
            icons.Add(new ManifestIcon(favicon, "any", null, "any"));
        }
        icons.Add(new ManifestIcon("/api/v1/experience/app-icon.svg", "any", "image/svg+xml", "any"));
        icons.Add(new ManifestIcon("/api/v1/experience/app-icon.svg?maskable=true", "any", "image/svg+xml", "maskable"));
        // Raster icons of the platform, so every browser that wants a PNG of a known size can install the app.
        icons.Add(new ManifestIcon("/icons/icon-192.png", "192x192", "image/png", "any"));
        icons.Add(new ManifestIcon("/icons/icon-512.png", "512x512", "image/png", "any"));
        var shortName = name.Length <= ShortNameLength ? name : name[..ShortNameLength].TrimEnd();
        return new WebManifest("/", name, shortName, locale.Locale, locale.Direction, "/?source=app", "/", "standalone", accent, surface, icons);
    }

    /// <summary>The app icon: the tenant's initials on the brand colour, with room for the mask when it is maskable.</summary>
    public async Task<string> IconSvgAsync(bool maskable, CancellationToken ct)
    {
        var (name, accent, surface, _) = await BrandAsync(ct);
        var initials = WebUtility.HtmlEncode(Initials(name));
        var radius = maskable ? 0 : 112;
        var size = initials.Length > 1 ? 200 : 260;
        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="{WebUtility.HtmlEncode(name)}"><rect width="512" height="512" rx="{radius}" fill="{accent}"/><text x="256" y="256" dy=".35em" text-anchor="middle" font-family="system-ui, sans-serif" font-weight="700" font-size="{size}" fill="{surface}">{initials}</text></svg>""";
    }

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = words.Take(2).Select(w => StringInfo.GetNextTextElementLength(w) is var n and > 0 ? w[..n] : "").Where(l => l.Length > 0);
        var text = string.Concat(letters).ToUpperInvariant();
        return text.Length == 0 ? "T" : text;
    }

    private static string Color(IReadOnlyDictionary<string, string> tokens, string key, string fallback) =>
        tokens.TryGetValue(key, out var value) && HexColor().IsMatch(value) ? value : fallback;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
