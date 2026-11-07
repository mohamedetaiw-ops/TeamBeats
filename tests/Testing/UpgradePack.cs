using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Testing;

/// <summary>
/// A test-only component with two majors and a migration hook between them (spec 032 FR-030, T012): <c>unit</c> in
/// version 1 becomes <c>format</c> in version 2, and version 2 allows shorter labels, so a long label fails validation
/// after the upgrade. A second hook skips a major and must be refused as out of range. Names reuse the KPI resource keys.
/// </summary>
public sealed class UpgradePack : IComponentRegistration
{
    public const string Id = "test.upgrade.gauge";

    private static ComponentManifest Gauge(string version, Dictionary<string, PropertySchema> properties) =>
        new(Id, version, "TeamBeats", "^1",
            new ComponentDisplay("experience.component.reference_alpha_kpi.name", "experience.component.reference_alpha_kpi.description", "data", "square"),
            properties, new PlacementRules(), ["locale"], ComponentRequirements.None, ComponentEvents.None, []);

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        Gauge("1.0.0", new()
        {
            ["label"] = new PropertySchema("string", Required: true, MaxLength: 80),
            ["unit"] = new PropertySchema("enum", Enum: ["count", "percent"]),
        }),
        Gauge("2.0.0", new()
        {
            ["label"] = new PropertySchema("string", Required: true, MaxLength: 20),
            ["format"] = new PropertySchema("enum", Enum: ["number", "percent"]),
        }),
    ];

    public IEnumerable<ComponentMigration> Migrations() =>
    [
        new(Id, 1, 2, props =>
        {
            var source = props is { ValueKind: JsonValueKind.Object } p ? JsonNode.Parse(p.GetRawText())!.AsObject() : [];
            var result = new JsonObject { ["label"] = source["label"]?.DeepClone() };
            if (source["unit"]?.GetValue<string>() is { } unit)
            {
                result["format"] = unit == "count" ? "number" : unit;
            }
            return JsonSerializer.SerializeToElement(result);
        }),
        new(Id, 2, 4, props => props),
    ];
}
