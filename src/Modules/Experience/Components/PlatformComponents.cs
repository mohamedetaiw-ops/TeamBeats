using System.Text.Json;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Experience.Components;

/// <summary>
/// The tool box utilities of spec 008 FR-050: platform components registered through spec 032 manifests like any other
/// module's components, so pages, templates and personal layouts place them without knowing them. They need no package
/// capability. Weather reads the weather connector of the Integrations module (Open-Meteo, decision 2026-11-07).
/// </summary>
public sealed class PlatformComponents : IComponentRegistration
{
    /// <summary>World clock zones: IANA names, so a city's daylight saving rules follow the zone.</summary>
    public const int MaxClockZones = 6;

    private static ComponentManifest Manifest(string id, string icon, Dictionary<string, PropertySchema> properties, PlacementRules placement,
        IReadOnlyList<string>? data = null, string category = "utility")
    {
        var key = "experience.component." + id.Replace('.', '_');
        return new(id, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", category, icon), properties,
            placement, ["locale", "direction"], ComponentRequirements.None, ComponentEvents.None, data ?? [], TrustTiers.Platform, ComponentStates.Active);
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
        }, new PlacementRules(MinSpan: 3), ["/api/v1/weather"]),
        // The home briefing (spec 008 FR-056): greeting, date and weather, and the next three things that need the viewer from
        // the task inbox (016), policies to accept (025) and today's meetings (018). Each source is read with the viewer's own
        // session and left out when the tenant does not have it, so the component needs no capability of its own.
        Manifest("home.briefing", "spark", new()
        {
            ["location"] = new PropertySchema("string", MaxLength: 100),
        }, new PlacementRules(MinSpan: 6, Singleton: true),
            ["/api/v1/me", "/api/v1/tasks", "/api/v1/me/policies", "/api/v1/me/calendar", "/api/v1/weather"], "personal"),
    ];
}
