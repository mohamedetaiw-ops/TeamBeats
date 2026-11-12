using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Learning;

/// <summary>
/// The My learning home card for spec 032 pages (spec 028 T005): the viewer's open courses, due first, with their
/// progress. It reads the learning API with the viewer's session, so it shows only that viewer's own assignments, and a
/// tenant whose package lacks courses is not offered it.
/// </summary>
public sealed class LearningComponents : IComponentRegistration
{
    public const string MyLearning = "learning.my";

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    public IEnumerable<ComponentManifest> Manifests()
    {
        yield return new ComponentManifest(MyLearning, "1.0.0", "TeamBeats", "^1",
            new ComponentDisplay("experience.component.learning_my.name", "experience.component.learning_my.description", "personal", "book"),
            new Dictionary<string, PropertySchema>
            {
                ["title"] = new("text", MaxLength: 80),
                ["top"] = new("integer", Default: J(3), Minimum: 1, Maximum: 10),
            },
            new PlacementRules(MinSpan: 3), ["locale", "direction"], new ComponentRequirements([Capabilities.LearningCourses], []), ComponentEvents.None,
            ["/api/v1/learning/me"]);
    }
}
