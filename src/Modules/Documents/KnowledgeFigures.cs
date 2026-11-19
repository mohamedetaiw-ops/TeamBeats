using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Knowledge articles for the analytics knowledge dashboard (spec 021 section 2): the most recent published articles the
/// caller may read, through the content contract, with the count of current helpfulness answers. No reader is named and
/// no comment is passed on.
/// </summary>
public sealed class KnowledgeFigures(IKnowledgeContent content, IDbSession session) : IKnowledgeFigures
{
    public const int Limit = 100;

    public async Task<IReadOnlyList<KnowledgeArticleFigure>> ArticlesAsync(CancellationToken ct = default)
    {
        var articles = await content.ReadableListAsync(Limit, ct);
        var ids = articles.Select(a => a.ItemId).ToArray();
        var counts = (await session.QueryAsync(
            "select item_id, count(*), count(*) filter (where helpful) from documents.kb_feedback where item_id = any(@ids) group by 1",
            new { ids }, r => (Id: r.GetGuid(0), Ratings: r.GetInt64(1), Helpful: r.GetInt64(2)), ct)).ToDictionary(c => c.Id);
        return articles.Select(a =>
        {
            var c = counts.GetValueOrDefault(a.ItemId);
            return new KnowledgeArticleFigure(a.ItemId, a.Titles, a.ReviewDueAt, c.Ratings, c.Helpful);
        }).ToList();
    }
}
