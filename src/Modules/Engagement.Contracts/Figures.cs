using TeamBeats.Primitives;

namespace TeamBeats.Modules.Engagement.Contracts;

/// <summary>
/// Engagement figures for the analytics engagement dashboard (spec 021 section 2): community activity, recognition and
/// survey and poll participation in a period. Weeks are ISO week starts. <see cref="Communities"/> counts posts and
/// comments per active community with its active members as people; <see cref="Surveys"/> counts responses per survey
/// with the title as label. No post, comment, answer or person is passed on.
/// </summary>
public sealed record EngagementSummary(
    long Posts,
    long Comments,
    long Reactions,
    long Contributors,
    IReadOnlyList<GroupFigure> ActivityWeeks,
    IReadOnlyList<GroupFigure> Communities,
    long Recognitions,
    long Recognized,
    IReadOnlyList<GroupFigure> RecognitionWeeks,
    IReadOnlyList<GroupFigure> RecognitionValues,
    IReadOnlyList<GroupFigure> Surveys,
    long Polls,
    long PollVoters);

/// <summary>Engagement figures for analytics; the caller decides who may see them. Current tenant only.</summary>
public interface IEngagementFigures
{
    Task<EngagementSummary> SummaryAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default);
}
