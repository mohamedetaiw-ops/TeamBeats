using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public sealed record ScreenRequest(string? Name);

public sealed record ScreenView(Guid Id, Guid SiteId, string Name, DateTimeOffset CreatedAt, DateTimeOffset? RotatedAt);

/// <summary>A screen with its address. The address is shown only when the screen is created or its token rotated.</summary>
public sealed record ScreenWithAddress(ScreenView Screen, string Path);

public sealed record SignageItem(Guid Id, string Locale, string Title, string? Summary, DateTimeOffset PublishedAt);

public sealed record SignageFeed(string Name, IReadOnlyDictionary<string, string> SiteNames, IReadOnlyList<SignageItem> Items);

/// <summary>
/// Digital signage (spec 009 section 4, R2 default: a read-only feed). A site owner names a screen and gets a signed
/// address for it; the screen shows the site's live items that are Public and meant for everyone, nothing else, without
/// signing in. The token is kept only as a hash; rotating it, or removing the screen, stops the old address.
/// </summary>
public sealed class ContentSignage(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    ITenantSettingsReader tenantSettings,
    Items items,
    Sites sites,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxScreensPerSite = 50;
    public const int FeedSize = 20;

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string PathOf(string token) => $"/signage/{token}";

    private async Task<SiteRow> ManagedSiteAsync(Guid siteId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        if (await sites.RowAsync(siteId, false, ct) is not { } site || !await sites.ManagesAsync(site, ct))
        {
            throw AppException.NotFound();
        }
        return site;
    }

    private static ScreenView Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.Get<DateTimeOffset?>("rotated_at"));

    private const string Columns = "id, site_id, name, created_at, rotated_at";

    public async Task<IReadOnlyList<ScreenView>> ListAsync(Guid siteId, CancellationToken ct)
    {
        var site = await ManagedSiteAsync(siteId, ct);
        return await session.QueryAsync($"select {Columns} from content.signage_screen where site_id = @site order by name, id", new { site = site.Id }, Map, ct);
    }

    public async Task<ScreenWithAddress> CreateAsync(Guid siteId, ScreenRequest request, CancellationToken ct)
    {
        var site = await ManagedSiteAsync(siteId, ct);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            throw AppException.Validation("name", "validation.content.screen_name");
        }
        if (!site.Active)
        {
            throw AppException.Conflict("conflict.content.site_archived");
        }
        if (await session.ScalarAsync<int>("select count(*)::int from content.signage_screen where site_id = @site", new { site = site.Id }, ct) >= MaxScreensPerSite)
        {
            throw AppException.Conflict("conflict.content.screen_limit");
        }
        var id = Ids.New();
        var token = NewToken();
        await session.ExecAsync(
            """
            insert into content.signage_screen (tenant_id, id, site_id, name, token_hash, created_by, created_at)
            values (platform.current_tenant_id(), @id, @site, @name, @hash, @user, @now)
            """, new { id, site = site.Id, name, hash = Hash(token), user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentScreenCreated, "content_screen", id.ToString(), TargetOrgUnitId: site.OrgUnitId, Changes: new { name }), ct);
        return new ScreenWithAddress(await ScreenAsync(id, ct), PathOf(token));
    }

    private async Task<ScreenView> ScreenAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from content.signage_screen where id = @id", new { id }, Map, ct)).Single();

    private async Task<(ScreenView Screen, SiteRow Site)> ManagedScreenAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var screen = (await session.QueryAsync($"select {Columns} from content.signage_screen where id = @id for update", new { id }, Map, ct)).FirstOrDefault()
            ?? throw AppException.NotFound();
        return (screen, await ManagedSiteAsync(screen.SiteId, ct));
    }

    public async Task<ScreenWithAddress> RotateAsync(Guid id, CancellationToken ct)
    {
        var (screen, site) = await ManagedScreenAsync(id, ct);
        var token = NewToken();
        await session.ExecAsync("update content.signage_screen set token_hash = @hash, rotated_at = @now where id = @id",
            new { id, hash = Hash(token), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentScreenRotated, "content_screen", id.ToString(), TargetOrgUnitId: site.OrgUnitId), ct);
        return new ScreenWithAddress(await ScreenAsync(screen.Id, ct), PathOf(token));
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var (_, site) = await ManagedScreenAsync(id, ct);
        await session.ExecAsync("delete from content.signage_screen where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentScreenRemoved, "content_screen", id.ToString(), TargetOrgUnitId: site.OrgUnitId), ct);
    }

    /// <summary>
    /// The feed of a screen, read without a person: the tenant comes from the host, the token finds the screen. An unknown
    /// or rotated token, an archived site and a plan without content all read as not found.
    /// </summary>
    public async Task<SignageFeed> FeedAsync(string token, CancellationToken ct)
    {
        if (token.Length is < 20 or > 100 || !await entitlements.IsEnabledAsync(Capabilities.ContentCore, ct))
        {
            throw AppException.NotFound();
        }
        var screen = (await session.QueryAsync($"select {Columns} from content.signage_screen where token_hash = @hash", new { hash = Hash(token) }, Map, ct)).FirstOrDefault();
        if (screen is null || await sites.RowAsync(screen.SiteId, false, ct) is not { Active: true } site)
        {
            throw AppException.NotFound();
        }
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var rows = await session.QueryAsync(
            $"""
            select {Items.Columns} from content.content_item
            where site_id = @site and status = 'published' and classification = 'public' and published_version is not null
            order by pinned desc, published_at desc, id limit @limit
            """, new { site = site.Id, limit = FeedSize * 2 }, Items.Read, ct);
        var list = new List<SignageItem>();
        foreach (var item in rows.Where(i => i.Audience.IsEveryone).Take(FeedSize))
        {
            if (await items.VersionAsync(item.Id, item.PublishedVersion!.Value, ct) is not { } version)
            {
                continue;
            }
            var (locale, variant) = Items.Pick(version.Variants, locales.DefaultLocale, locales.DefaultLocale, item.PrimaryLocale);
            list.Add(new SignageItem(item.Id, locale, variant.Title, variant.Summary, item.PublishedAt ?? version.CreatedAt));
        }
        return new SignageFeed(screen.Name, site.Names, list);
    }
}
