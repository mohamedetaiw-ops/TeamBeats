using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Experience.Personalization;

/// <summary>
/// The components modules suggest for the personal region of the default home (spec 008 FR-051), and the connection
/// features that are off for the tenant (spec 032 FR-069). A suggestion is shown only while its component is available
/// to the viewer, its connection feature is usable, and the viewer may add to the region, so it is a personal placement
/// the viewer can remove like any other. Its placement ID is fixed, so a removal stays removed.
/// </summary>
public sealed class HomeDefaults(IEnumerable<IComponentRegistration> registrations, ComponentRegistry registry, IConnectionFeatures connections)
{
    public const string IdPrefix = "home-";

    /// <summary>The fixed placement ID of a suggestion, such as <c>home-outlook-my-mail</c>.</summary>
    public static string PlacementId(string reference) => IdPrefix + reference.Split('@')[0].Replace('.', '-').Replace('_', '-');

    /// <summary>Connection features (<c>kind:feature</c>) that components declare and that are not on and usable for the tenant.</summary>
    public async Task<IReadOnlySet<string>> FeaturesOffAsync(CancellationToken ct)
    {
        var off = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in registry.All.Select(m => m.Requires.ConnectionFeature).OfType<string>().Distinct())
        {
            var parts = feature.Split(':', 2);
            if (await connections.FindAsync(parts[0], parts[1], ct) is not { Usable: true })
            {
                off.Add(feature);
            }
        }
        return off;
    }

    /// <summary>
    /// Suggested placements per region: only the first personal region of the template, and only where the viewer may add.
    /// A suggestion is left out when <paramref name="regions"/> (the page's own placements after audience rules) already
    /// hold its component, or a component that covers it (spec 008 FR-051, spec 032 FR-070).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Placement>>> ForAsync(TemplateDefinition template,
        IReadOnlyDictionary<string, RegionRights> rights, Func<ComponentManifest, Availability> availability, CancellationToken ct,
        IReadOnlyList<PageRegion>? regions = null)
    {
        var region = template.Regions.FirstOrDefault(r => r.Policy.Personal);
        if (region is null || !rights.TryGetValue(region.Id, out var r) || !r.Rights.Contains(LayoutEngine.RightAdd))
        {
            return new Dictionary<string, IReadOnlyList<Placement>>();
        }
        var references = registrations.SelectMany(x => x.PersonalHomeDefaults()).Distinct(StringComparer.Ordinal).ToList();
        if (references.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<Placement>>();
        }
        var shown = Shown(regions);
        references = references.Where(reference => !shown.Contains(ComponentId(reference))).ToList();
        if (references.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<Placement>>();
        }
        var off = await FeaturesOffAsync(ct);
        var placements = references
            .Select(registry.Find)
            .OfType<ComponentManifest>()
            .Where(m => m.State == ComponentStates.Active && availability(m) == Availability.Available
                && (m.Requires.ConnectionFeature is not { } f || !off.Contains(f))
                && (region.Policy.AllowedCategories.Count == 0 || region.Policy.AllowedCategories.Contains(m.Display.Category)))
            .Select(m => new Placement(PlacementId(m.Reference), m.Reference))
            .Take(r.MaxPersonalPlacements)
            .ToList();
        return new Dictionary<string, IReadOnlyList<Placement>>(StringComparer.Ordinal) { [region.Id] = placements };
    }

    private static string ComponentId(string reference) => reference.Split('@')[0];

    /// <summary>Component IDs the page already shows anywhere, with the suggestions those components cover.</summary>
    private HashSet<string> Shown(IReadOnlyList<PageRegion>? regions)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (regions is null)
        {
            return shown;
        }
        var covers = registrations.SelectMany(x => x.CoveredSuggestions()).ToLookup(x => x.Component, x => x.Covered, StringComparer.Ordinal);
        foreach (var id in regions.SelectMany(r => r.Placements).Select(p => ComponentId(p.ComponentRef)))
        {
            shown.Add(id);
            shown.UnionWith(covers[id]);
        }
        return shown;
    }
}
