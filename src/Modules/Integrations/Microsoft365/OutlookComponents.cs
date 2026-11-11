using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>
/// My mail and My calendar (spec 018 T016, FR-006 to FR-016) as components of the Integrations module, registered through
/// spec 032 manifests. Both need <c>integration.microsoft365</c> and the matching feature of the Microsoft 365 connection
/// (spec 032 FR-069); every other state (not connected, admin setup, no mailbox, turned off, degraded) comes from the API.
/// </summary>
public sealed class OutlookComponents : IComponentRegistration
{
    public const string MailFeature = "microsoft365:" + Microsoft365Features.Mail;
    public const string CalendarFeature = "microsoft365:" + Microsoft365Features.Calendar;

    private static ComponentManifest Manifest(string id, string icon, string feature, Dictionary<string, PropertySchema> properties, IReadOnlyList<string> data)
    {
        var key = "experience.component." + id.Replace('.', '_');
        return new(id, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", "personal", icon), properties,
            new PlacementRules(MinSpan: 4), ["locale", "direction"], new ComponentRequirements([Capabilities.IntegrationMicrosoft365], [], feature),
            ComponentEvents.None, data);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        Manifest("outlook.my_mail", "mail", MailFeature, new()
        {
            ["view"] = new PropertySchema("enum", Default: Json("focused"), Enum: ["focused", "all"]),
            ["top"] = new PropertySchema("integer", Default: Json(5), Minimum: 1, Maximum: 10),
        }, ["/api/v1/me/mail", "/api/v1/me/outlook"]),
        Manifest("outlook.my_calendar", "calendar", CalendarFeature, new()
        {
            ["days"] = new PropertySchema("integer", Default: Json(7), Minimum: 1, Maximum: 14),
            ["hidePrivate"] = new PropertySchema("boolean", Default: Json(false)),
        }, ["/api/v1/me/calendar", "/api/v1/me/outlook", "/api/v1/calendar/summary", "/api/v1/calendar/events"]),
    ];

    /// <summary>
    /// Spec 008 FR-051: My mail in the personal region of the default home while its feature is on. The calendar card of
    /// spec 037 (<c>calendar.overview</c>) replaced My calendar there; existing My calendar placements render that card.
    /// </summary>
    public IEnumerable<string> PersonalHomeDefaults() => ["outlook.my_mail@1"];
}
