using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// The documents component for spec 032 pages (spec 011 plan, pages): the newest documents of one library, read with the
/// viewer's session, so it shows only what that viewer may open. A tenant without <c>docs.core</c> is not offered it.
/// </summary>
public sealed class DocumentsComponents : IComponentRegistration
{
    public const string Recent = "documents.recent";

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    public IEnumerable<ComponentManifest> Manifests()
    {
        const string key = "experience.component.documents_recent";
        yield return new ComponentManifest(Recent, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", "data", "folder"),
            new Dictionary<string, PropertySchema>
            {
                ["title"] = new("text", MaxLength: 80),
                ["library"] = new("string", Required: true, MinLength: 36, MaxLength: 36),
                ["top"] = new("integer", Default: J(5), Minimum: 1, Maximum: 20),
            },
            new PlacementRules(MinSpan: 3), ["locale", "direction"], new ComponentRequirements([Capabilities.DocsCore], []), ComponentEvents.None,
            ["/api/v1/documents/libraries"]);
    }
}
