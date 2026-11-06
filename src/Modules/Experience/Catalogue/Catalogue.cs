using System.Text.Json;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Experience.Catalogue;

/// <summary>A layout region: its column span at full width and its section mode (spec 032 FR-004, FR-008).</summary>
public sealed record LayoutRegion(string Id, int Span, string Section);

/// <summary>One of the 10 baseline layouts (FR-006). <c>Fallback</c> names how regions collapse at narrow widths (FR-013).</summary>
public sealed record LayoutDefinition(string Id, string Version, string NameKey, IReadOnlyList<LayoutRegion> Regions, string Fallback);

/// <summary>
/// What a region allows (FR-012, FR-019): component categories (empty allows any), placement counts, protection and whether
/// employees may personalize it (spec 008). It never names a concrete component (FR-014).
/// </summary>
public sealed record RegionPolicy(IReadOnlyList<string> AllowedCategories, int MinPlacements, int MaxPlacements, string Protection, bool Personal);

public sealed record TemplateRegion(string Id, RegionPolicy Policy);

/// <summary>One of the 12 page templates (FR-015) with its visual variants (FR-016).</summary>
public sealed record TemplateDefinition(string Id, string Version, string State, string NameKey, string DescriptionKey, string Layout,
    IReadOnlyList<string> Variants, IReadOnlyList<TemplateRegion> Regions);

public static class RegionProtection
{
    public const string Optional = "optional";
    public const string Mandatory = "mandatory";
    public const string Protected = "protected";
}

/// <summary>The platform layout and template catalogue, loaded once from the embedded <c>catalogue.json</c>.</summary>
public sealed class PlatformCatalogue
{
    private sealed record File(List<LayoutDefinition> Layouts, List<TemplateDefinition> Templates);

    public PlatformCatalogue()
    {
        using var stream = typeof(PlatformCatalogue).Assembly.GetManifestResourceStream("TeamBeats.Modules.Experience.Catalogue.catalogue.json")
            ?? throw new InvalidOperationException("The experience catalogue is not embedded.");
        var file = JsonSerializer.Deserialize<File>(stream, JsonDefaults.Options) ?? throw new InvalidOperationException("Empty catalogue.");
        Layouts = file.Layouts.ToDictionary(l => l.Id, StringComparer.Ordinal);
        Templates = file.Templates.ToDictionary(t => t.Id, StringComparer.Ordinal);
        foreach (var template in file.Templates)
        {
            var layout = Layouts[template.Layout];
            if (!template.Regions.Select(r => r.Id).Order(StringComparer.Ordinal).SequenceEqual(layout.Regions.Select(r => r.Id).Order(StringComparer.Ordinal)))
            {
                throw new InvalidOperationException($"Template {template.Id} regions do not match layout {layout.Id}.");
            }
        }
    }

    public IReadOnlyDictionary<string, LayoutDefinition> Layouts { get; }

    public IReadOnlyDictionary<string, TemplateDefinition> Templates { get; }
}
