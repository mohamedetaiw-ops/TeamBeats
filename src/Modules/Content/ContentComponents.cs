using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Content;

/// <summary>
/// The content components of spec 009 US7, registered through spec 032 manifests like any other module's components,
/// so templates, layouts and the builder never name them. Each one reads the content APIs with the viewer's session, so
/// what it shows is checked for that viewer on every render (US7 scenario 2). A tenant without the capability is not
/// offered them and the APIs refuse them (scenario 5).
/// </summary>
public sealed class ContentComponents : IComponentRegistration
{
    public const string NewsFeed = "content.news_feed";
    public const string RichText = "content.rich_text";
    public const string Links = "content.links";
    public const string CallToAction = "content.call_to_action";
    public const string AnnouncementBanner = "content.announcement_banner";
    public const int MaxLinks = 12;

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static ComponentManifest Manifest(string id, string category, string icon, Dictionary<string, PropertySchema> properties, PlacementRules placement,
        IReadOnlyList<string> capabilities, IReadOnlyList<string> data)
    {
        var key = "experience.component." + id.Replace('.', '_');
        return new(id, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", category, icon), properties, placement,
            ["locale", "direction"], new ComponentRequirements(capabilities, []), ComponentEvents.None, data);
    }

    public IEnumerable<ComponentManifest> Manifests() =>
    [
        Manifest(NewsFeed, "data", "news", new()
        {
            ["title"] = new PropertySchema("text", MaxLength: 80),
            ["type"] = new PropertySchema("enum", Default: J(ContentTypes.News), Enum: ["all", ContentTypes.News, ContentTypes.Announcement, ContentTypes.Page]),
            ["site"] = new PropertySchema("string", MaxLength: 36),
            ["top"] = new PropertySchema("integer", Default: J(5), Minimum: 1, Maximum: 20),
            // "featured" shows the lead story as a large card and the rest as compact rows (spec 008 home, spec 036 Featured news).
            ["layout"] = new PropertySchema("enum", Default: J("list"), Enum: ["list", "featured"]),
        }, new PlacementRules(MinSpan: 3), [Capabilities.ContentCore], ["/api/v1/content/feed"]),
        Manifest(RichText, "content", "text", new()
        {
            ["itemId"] = new PropertySchema("string", Required: true, MinLength: 36, MaxLength: 36),
            ["showTitle"] = new PropertySchema("boolean", Default: J(true)),
        }, new PlacementRules(MinSpan: 4), [Capabilities.ContentCore], ["/api/v1/content/items"]),
        Manifest(Links, "navigation", "link", new()
        {
            ["title"] = new PropertySchema("text", MaxLength: 80),
            ["links"] = new PropertySchema("list", Default: J(Array.Empty<string>()), Items: new PropertySchema("url"), MaxItems: MaxLinks),
            ["labels"] = new PropertySchema("list", Default: J(Array.Empty<string>()), Items: new PropertySchema("text", MaxLength: 80), MaxItems: MaxLinks),
        }, new PlacementRules(MinSpan: 3), [Capabilities.ContentCore], []),
        Manifest(CallToAction, "content", "pointer", new()
        {
            ["eyebrow"] = new PropertySchema("text", MaxLength: 80),
            ["heading"] = new PropertySchema("text", Required: true, MaxLength: 120),
            ["body"] = new PropertySchema("text", MaxLength: 400),
            ["label"] = new PropertySchema("text", Required: true, MaxLength: 40),
            ["link"] = new PropertySchema("url", Required: true),
            ["emphasis"] = new PropertySchema("enum", Default: J("primary"), Enum: ["primary", "secondary", "campaign"]),
        }, new PlacementRules(MinSpan: 3), [Capabilities.ContentCore], []),
        Manifest(AnnouncementBanner, "content", "megaphone", [], new PlacementRules(MinSpan: 6, Singleton: true),
            [Capabilities.ContentCore, Capabilities.ContentCriticalAck], ["/api/v1/content/critical/pending"]),
    ];
}
