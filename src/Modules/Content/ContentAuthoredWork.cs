using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Content;

/// <summary>
/// Published items a person wrote, for skill suggestions (spec 010 R3 FR-034): the published version is theirs, the
/// item is not Confidential or Restricted, and it is read in its primary language as the title and the summary, or the
/// opening of the body when there is no summary.
/// </summary>
public sealed class ContentAuthoredWork(IDbSession session) : IAuthoredWorkSource
{
    public const string Name = "content";
    public const int MaxText = 500;

    public string Source => Name;

    public async Task<IReadOnlyList<AuthoredWork>> ByAuthorAsync(Guid userId, DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        var rows = await session.QueryAsync(
            """
            select i.id, i.primary_locale, v.variants::text, i.published_at
            from content.content_item i join content.content_version v on v.item_id = i.id and v.number = i.published_version
            where i.status = 'published' and v.author_user_id = @userId and i.classification in ('public', 'internal') and i.published_at >= @since
            order by i.published_at desc, i.id limit @limit
            """, new { userId, since, limit }, r => (Id: r.GetGuid(0), Locale: r.GetString(1), Variants: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var works = new List<AuthoredWork>();
        foreach (var row in rows)
        {
            var variants = ContentJson.Read<Dictionary<string, ContentVariant>>(row.Variants);
            var variant = variants.GetValueOrDefault(row.Locale) ?? variants.Values.FirstOrDefault();
            if (variant is null)
            {
                continue;
            }
            var text = string.IsNullOrWhiteSpace(variant.Summary) ? BodyValidator.PlainText(variant.Body) : variant.Summary;
            works.Add(new AuthoredWork(Name, row.Id, variant.Title, Cut(text.Trim()), ContentSearchSource.Link(row.Id), row.At));
        }
        return works;
    }

    private static string Cut(string text) => text.Length > MaxText ? text[..MaxText] : text;
}
