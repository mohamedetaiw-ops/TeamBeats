using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>
/// The services components for spec 032 pages, named in spec 008 as home components owned by spec 016: the My services
/// strip (favourites first, then recently used, then the catalogue) and the task inbox summary (how many tasks wait and
/// the oldest few). Both read the services API with the viewer's session, so each person sees only their own services
/// and tasks. A tenant without <c>workflow.catalogue</c> is not offered them.
/// </summary>
public sealed class ServicesComponents : IComponentRegistration
{
    public const string MyServices = "services.my_services";
    public const string TaskSummary = "services.task_summary";

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    public IEnumerable<ComponentManifest> Manifests()
    {
        const string services = "experience.component.services_my_services";
        yield return new ComponentManifest(MyServices, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(services + ".name", services + ".description", "services", "grid"),
            new Dictionary<string, PropertySchema>
            {
                ["title"] = new("text", MaxLength: 80),
                ["top"] = new("integer", Default: J(5), Minimum: 1, Maximum: 10),
            },
            new PlacementRules(MinSpan: 4), ["locale", "direction"], new ComponentRequirements([Capabilities.WorkflowCatalogue], []), ComponentEvents.None,
            ["/api/v1/services"]);

        const string tasks = "experience.component.services_task_summary";
        yield return new ComponentManifest(TaskSummary, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(tasks + ".name", tasks + ".description", "services", "check"),
            new Dictionary<string, PropertySchema>
            {
                ["title"] = new("text", MaxLength: 80),
                ["top"] = new("integer", Default: J(3), Minimum: 1, Maximum: 10),
            },
            new PlacementRules(MinSpan: 3), ["locale", "direction"], new ComponentRequirements([Capabilities.WorkflowCatalogue], []), ComponentEvents.None,
            ["/api/v1/tasks"]);
    }
}
