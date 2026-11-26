using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public sealed record SiteGuestShareRequest(Guid? SpaceId, int? Days);

public sealed record SiteGuestShareView(Guid Id, Guid SiteId, Guid SpaceId, string SpaceName, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, ContentPerson CreatedBy,
    DateTimeOffset? RevokedAt, bool Live);

/// <summary>A site shared with a space, as the space's readers see it.</summary>
public sealed record SharedSiteView(Guid ShareId, Guid SiteId, IReadOnlyDictionary<string, string> Names, DateTimeOffset ExpiresAt);

/// <summary>A published item of a shared site in a list: its title and summary in one language.</summary>
public sealed record GuestItemSummary(Guid Id, string Type, string Locale, string Title, string? Summary, DateTimeOffset PublishedAt);

/// <summary>A published item of a shared site as a guest reads it: the text only, no comments, reactions or confirmation.</summary>
public sealed record GuestItemView(Guid Id, Guid SiteId, IReadOnlyDictionary<string, string> SiteNames, string Type, string Locale, string Title, string? Summary,
    BodyDocument Body, DateTimeOffset PublishedAt);

/// <summary>
/// Sites shared read-only with guest spaces (spec 009 R3 US10, FR-025), like the folder shares of spec 011. A person who
/// manages a site shares it with a space they own for 1 to 365 days (30 by default). The space's readers, its guests above
/// all, list the site's published Public and Internal items and read them in the space while the share lasts; nothing
/// else of the intranet opens to them. Never for an archived site; archiving stops guests reading at once.
/// </summary>
public sealed class GuestSites(
    IDbSession session,
    ITenantContext context,
    Sites sites,
    Items items,
    IEntitlementService entitlements,
    ISpaceAccess spaces,
    IAuditWriter audit,
    TeamBeats.Localization.RequestLocale locale,
    IClock clock)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;
    public const int MaxItems = 100;
    private static readonly string[] Readable = ["public", "internal"];

    private const string Select = "select id, site_id, space_id, expires_at, created_at, created_by, revoked_at from content.site_guest_share";

    private sealed record Row(Guid Id, Guid Site, Guid Space, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, Guid CreatedBy, DateTimeOffset? RevokedAt);

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
        r.GetGuid(5), r.Get<DateTimeOffset?>("revoked_at"));

    private async Task<SiteRow> ManagedAsync(Guid siteId, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentGuestSites, ct);
        context.RequireUser();
        if (await sites.RowAsync(siteId, forUpdate, ct) is not { } site || !await sites.ManagesAsync(site, ct))
        {
            throw AppException.NotFound();
        }
        return site;
    }

    private async Task<SiteGuestShareView> ViewAsync(Row row, CancellationToken ct) =>
        new(row.Id, row.Site, row.Space, (await spaces.ForViewerAsync(row.Space, ct))?.Name ?? string.Empty, row.ExpiresAt, row.CreatedAt,
            await items.PersonAsync(row.CreatedBy, ct), row.RevokedAt, row.RevokedAt is null && row.ExpiresAt > clock.UtcNow);

    /// <summary>The site's shares, live ones first, for the people who manage it.</summary>
    public async Task<IReadOnlyList<SiteGuestShareView>> ListAsync(Guid siteId, CancellationToken ct)
    {
        await ManagedAsync(siteId, false, ct);
        var rows = await session.QueryAsync(Select + " where site_id = @siteId order by (revoked_at is null and expires_at > @now) desc, created_at desc limit 200",
            new { siteId, now = clock.UtcNow }, Read, ct);
        var list = new List<SiteGuestShareView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    public async Task<SiteGuestShareView> CreateAsync(Guid siteId, SiteGuestShareRequest request, CancellationToken ct)
    {
        var site = await ManagedAsync(siteId, true, ct);
        if (!site.Active)
        {
            throw AppException.Conflict("conflict.content.site_archived");
        }
        var errors = new ValidationErrors();
        var days = request.Days ?? DefaultDays;
        if (days is < 1 or > MaxDays)
        {
            errors.Add("days", "validation.content.share_days");
        }
        // Shared into a space the person owns, so the people reached are the ones the space's owners invited.
        var space = request.SpaceId is { } s ? await spaces.ForViewerAsync(s, ct) : null;
        if (space is not { Owns: true, Active: true })
        {
            errors.Add("spaceId", "validation.content.share_space");
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        if (await session.ScalarAsync<bool>(
                "select exists (select 1 from content.site_guest_share where site_id = @siteId and space_id = @space and revoked_at is null and expires_at > @now)",
                new { siteId, space = space!.SpaceId, now }, ct))
        {
            throw AppException.Conflict("conflict.content.already_shared");
        }
        var id = Ids.New();
        var expires = now.AddDays(days);
        await session.ExecAsync(
            """
            insert into content.site_guest_share (tenant_id, id, site_id, space_id, expires_at, created_at, created_by)
            values (platform.current_tenant_id(), @id, @siteId, @space, @expires, @now, @by)
            """, new { id, siteId, space = space.SpaceId, expires, now, by = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SiteSharedWithGuests, "content_site", siteId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { share = id, space = space.SpaceId, expiresAt = expires }), ct);
        return await ViewAsync((await session.QuerySingleOrDefaultAsync(Select + " where id = @id", new { id }, Read, ct))!, ct);
    }

    /// <summary>Ends a share at once. Needs managing the site; revoking twice is a conflict.</summary>
    public async Task<SiteGuestShareView> RevokeAsync(Guid shareId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var row = await session.QuerySingleOrDefaultAsync(Select + " where id = @shareId for update", new { shareId }, Read, ct) ?? throw AppException.NotFound();
        if (await sites.RowAsync(row.Site, false, ct) is not { } site || !await sites.ManagesAsync(site, ct))
        {
            throw AppException.NotFound();
        }
        if (row.RevokedAt is not null)
        {
            throw AppException.Conflict("conflict.content.share_revoked");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update content.site_guest_share set revoked_at = @now, revoked_by = @by where id = @shareId", new { shareId, now, by = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SiteGuestShareRevoked, "content_site", row.Site.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { share = shareId, space = row.Space }), ct);
        return await ViewAsync(row with { RevokedAt = now }, ct);
    }

    private async Task ReaderAsync(Guid spaceId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentGuestSites, ct);
        context.RequireUser();
        if (await spaces.ForViewerAsync(spaceId, ct) is not { Reads: true, Active: true })
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>The live shares of a space with active sites, for anyone who reads the space.</summary>
    public async Task<IReadOnlyList<SharedSiteView>> ForSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        await ReaderAsync(spaceId, ct);
        return await session.QueryAsync(
            """
            select g.id, g.site_id, s.names::text, g.expires_at from content.site_guest_share g join content.site s on s.id = g.site_id
            where g.space_id = @spaceId and g.revoked_at is null and g.expires_at > @now and s.status = 'active'
            order by g.created_at, g.id
            """, new { spaceId, now = clock.UtcNow },
            r => new SharedSiteView(r.GetGuid(0), r.GetGuid(1), ContentJson.Read<Dictionary<string, string>>(r.GetString(2)), r.GetFieldValue<DateTimeOffset>(3)), ct);
    }

    private async Task<SharedSiteView> SharedAsync(Guid spaceId, Guid siteId, CancellationToken ct) =>
        (await ForSpaceAsync(spaceId, ct)).FirstOrDefault(s => s.SiteId == siteId) ?? throw AppException.NotFound();

    private (string Locale, ContentVariant Variant)? Pick(Dictionary<string, ContentVariant> variants, string primary)
    {
        var wanted = locale.Locale;
        if (variants.TryGetValue(wanted, out var v))
        {
            return (wanted, v);
        }
        if (variants.TryGetValue(primary, out var p))
        {
            return (primary, p);
        }
        return variants.Count > 0 ? (variants.Keys.First(), variants.Values.First()) : null;
    }

    /// <summary>The site's published Public and Internal items, newest first.</summary>
    public async Task<IReadOnlyList<GuestItemSummary>> ItemsAsync(Guid spaceId, Guid siteId, CancellationToken ct)
    {
        await SharedAsync(spaceId, siteId, ct);
        var rows = await session.QueryAsync(
            """
            select i.id, i.type, i.primary_locale, v.variants::text, i.published_at
            from content.content_item i join content.content_version v on v.item_id = i.id and v.number = i.published_version
            where i.site_id = @siteId and i.status = 'published' and i.classification = any(@readable)
            order by i.published_at desc, i.id limit @limit
            """, new { siteId, readable = Readable, limit = MaxItems },
            r => (Id: r.GetGuid(0), Type: r.GetString(1), Primary: r.GetString(2), Variants: r.GetString(3), At: r.GetFieldValue<DateTimeOffset>(4)), ct);
        var list = new List<GuestItemSummary>();
        foreach (var row in rows)
        {
            if (Pick(ContentJson.Read<Dictionary<string, ContentVariant>>(row.Variants), row.Primary) is { } chosen)
            {
                list.Add(new GuestItemSummary(row.Id, row.Type, chosen.Locale, chosen.Variant.Title, chosen.Variant.Summary, row.At));
            }
        }
        return list;
    }

    /// <summary>One published Public or Internal item of the shared site; anything else is not found.</summary>
    public async Task<GuestItemView> ItemAsync(Guid spaceId, Guid siteId, Guid itemId, CancellationToken ct)
    {
        var shared = await SharedAsync(spaceId, siteId, ct);
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select i.type, i.primary_locale, v.variants::text, i.published_at
            from content.content_item i join content.content_version v on v.item_id = i.id and v.number = i.published_version
            where i.id = @itemId and i.site_id = @siteId and i.status = 'published' and i.classification = any(@readable)
            """, new { itemId, siteId, readable = Readable },
            r => (Type: r.GetString(0), Primary: r.GetString(1), Variants: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        if (row.Type is null || Pick(ContentJson.Read<Dictionary<string, ContentVariant>>(row.Variants), row.Primary) is not { } chosen)
        {
            throw AppException.NotFound();
        }
        return new GuestItemView(itemId, siteId, shared.Names, row.Type, chosen.Locale, chosen.Variant.Title, chosen.Variant.Summary, chosen.Variant.Body, row.At);
    }
}
