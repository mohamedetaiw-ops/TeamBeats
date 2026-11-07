using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.People;

/// <summary>
/// The people component (spec 010 US4, FR-010): new joiners, the members of a unit and its subtree, or a named set of
/// people, registered through a spec 032 manifest. It reads the directory API, which applies the visibility policy on
/// every render, so the component shows only people the viewer may see.
/// </summary>
public sealed class PeopleComponents : IComponentRegistration
{
    public const string Id = "people.list";
    public const int MaxNamed = 20;

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public IEnumerable<ComponentManifest> Manifests()
    {
        var key = "experience.component." + Id.Replace('.', '_');
        return
        [
            new(Id, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", "people", "people"), new Dictionary<string, PropertySchema>
            {
                ["source"] = new PropertySchema("enum", Required: true, Default: Json("newcomers"), Enum: ["newcomers", "unit", "named"]),
                ["unitId"] = new PropertySchema("string", MaxLength: 36),
                ["people"] = new PropertySchema("list", Default: Json(Array.Empty<string>()), Items: new PropertySchema("string", MinLength: 36, MaxLength: 36), MaxItems: MaxNamed),
                ["top"] = new PropertySchema("integer", Default: Json(6), Minimum: 1, Maximum: 20),
            }, new PlacementRules(MinSpan: 3), ["locale", "direction"],
            new ComponentRequirements([Capabilities.PeopleDirectory], [Permissions.PeopleView]), ComponentEvents.None,
            ["/api/v1/people", "/api/v1/people/newcomers"]),
        ];
    }
}
