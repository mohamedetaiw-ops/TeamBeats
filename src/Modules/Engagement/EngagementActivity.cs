using TeamBeats.Modules.Analytics.Contracts;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// Feeds analytics (spec 021) with what people do in communities and engagement (spec 023 FR-024, SC-001). Named actions
/// go as the person's own events; anonymous ones (anonymous Speak Up posts, anonymous survey responses and poll votes)
/// go as counts that carry no user, key, unit, community or time finer than the day, so analytics holds nothing that
/// points to who acted (FR-005).
/// </summary>
public sealed class EngagementActivity(IAnalyticsEvents analytics)
{
    public Task ActedAsync(string action, Guid? communityId, CancellationToken ct) =>
        analytics.EmitAsync(AnalyticsEvents.EngagementAction, new Dictionary<string, object?> { ["action"] = action, ["communityId"] = communityId }, ct);

    public Task AnonymousAsync(string action, CancellationToken ct) =>
        analytics.EmitAsync(AnalyticsEvents.EngagementAnonymousAction, new Dictionary<string, object?> { ["action"] = action }, ct);
}
