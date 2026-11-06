using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Api.DevHost;

/// <summary>
/// The two reference component packs of spec 032 (US1 independent test, swap test S02). They are deliberately unrelated to each
/// other and to every product module, and exist only in the development host and tests. A few manifests are there to exercise
/// registry rules: one needs a capability, one a permission, one is retired, one is deprecated and one is invalid.
/// </summary>
public sealed class ReferencePacks : IComponentRegistration
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static ComponentDisplay Display(string id, string category, string icon)
    {
        var key = "experience.component." + id.Replace('.', '_');
        return new ComponentDisplay(key + ".name", key + ".description", category, icon);
    }

    private static ComponentManifest Manifest(string id, string category, Dictionary<string, PropertySchema> properties,
        PlacementRules? placement = null, IReadOnlyList<string>? context = null, ComponentRequirements? requires = null,
        ComponentEvents? events = null, string version = "1.0.0", string state = ComponentStates.Active) =>
        new(id, version, "TeamBeats", "^1", Display(id, category, "square"), properties, placement ?? new PlacementRules(),
            context ?? ["locale", "direction"], requires ?? ComponentRequirements.None, events ?? ComponentEvents.None, [], TrustTiers.Platform, state);

    private static readonly Dictionary<string, PropertySchema> FilterPayload = new() { ["query"] = new PropertySchema("string", MaxLength: 100) };

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        Manifest("reference.alpha.text", "content", new()
        {
            ["heading"] = new PropertySchema("text", MaxLength: 120),
            ["body"] = new PropertySchema("text", Required: true, MaxLength: 2000),
        }),
        Manifest("reference.alpha.links", "navigation", new()
        {
            ["title"] = new PropertySchema("string", MaxLength: 80),
            ["links"] = new PropertySchema("list", Items: new PropertySchema("url"), MaxItems: 10),
        }),
        Manifest("reference.alpha.kpi", "data", new()
        {
            ["label"] = new PropertySchema("text", Required: true, MaxLength: 60),
            ["value"] = new PropertySchema("number", Minimum: 0, Maximum: 1_000_000_000),
            ["unit"] = new PropertySchema("enum", Default: J("count"), Enum: ["count", "percent"]),
        }, new PlacementRules(MinSpan: 3, MaxSpan: 6)),
        Manifest("reference.alpha.filter", "data", new()
        {
            ["placeholder"] = new PropertySchema("text", MaxLength: 60),
        }, new PlacementRules(Singleton: true), events: new ComponentEvents([new ComponentEvent("filterChanged", FilterPayload)], [])),
        Manifest("reference.beta.banner", "media", new()
        {
            ["image"] = new PropertySchema("image", Required: true),
            ["alt"] = new PropertySchema("text", Required: true, MaxLength: 200),
            ["link"] = new PropertySchema("url"),
        }, new PlacementRules(MinSpan: 6)),
        Manifest("reference.beta.list", "data", new()
        {
            ["pageSize"] = new PropertySchema("integer", Default: J(10), Minimum: 1, Maximum: 50),
        }, context: ["locale", "direction", "pageId"], events: new ComponentEvents([], [new ComponentEvent("filterChanged", FilterPayload)])),
        Manifest("reference.beta.counter", "utility", new()
        {
            ["accent"] = new PropertySchema("color_token", Default: J("color.accent.base")),
        }, requires: new ComponentRequirements([Capabilities.IntegrationMicrosoft365], [])),
        Manifest("reference.beta.restricted", "utility", [], requires: new ComponentRequirements([], [Authorization.Contracts.Permissions.ContentPublish])),
        Manifest("reference.beta.legacy", "content", [], state: ComponentStates.Retired),
        Manifest("reference.beta.old", "content", [], state: ComponentStates.Deprecated),
        // Invalid on purpose: asks for a context value outside the approved list (FR-028), so it is never registered.
        Manifest("reference.beta.invalid", "content", [], context: ["userEmail"]),
    ];
}
