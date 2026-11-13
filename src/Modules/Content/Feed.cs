using System.Text;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public sealed record FeedItem(
    Guid Id,
    Guid SiteId,
    string Type,
    string Locale,
    bool Fallback,
    string Title,
    string? Summary,
    DateTimeOffset PublishedAt,
    bool Pinned,
    bool Featured,
    int ReadMinutes = 1,
    int Reactions = 0,
    string Classification = "internal");

public sealed record FeedPage(IReadOnlyList<FeedItem> Items, string? Next);

/// <summary>
/// The news feed (spec 009 plan, "Feed"): live items whose audience includes the viewer and which the viewer may read,
/// pinned first and then newest, paged by an opaque cursor. Targeting picks what appears; the read check decides access
/// on its own, so a targeted person without permission sees nothing and an item outside the audience stays readable by
/// link (section 3).
/// </summary>
public sealed class ContentFeed(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    ITenantSettingsReader tenantSettings,
    RequestLocale requestLocale,
    AudienceFactsLoader facts,
    Items items,
    Sites sites)
{
    public const int MaxLimit = 50;
    private const int Batch = 100;
    private const int MaxScanned = 1000;

    /// <summary>
    /// A page of the feed. An application app calling the public API (spec 035) has no audience facts, so targeting does
    /// not pick for it: it gets every live item its own roles let it read.
    /// </summary>
    public async Task<FeedPage> PageAsync(Guid? siteId, string? type, string? cursor, int limit, CancellationToken ct, int maxLimit = MaxLimit)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var principal = context.RequirePrincipal();
        if (type is not null && !ContentTypes.Defaults.ContainsKey(type))
        {
            throw AppException.Validation("type", "validation.content.type");
        }
        (int Pinned, DateTimeOffset At, Guid Id)? position = null;
        if (cursor is not null)
        {
            position = Decode(cursor) ?? throw AppException.Validation("cursor", "validation.content.cursor");
        }
        limit = Math.Clamp(limit, 1, maxLimit);
        var me = context.ActorType == ActorType.App ? null : await facts.ForAsync(principal, ct);
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var siteRows = new Dictionary<Guid, SiteRow>();
        var result = new List<FeedItem>();
        var scanned = 0;
        string? next = null;
        while (result.Count < limit && scanned < MaxScanned)
        {
            var rows = await session.QueryAsync(
                $"""
                select {Items.Columns} from content.content_item
                where status = 'published' and (@site::uuid is null or site_id = @site) and (@type::text is null and type <> 'knowledge' or type = @type)
                  and (@first or (pinned::int, published_at, id) < (@pinned, @at, @id))
                order by pinned desc, published_at desc, id desc limit @batch
                """, new
                {
                    site = siteId, type, first = position is null, pinned = position?.Pinned ?? 0, at = position?.At ?? DateTimeOffset.MinValue,
                    id = position?.Id ?? Guid.Empty, batch = Batch,
                }, Items.Read, ct);
            foreach (var row in rows)
            {
                scanned++;
                position = (row.Pinned ? 1 : 0, row.PublishedAt!.Value, row.Id);
                if (!siteRows.TryGetValue(row.SiteId, out var site))
                {
                    siteRows[row.SiteId] = site = (await sites.RowAsync(row.SiteId, false, ct))!;
                }
                if ((me is not null && !AudienceRules.Matches(row.Audience, me)) || !await items.MayReadAsync(row, site, ct) || row.PublishedVersion is not { } number)
                {
                    continue;
                }
                var version = (await items.VersionAsync(row.Id, number, ct))!;
                var (locale, variant) = Items.Pick(version.Variants, requestLocale.Locale, locales.DefaultLocale, row.PrimaryLocale);
                result.Add(new FeedItem(row.Id, row.SiteId, row.Type, locale, locale != requestLocale.Locale, variant.Title, variant.Summary,
                    row.PublishedAt!.Value, row.Pinned, row.Featured, ReadMinutes(variant), Classification: row.Classification));
                if (result.Count == limit)
                {
                    break;
                }
            }
            if (rows.Count < Batch && result.Count < limit)
            {
                position = null; // The end: nothing more to page.
                break;
            }
        }
        if (position is { } p && (result.Count == limit || scanned >= MaxScanned))
        {
            next = Encode(p);
        }
        if (result.Count > 0)
        {
            // One query for the reaction counts of the page; the reaction table is this module's own.
            var counts = await session.QueryAsync("select item_id, count(*)::int from content.reaction where item_id = any(@ids) group by item_id",
                new { ids = result.Select(i => i.Id).ToArray() }, r => (Id: r.GetGuid(0), Count: r.GetInt32(1)), ct);
            var byId = counts.ToDictionary(c => c.Id, c => c.Count);
            result = [.. result.Select(i => byId.TryGetValue(i.Id, out var n) ? i with { Reactions = n } : i)];
        }
        return new FeedPage(result, next);
    }

    /// <summary>Reading time for the feed card: words of the body at 200 words a minute, at least one minute.</summary>
    internal static int ReadMinutes(ContentVariant variant)
    {
        var text = BodyValidator.PlainText(variant.Body);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Math.Max(1, (int)Math.Ceiling(words / 200.0));
    }

    private static string Encode((int Pinned, DateTimeOffset At, Guid Id) p) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.Pinned}|{p.At.UtcTicks}|{p.Id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static (int Pinned, DateTimeOffset At, Guid Id)? Decode(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split('|');
            return parts.Length == 3 && parts[0] is "0" or "1" && long.TryParse(parts[1], out var ticks) && Guid.TryParseExact(parts[2], "N", out var id)
                && ticks is >= 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks
                ? (parts[0] == "1" ? 1 : 0, new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
