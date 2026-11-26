using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// A person's own community posts, for skill suggestions (spec 010 R3 FR-034): published, named posts in active
/// communities that are not Confidential. Anonymous posts are never read. The title is the community's name in the
/// tenant's default language and the text is the opening of the post.
/// </summary>
public sealed class EngagementAuthoredWork(IDbSession session, TeamBeats.Modules.Tenancy.Contracts.ITenantSettingsReader tenantSettings) : IAuthoredWorkSource
{
    public const string Name = "community_post";
    public const int MaxText = 500;

    public string Source => Name;

    public async Task<IReadOnlyList<AuthoredWork>> ByAuthorAsync(Guid userId, DateTimeOffset since, int limit, CancellationToken ct = default)
    {
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var rows = await session.QueryAsync(
            """
            select p.id, p.community_id, p.text, p.published_at, c.names::text
            from engagement.post p join engagement.community c on c.id = p.community_id
            where p.author_id = @userId and p.status = 'published' and not p.anonymous and c.status = 'active' and c.classification <> 'confidential'
              and p.published_at >= @since
            order by p.published_at desc, p.id limit @limit
            """, new { userId, since, limit },
            r => (Id: r.GetGuid(0), Community: r.GetGuid(1), Text: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3), Names: r.GetString(4)), ct);
        return rows.Select(r =>
        {
            var names = EngagementJson.Read<Dictionary<string, string>>(r.Names);
            var title = names.GetValueOrDefault(locale) ?? names.Values.FirstOrDefault() ?? "";
            var text = r.Text.Trim();
            return new AuthoredWork(Name, r.Id, title, text.Length > MaxText ? text[..MaxText] : text, Posts.Link(r.Community, r.Id), r.At);
        }).ToList();
    }
}
