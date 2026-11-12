using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// The engagement components for spec 032 pages, named in spec 008 FR-055 as the planned home cards for recognition and
/// celebrations (spec 023 T015): the latest discussions and the active poll of a community, the latest recognitions,
/// the points leaderboard, today's celebrations and the latest award winners. Each reads the engagement API with the viewer's session, so it shows only what that viewer may see, and
/// a tenant whose package lacks the capability behind a card is not offered it.
/// </summary>
public sealed class EngagementComponents : IComponentRegistration
{
    public const string Recognition = "engagement.recognition";
    public const string Leaderboard = "engagement.leaderboard";
    public const string Celebrations = "engagement.celebrations";
    public const string Awards = "engagement.awards";
    public const string Discussions = "engagement.discussions";
    public const string Poll = "engagement.poll";

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, PropertySchema> Props(int top, int max) => new()
    {
        ["title"] = new("text", MaxLength: 80),
        ["top"] = new("integer", Default: J(top), Minimum: 1, Maximum: max),
    };

    private static Dictionary<string, PropertySchema> CommunityProps(int top, int max) => new(Props(top, max))
    {
        ["community"] = new("string", Required: true, MinLength: 36, MaxLength: 36),
    };

    private static ComponentManifest Manifest(string reference, string key, string icon, Dictionary<string, PropertySchema> props, string capability, string path) =>
        new(reference, "1.0.0", "TeamBeats", "^1", new ComponentDisplay(key + ".name", key + ".description", "people", icon), props,
            new PlacementRules(MinSpan: 3), ["locale", "direction"], new ComponentRequirements([capability], []), ComponentEvents.None, [path]);

    public IEnumerable<ComponentManifest> Manifests()
    {
        yield return Manifest(Recognition, "experience.component.engagement_recognition", "spark", Props(3, 10),
            Capabilities.EngagementRecognition, "/api/v1/engagement/recognitions");
        yield return Manifest(Leaderboard, "experience.component.engagement_leaderboard", "trend-up", Props(5, 10),
            Capabilities.EngagementPoints, "/api/v1/engagement/leaderboard");
        yield return Manifest(Celebrations, "experience.component.engagement_celebrations", "calendar", Props(4, 10),
            Capabilities.EngagementCommunities, "/api/v1/engagement/celebrations");
        yield return Manifest(Awards, "experience.component.engagement_awards", "star", Props(3, 5),
            Capabilities.EngagementRecognition, "/api/v1/engagement/awards");
        // One community's latest posts and its newest open poll, read through the same queries as the community pages
        // (FR-022), so a closed community the viewer cannot read shows nothing about itself.
        yield return Manifest(Discussions, "experience.component.engagement_discussions", "chat", CommunityProps(3, 10),
            Capabilities.EngagementCommunities, "/api/v1/engagement/communities");
        yield return Manifest(Poll, "experience.component.engagement_poll", "trend-up", CommunityProps(1, 1),
            Capabilities.EngagementCommunities, "/api/v1/engagement/communities");
    }
}
