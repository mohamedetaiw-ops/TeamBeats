using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Integrations.Google;

/// <summary>
/// My mail from Gmail (spec 018 R3, FR-024): the same mail port and placement as Outlook's My mail, offered while the
/// tenant's Google Workspace connection has its mail feature on. It needs the connector packs; the states (no mailbox,
/// admin setup, degraded) come from the API.
/// </summary>
public sealed class GoogleComponents : IComponentRegistration
{
    public const string MailFeature = GooglePack.KindName + ":" + GoogleFeatures.Mail;

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        new("google.my_mail", "1.0.0", "TeamBeats", "^1",
            new ComponentDisplay("experience.component.google_my_mail.name", "experience.component.google_my_mail.description", "personal", "mail"),
            new Dictionary<string, PropertySchema>
            {
                ["view"] = new PropertySchema("enum", Default: Json("focused"), Enum: ["focused", "all"]),
                ["top"] = new PropertySchema("integer", Default: Json(5), Minimum: 1, Maximum: 10),
            },
            new PlacementRules(MinSpan: 4, Source: "mail", States: PlacementStates.Required), ["locale", "direction"],
            new ComponentRequirements([Capabilities.IntegrationConnectors], [], MailFeature), ComponentEvents.None, ["/api/v1/me/mail"]),
    ];

    /// <summary>In the personal region of the default home while the Gmail feature is on (spec 008 FR-051).</summary>
    public IEnumerable<string> PersonalHomeDefaults() => ["google.my_mail@1"];
}
