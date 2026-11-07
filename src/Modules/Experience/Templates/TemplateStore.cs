using System.Text.Json;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Experience.Templates;

/// <summary>What a tenant template version stores: everything a page needs from its template, and nothing tenant-specific.</summary>
public sealed record StoredTemplate(IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> Descriptions, string Layout,
    IReadOnlyList<string> Variants, IReadOnlyList<TemplateRegion> Regions);

/// <summary>
/// Finds the template a page names, platform or tenant, by ID and version (spec 032 FR-018). A page keeps the version it
/// records whatever its state, so pages on a deprecated version keep working (FR-021). The platform catalogue holds one version
/// of each platform template, and the page validator reports a page whose recorded version differs. Answers are kept for the request.
/// </summary>
public sealed class TemplateStore(IDbSession session, PlatformCatalogue catalogue)
{
    private readonly Dictionary<(string, string), TemplateDefinition?> _seen = [];

    public static bool IsPlatform(PlatformCatalogue catalogue, string id) => catalogue.Templates.ContainsKey(id);

    public Task<TemplateDefinition?> FindAsync(TemplateRef reference, CancellationToken ct) => FindAsync(reference.Id, reference.Version, ct);

    /// <summary>The tenant template version a page records, or the platform template of that ID; null when there is none.</summary>
    public async Task<TemplateDefinition?> FindAsync(string id, string version, CancellationToken ct)
    {
        if (catalogue.Templates.TryGetValue(id, out var platform))
        {
            return platform;
        }
        if (_seen.TryGetValue((id, version), out var known))
        {
            return known;
        }
        TemplateDefinition? found = null;
        if (int.TryParse(version, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            found = await session.QuerySingleOrDefaultAsync(
                "select version, state, definition::text from experience.template_version where template_id = @id and version = @v",
                new { id, v = number }, r => Map(id, r.GetInt32(0), r.GetString(1), r.GetString(2)), ct);
        }
        _seen[(id, version)] = found;
        return found;
    }

    /// <summary>The definition a page names; it exists, because pages are only made from known templates.</summary>
    public async Task<TemplateDefinition> GetAsync(TemplateRef reference, CancellationToken ct) =>
        await FindAsync(reference, ct) ?? throw new InvalidOperationException($"Template {reference.Id}@{reference.Version} is missing.");

    /// <summary>The version new pages use: the platform template, or the tenant template's latest released version.</summary>
    public async Task<TemplateDefinition?> CurrentAsync(string id, CancellationToken ct)
    {
        if (catalogue.Templates.TryGetValue(id, out var platform))
        {
            return platform;
        }
        return await session.QuerySingleOrDefaultAsync(
            """
            select version, state, definition::text from experience.template_version
            where template_id = @id and state = 'released' order by version desc limit 1
            """, new { id }, r => Map(id, r.GetInt32(0), r.GetString(1), r.GetString(2)), ct);
    }

    public static TemplateDefinition Map(string id, int version, string state, string json)
    {
        var stored = JsonSerializer.Deserialize<StoredTemplate>(json, JsonDefaults.Options)!;
        return new TemplateDefinition(id, version.ToString(System.Globalization.CultureInfo.InvariantCulture), state, "", "", stored.Layout,
            stored.Variants, stored.Regions, stored.Names, stored.Descriptions);
    }
}
