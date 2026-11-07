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

    /// <summary>Suggested placements per region: only the first personal region of the template, and only where the viewer may add.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Placement>>> ForAsync(TemplateDefinition template,
        IReadOnlyDictionary<string, RegionRights> rights, Func<ComponentManifest, Availability> availability, CancellationToken ct)
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
}
