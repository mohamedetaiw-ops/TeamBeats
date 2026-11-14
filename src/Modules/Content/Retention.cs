using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Content;

/// <summary>
/// Retention per content type (spec 009 FR-020), run by the content scheduler as the system actor. Once a type's period
/// has passed, either versions older than the period go (never the live or draft version), or archived items left
/// unchanged for the period go with everything they hold. An item under a spec 011 legal hold is left whole, and so is
/// an item a campaign sends, whose send log keeps its own retention, and an item people confirmed, as the confirmations are
/// evidence that is never deleted (FR-007).
/// </summary>
public sealed class ContentRetention(IDbSession session, Sites sites, ILegalHolds holds, IAuditWriter audit)
{
    public async Task<int> RunAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var done = 0;
        foreach (var key in ContentTypes.Defaults.Keys)
        {
            if (await sites.TypeAsync(key, ct) is not { RetentionMonths: { } months, RetentionAction: { } action })
            {
                continue;
            }
            var cutoff = now.AddMonths(-months);
            done += action == RetentionActions.Item ? await ItemsAsync(key, cutoff, limit, ct) : await VersionsAsync(key, cutoff, limit, ct);
        }
        return done;
    }

    private async Task<int> VersionsAsync(string type, DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select v.item_id, v.number from content.content_version v join content.content_item i on i.id = v.item_id
            where i.type = @type and v.created_at < @cutoff
              and v.number is distinct from i.published_version and v.number is distinct from i.draft_version
            order by v.item_id, v.number limit @limit
            """, new { type, cutoff, limit }, r => (Item: r.GetGuid(0), Number: r.GetInt32(1)), ct);
        var held = await holds.HeldAsync(Items.ResourceType, rows.Select(r => r.Item).Distinct().ToList(), ct);
        var deleted = 0;
        foreach (var group in rows.Where(r => !held.Contains(r.Item)).GroupBy(r => r.Item))
        {
            var numbers = group.Select(r => r.Number).ToArray();
            await session.ExecAsync("delete from content.content_version where item_id = @item and number = any(@numbers)", new { item = group.Key, numbers }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentRetentionApplied, "content_item", group.Key.ToString(),
                Changes: new { deleted = "versions", versions = numbers }), ct);
            deleted += numbers.Length;
        }
        return deleted;
    }

    private async Task<int> ItemsAsync(string type, DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        var ids = await session.QueryAsync(
            """
            select i.id from content.content_item i
            where i.type = @type and i.status = 'archived' and i.updated_at < @cutoff
              and not exists (select 1 from content.campaign_step s where s.item_id = i.id)
              and not exists (select 1 from content.acknowledgement a where a.item_id = i.id)
            order by i.id limit @limit
            """, new { type, cutoff, limit }, r => r.GetGuid(0), ct);
        var held = await holds.HeldAsync(Items.ResourceType, ids, ct);
        var deleted = 0;
        foreach (var id in ids.Where(i => !held.Contains(i)))
        {
            await session.ExecAsync(
                """
                delete from content.comment_report where comment_id in (select id from content.comment where item_id = @id);
                delete from content.comment where item_id = @id;
                delete from content.reaction where item_id = @id;
                delete from content.ack_report where item_id = @id;
                delete from content.audience_member where item_id = @id;
                delete from content.item_tag where item_id = @id;
                delete from content.item_event where item_id = @id;
                delete from content.content_version where item_id = @id;
                delete from content.content_item where id = @id;
                """, new { id }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentRetentionApplied, "content_item", id.ToString(), Changes: new { deleted = "item", type }), ct);
            deleted++;
        }
        return deleted;
    }
}
