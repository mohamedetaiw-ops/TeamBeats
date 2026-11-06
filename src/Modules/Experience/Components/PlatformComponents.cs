using System.Text.Json;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Experience.Components;

/// <summary>
/// The tool box utilities of spec 008 FR-050: platform components registered through spec 032 manifests like any other
/// module's components, so pages, templates and personal layouts place them without knowing them. They need no package
/// capability. Weather has no provider yet (open decision), so it renders the unsupported state and declares no data.
/// </summary>
public sealed class PlatformComponents : IComponentRegistration
{
    /// <summary>World clock zones: IANA names, so a city's daylight saving rules follow the zone.</summary>
    public const int MaxClockZones = 6;

    private static ComponentManifest Manifest(string id, string icon, Dictionary<string, PropertySchema> properties, PlacementRules placement)
    {
        var key = "experience.component." + id.Replace('.', '_');
        return new(id, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", "utility", icon), properties,
            placement, ["locale", "direction"], ComponentRequirements.None, ComponentEvents.None, [], TrustTiers.Platform, ComponentStates.Active);
    }

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        Manifest("toolbox.world_clock", "clock", new()
        {
            ["zones"] = new PropertySchema("list", Default: JsonSerializer.SerializeToElement(Array.Empty<string>()),
                Items: new PropertySchema("string", MinLength: 1, MaxLength: 64), MaxItems: MaxClockZones),
        }, new PlacementRules(MinSpan: 3)),
        Manifest("toolbox.time_converter", "clock", [], new PlacementRules(MinSpan: 4)),
        Manifest("toolbox.weather", "cloud", new()
        {
            ["location"] = new PropertySchema("string", MaxLength: 100),
        }, new PlacementRules(MinSpan: 3)),
    ];
}
