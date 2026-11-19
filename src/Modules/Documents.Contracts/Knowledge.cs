namespace TeamBeats.Modules.Documents.Contracts;

/// <summary>
/// One published knowledge article the caller may read, with its current helpfulness answers (one per reader, the latest)
/// and its review date (spec 021 section 2, knowledge dashboard).
/// </summary>
public sealed record KnowledgeArticleFigure(Guid ItemId, IReadOnlyDictionary<string, string> Titles, DateTimeOffset? ReviewDueAt, long Ratings, long Helpful);

/// <summary>Knowledge figures for analytics, in the current tenant, as the current user: only articles they may read are listed.</summary>
public interface IKnowledgeFigures
{
    Task<IReadOnlyList<KnowledgeArticleFigure>> ArticlesAsync(CancellationToken ct = default);
}
