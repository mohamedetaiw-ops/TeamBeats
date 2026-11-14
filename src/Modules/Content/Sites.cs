using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public sealed record SiteView(
    Guid Id,
    IReadOnlyDictionary<string, string> Names,
    Guid? OrgUnitId,
    string TimeZone,
    ContentAudience DefaultAudience,
    string CommentsMode,
    bool Moderation,
    bool IsDefault,
    string Status,
    int Version,
    IReadOnlyDictionary<string, ContentRoute> Routes,
    Guid? HomePageId = null,
    string? HomeSlug = null,
    string? TemplateKey = null,
    ContentAudience? SharedAudience = null,
    DateTimeOffset? ArchivedAt = null,
    bool CanManage = false);

public sealed record SiteRequest(
    IReadOnlyDictionary<string, string>? Names,
    Guid? OrgUnitId,
    string? TimeZone,
    ContentAudience? DefaultAudience,
    string? CommentsMode,
    bool? Moderation,
    int? Version,
    IReadOnlyDictionary<string, ContentRoute>? Routes = null,
    string? Template = null,
    string? HomeSlug = null,
    Guid? HomePageId = null,
    ContentAudience? SharedAudience = null,
    bool? Unshare = null);

/// <summary>A site as a reader's navigation lists it (US6 scenario 2): its names and the path of its home page.</summary>
public sealed record SiteLink(Guid Id, IReadOnlyDictionary<string, string> Names, string? HomeSlug);

/// <summary>
/// A site template (US6 scenario 1): versioned product configuration that sets the home page template of spec 032, the
/// comment and moderation defaults, and whether news needs approval. The tenant edits the resulting site freely.
/// </summary>
public sealed record SiteTemplate(string Key, string PageTemplate, string CommentsMode, bool Moderation, bool NewsApproval);

public static class SiteTemplates
{
    public static readonly IReadOnlyList<SiteTemplate> All =
    [
        new("subsidiary", "T05", "on", false, true),
        new("location", "T04", "on", false, false),
        new("brand", "T06", "on", true, true),
    ];

    public static SiteTemplate? Find(string? key) => All.FirstOrDefault(t => t.Key == key);
}

public sealed record ContentSettingsView(IReadOnlyList<string> EmbedHosts, IReadOnlyList<ContentTypeSettings> Types);

public sealed record ContentSettingsRequest(IReadOnlyList<string>? EmbedHosts, IReadOnlyList<ContentTypeSettings>? Types);

internal sealed record SiteRow(Guid Id, IReadOnlyDictionary<string, string> Names, Guid? OrgUnitId, string TimeZone, ContentAudience DefaultAudience,
    string RoutesJson, string CommentsMode, bool Moderation, bool IsDefault, string Status, int Version, Guid? HomePageId = null, string? TemplateKey = null,
    ContentAudience? SharedAudience = null, DateTimeOffset? ArchivedAt = null)
{
    public bool Active => Status == "active";

    public AuthzTarget Target => OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    public IReadOnlyDictionary<string, ContentRoute> Routes { get; } = ContentJson.Read<Dictionary<string, ContentRoute>>(RoutesJson);

    /// <summary>The route of a content type; none means publishers publish directly.</summary>
    public ContentRoute RouteOf(string type) => Routes.TryGetValue(type, out var r) ? r : new ContentRoute(false);

    public SiteView View(string? homeSlug = null, bool canManage = false) => new(Id, Names, OrgUnitId, TimeZone, DefaultAudience, CommentsMode, Moderation,
        IsDefault, Status, Version, Routes, HomePageId, homeSlug, TemplateKey, SharedAudience, ArchivedAt, canManage);
}

/// <summary>
/// Sites of R1 (spec 009 plan "Sites in R1"): containers with names, an org unit subtree, a default audience and a time
/// zone, where authoring roles are granted. Each tenant gets the site "Intranet" for the whole tenant on first use.
/// Communications administrators (<c>content.admin</c>) create sites up to <c>content.max_sites</c>; site homes,
/// navigation and themes arrive with US6.
/// </summary>
public sealed class Sites(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    ITenantMemberships memberships,
    ILocalizer localizer,
    IAuditWriter audit,
    IClock clock,
    Experience.Contracts.ISitePages pages,
    AudienceFactsLoader facts)
{
    public const int MaxEmbedHosts = 50;

    private const string Columns = "id, names::text, org_unit_id, time_zone, default_audience::text, routes::text, comments_mode, moderation, is_default, status, row_version, " +
        "home_page_id, template_key, shared_audience::text, archived_at";

    private static SiteRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), ContentJson.Read<Dictionary<string, string>>(r.GetString(1)), r.Get<Guid?>("org_unit_id"),
        r.GetString(3), ContentJson.Read<ContentAudience>(r.GetString(4)), r.GetString(5), r.GetString(6), r.GetBoolean(7), r.GetBoolean(8), r.GetString(9), r.GetInt32(10),
        r.IsDBNull(11) ? null : r.GetGuid(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : ContentJson.Read<ContentAudience>(r.GetString(13)),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14));

    internal Task<SiteRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from content.site where id = @id" + (forUpdate ? " for update" : ""), new { id }, Read, ct);

    /// <summary>The tenant's default site, created the first time anyone needs it.</summary>
    internal async Task<SiteRow> DefaultAsync(CancellationToken ct)
    {
        if (await session.QuerySingleOrDefaultAsync($"select {Columns} from content.site where is_default", null, Read, ct) is { } site)
        {
            return site;
        }
        var locales = await tenantSettings.GetLocaleSettingsAsync(ct);
        var names = locales.EnabledLocales.ToDictionary(l => l, l => localizer.Get("content.site.default_name", l));
        await session.ExecAsync(
            """
            insert into content.site (tenant_id, id, names, org_unit_id, time_zone, is_default)
            values (platform.current_tenant_id(), @id, @names, null, @tz, true)
            on conflict (tenant_id) where is_default do nothing
            """, new { id = Ids.New(), names = new Jsonb(ContentJson.Write(names)), tz = locales.TimeZone }, ct);
        return (await session.QuerySingleOrDefaultAsync($"select {Columns} from content.site where is_default", null, Read, ct))!;
    }

    /// <summary>Whether the caller administers the site: <c>content.admin</c> at its scope, which a site owner holds at its unit (US6 scenario 4).</summary>
    private async Task<bool> ManagesAsync(SiteRow site, CancellationToken ct) => (await authz.CheckAsync(Permissions.ContentAdmin, site.Target, ct)).Allowed;

    private async Task<SiteView> ViewAsync(SiteRow row, bool canManage, CancellationToken ct) =>
        row.View(row.HomePageId is { } page ? (await pages.FindAsync(page, ct))?.Slug : null, canManage);

    /// <summary>
    /// Sites where the caller writes, edits, publishes or administers content, the default site first. Archived sites are
    /// listed to those who administer them, so they can be restored.
    /// </summary>
    public async Task<IReadOnlyList<SiteView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        await DefaultAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from content.site order by is_default desc, status, created_at", null, Read, ct);
        var mine = new List<SiteView>();
        foreach (var row in rows)
        {
            var manages = await ManagesAsync(row, ct);
            if (manages || (row.Active && await WorksInAsync(row, ct)))
            {
                mine.Add(await ViewAsync(row, manages, ct));
            }
        }
        return mine;
    }

    /// <summary>
    /// The sites a reader finds in their navigation (US6 scenario 2): active sites other than the tenant's default whose
    /// default audience, or the audience they are shared with, includes the reader. Archived sites are left out.
    /// </summary>
    public async Task<IReadOnlyList<SiteLink>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        var me = await facts.ForAsync(user, ct);
        var rows = await session.QueryAsync($"select {Columns} from content.site where status = 'active' and not is_default order by created_at", null, Read, ct);
        var links = new List<SiteLink>();
        foreach (var row in rows)
        {
            if (AudienceRules.Matches(row.DefaultAudience, me) || (row.SharedAudience is { } shared && AudienceRules.Matches(shared, me)))
            {
                var home = row.HomePageId is { } page ? await pages.FindAsync(page, ct) : null;
                links.Add(new SiteLink(row.Id, row.Names, home is { Published: true } ? home.Slug : null));
            }
        }
        return links;
    }

    private async Task<bool> WorksInAsync(SiteRow site, CancellationToken ct)
    {
        foreach (var p in new[] { Permissions.ContentAuthor, Permissions.ContentEdit, Permissions.ContentPublish })
        {
            if ((await authz.CheckAsync(p, site.Target, ct)).Allowed)
            {
                return true;
            }
        }
        return false;
    }

    public async Task<SiteView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var manages = await ManagesAsync(row, ct);
        if (!manages && (!row.Active || !await WorksInAsync(row, ct)))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, manages, ct);
    }

    private async Task ValidateAsync(SiteRequest request, bool create, ValidationErrors errors, CancellationToken ct)
    {
        var locales = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        if (request.Names is null ? create : request.Names.Count == 0 || request.Names.Any(n => !locales.Contains(n.Key) || string.IsNullOrWhiteSpace(n.Value)
            || n.Value.Trim().Length > ContentLimits.SiteNameMax))
        {
            errors.Add("names", "validation.content.site_names");
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.content.site_unit");
        }
        if (request.TimeZone is { } tz && !IsTimeZone(tz))
        {
            errors.Add("timeZone", "validation.content.time_zone");
        }
        if (request.CommentsMode is { } mode && mode is not ("on" or "off"))
        {
            errors.Add("commentsMode", "validation.content.comments_mode");
        }
        AudienceRules.Validate(request.DefaultAudience, "defaultAudience", errors);
        AudienceRules.Validate(request.SharedAudience, "sharedAudience", errors);
        if (request.Template is not null && (!create || SiteTemplates.Find(request.Template) is null))
        {
            errors.Add("template", "validation.content.site_template");
        }
        if (request.Template is not null && request.HomePageId is not null)
        {
            errors.Add("homePageId", "validation.content.site_home");
        }
        foreach (var (type, route) in request.Routes ?? new Dictionary<string, ContentRoute>())
        {
            if (!ContentTypes.Defaults.ContainsKey(type) || route is null || route.Approvers is { Count: > ContentRoute.MaxApprovers }
                || (route.Approvers is { Count: > 0 } && !route.Approval))
            {
                errors.Add("routes", "validation.content.route");
                continue;
            }
            foreach (var approver in route.Approvers ?? [])
            {
                if ((await memberships.GetAsync(approver, ct))?.Status != MembershipStatus.Active)
                {
                    errors.Add("routes", "validation.content.route_approver");
                    break;
                }
            }
        }
    }

    private static string RoutesJson(IReadOnlyDictionary<string, ContentRoute>? routes) =>
        ContentJson.Write((routes ?? new Dictionary<string, ContentRoute>()).ToDictionary(r => r.Key, r => r.Value with { Approvers = r.Value.Approvers?.Distinct().ToList() }));

    internal static bool IsTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return id.Length <= 100;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string> names) => names.ToDictionary(n => n.Key, n => n.Value.Trim());

    /// <summary>A home page must be a spec 032 page owned at the site's own scope, so the site owner can edit it and it uses the site's theme.</summary>
    private async Task ValidateHomeAsync(Guid? pageId, Guid? unit, CancellationToken ct)
    {
        if (pageId is { } id && (await pages.FindAsync(id, ct) is not { } page || page.OrgUnitId != unit))
        {
            throw AppException.Validation("homePageId", "validation.content.site_home");
        }
    }

    private async Task DemandRoomAsync(CancellationToken ct)
    {
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':content.sites', 0))", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from content.site where status = 'active'", null, ct);
        if (await entitlements.GetLimitAsync(Capabilities.ContentMaxSites, ct) is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.content.site_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.ContentMaxSites, ["limit"] = max });
        }
    }

    /// <summary>
    /// Creates a site (US6 scenario 1). From a template, the site takes the template's comment, moderation and approval
    /// defaults, and its home page is created in the page builder at the site's unit when the plan has the builder; the
    /// page builder's own permission applies to the caller. A site linked to a unit defaults its audience to that subtree.
    /// </summary>
    public async Task<SiteView> CreateAsync(SiteRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await authz.DemandAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct);
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        await ValidateAsync(request, create: true, errors, ct);
        errors.ThrowIfAny();
        await ValidateHomeAsync(request.HomePageId, request.OrgUnitId, ct);
        await DefaultAsync(ct);
        await DemandRoomAsync(ct);

        var template = SiteTemplates.Find(request.Template);
        var id = Ids.New();
        var tz = request.TimeZone ?? (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
        var audience = request.DefaultAudience
            ?? (request.OrgUnitId is { } unit ? new ContentAudience([new AudienceClause(OrgUnits: [new UnitTarget(unit)])]) : ContentAudience.Everyone);
        var routes = request.Routes ?? (template is { NewsApproval: true }
            ? new Dictionary<string, ContentRoute> { [ContentTypes.News] = new(true), [ContentTypes.Announcement] = new(true) }
            : null);
        var home = request.HomePageId;
        if (template is not null && await entitlements.IsEnabledAsync(Capabilities.ExperiencePageBuilder, ct))
        {
            var slug = request.HomeSlug ?? $"sites/{id.ToString("N")[..8]}";
            home = (await pages.CreateAsync(template.PageTemplate, slug, request.OrgUnitId, ct)).Id;
        }
        await session.ExecAsync(
            """
            insert into content.site (tenant_id, id, names, org_unit_id, time_zone, default_audience, routes, comments_mode, moderation, created_by, created_at, updated_at,
              home_page_id, template_key, shared_audience)
            values (platform.current_tenant_id(), @id, @names, @unit, @tz, @audience, @routes, @mode, @moderation, @user, @now, @now, @home, @template, @shared)
            """, new
            {
                id, names = new Jsonb(ContentJson.Write(Clean(request.Names!))), unit = request.OrgUnitId, tz,
                audience = new Jsonb(ContentJson.Write(audience)), routes = new Jsonb(RoutesJson(routes)),
                mode = request.CommentsMode ?? template?.CommentsMode ?? "on",
                moderation = request.Moderation ?? template?.Moderation ?? false, user, now = clock.UtcNow, home, template = template?.Key,
                shared = request.SharedAudience is { IsEveryone: false } shared ? new Jsonb(ContentJson.Write(shared)) : (Jsonb?)null,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SiteCreated, "content_site", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { orgUnitId = request.OrgUnitId, tz, template = template?.Key, homePageId = home }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    /// <summary>
    /// Changes a site. Its owner (<c>content.admin</c> at the site's unit) may change it, but moving it to another unit
    /// also needs that permission at the new unit, so an owner cannot move a site out of their reach or into another's.
    /// </summary>
    public async Task<SiteView> UpdateAsync(Guid id, SiteRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (!await ManagesAsync(row, ct))
        {
            throw await WorksInAsync(row, ct) ? AppException.Denied() : AppException.NotFound();
        }
        if (!row.Active)
        {
            throw AppException.Conflict("conflict.content.site_archived");
        }
        if (request.Version is { } expected && expected != row.Version)
        {
            throw AppException.Conflict("conflict.content.changed");
        }
        var errors = new ValidationErrors();
        await ValidateAsync(request, create: false, errors, ct);
        if (row.IsDefault && request.OrgUnitId is not null)
        {
            // The default site covers the whole tenant.
            errors.Add("orgUnitId", "validation.content.site_unit");
        }
        errors.ThrowIfAny();
        var unit = row.IsDefault ? null : request.OrgUnitId ?? row.OrgUnitId;
        if (unit != row.OrgUnitId)
        {
            await authz.DemandAsync(Permissions.ContentAdmin, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);
        }
        var home = request.HomePageId ?? row.HomePageId;
        if (request.HomePageId is not null || unit != row.OrgUnitId)
        {
            await ValidateHomeAsync(home, unit, ct);
        }
        var names = request.Names is null ? row.Names : Clean(request.Names);
        var shared = request.Unshare == true ? null : request.SharedAudience ?? row.SharedAudience;
        await session.ExecAsync(
            """
            update content.site set names = @names, org_unit_id = @unit, time_zone = @tz, default_audience = @audience, routes = @routes, comments_mode = @mode,
              moderation = @moderation, home_page_id = @home, shared_audience = @shared, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, names = new Jsonb(ContentJson.Write(names)), unit, tz = request.TimeZone ?? row.TimeZone,
                audience = new Jsonb(ContentJson.Write(request.DefaultAudience ?? row.DefaultAudience)),
                routes = new Jsonb(request.Routes is null ? row.RoutesJson : RoutesJson(request.Routes)), mode = request.CommentsMode ?? row.CommentsMode,
                moderation = request.Moderation ?? row.Moderation, home,
                shared = shared is { IsEveryone: false } a ? new Jsonb(ContentJson.Write(a)) : (Jsonb?)null, now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SiteChanged, "content_site", id.ToString(), TargetOrgUnitId: unit,
            Changes: new
            {
                before = new { row.OrgUnitId, row.TimeZone, row.Routes, row.HomePageId, row.SharedAudience },
                after = new { orgUnitId = unit, timeZone = request.TimeZone ?? row.TimeZone, routes = request.Routes ?? row.Routes, homePageId = home, sharedAudience = shared },
            }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    /// <summary>
    /// Archives a site (US6 scenario 5): its items become read-only and leave feeds, and the site leaves navigation;
    /// readers with permission still open items by link. The tenant's default site cannot be archived.
    /// </summary>
    public async Task<SiteView> ArchiveAsync(Guid id, bool archive, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (!await ManagesAsync(row, ct))
        {
            throw row.Active && await WorksInAsync(row, ct) ? AppException.Denied() : AppException.NotFound();
        }
        if (row.IsDefault)
        {
            throw AppException.Conflict("conflict.content.site_default");
        }
        if (archive == !row.Active)
        {
            return await ViewAsync(row, true, ct);
        }
        if (!archive)
        {
            await DemandRoomAsync(ct);
        }
        var user = context.RequireUser();
        await session.ExecAsync(
            """
            update content.site set status = @status, archived_at = @at, archived_by = @by, updated_at = @now, row_version = row_version + 1 where id = @id
            """, new
            {
                id, status = archive ? "archived" : "active", at = archive ? clock.UtcNow : (DateTimeOffset?)null, by = archive ? user : (Guid?)null, now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(archive ? AuditActions.SiteArchived : AuditActions.SiteRestored, "content_site", id.ToString(), TargetOrgUnitId: row.OrgUnitId), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    // ---- Tenant content settings ----

    internal async Task<IReadOnlySet<string>> EmbedHostsAsync(CancellationToken ct) =>
        (await session.ScalarAsync<string[]>("select embed_hosts from content.settings", null, ct) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal async Task<ContentTypeSettings?> TypeAsync(string key, CancellationToken ct)
    {
        if (!ContentTypes.Defaults.TryGetValue(key, out var defaults))
        {
            return null;
        }
        return await session.QuerySingleOrDefaultAsync(
            "select requires_owner, review_months, show_history_to_readers, under_review_label from content.content_type where key = @key", new { key },
            r => new ContentTypeSettings(key, r.GetBoolean(0), r.Get<int?>("review_months"), r.GetBoolean(2), r.GetBoolean(3)), ct) ?? defaults;
    }

    public async Task<ContentSettingsView> SettingsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await authz.DemandAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct);
        var types = new List<ContentTypeSettings>();
        foreach (var key in ContentTypes.Defaults.Keys)
        {
            types.Add((await TypeAsync(key, ct))!);
        }
        return new ContentSettingsView((await EmbedHostsAsync(ct)).Order(StringComparer.Ordinal).ToList(), types);
    }

    public async Task<ContentSettingsView> UpdateSettingsAsync(ContentSettingsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await authz.DemandAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct);
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        var hosts = request.EmbedHosts?.Select(h => h?.Trim().ToLowerInvariant() ?? string.Empty).Distinct().ToArray();
        if (hosts is not null && (hosts.Length > MaxEmbedHosts || hosts.Any(h => !IsHostName(h))))
        {
            errors.Add("embedHosts", "validation.content.embed_host");
        }
        if (request.Types?.Any(t => t is null || !ContentTypes.Defaults.ContainsKey(t.Key ?? string.Empty) || t.ReviewMonths is < 1 or > 60) == true)
        {
            errors.Add("types", "validation.content.type_settings");
        }
        errors.ThrowIfAny();
        var before = await SettingsAsync(ct);
        var now = clock.UtcNow;
        if (hosts is not null)
        {
            await session.ExecAsync(
                """
                insert into content.settings (tenant_id, embed_hosts, updated_at, updated_by) values (platform.current_tenant_id(), @hosts, @now, @user)
                on conflict (tenant_id) do update set embed_hosts = excluded.embed_hosts, updated_at = excluded.updated_at, updated_by = excluded.updated_by
                """, new { hosts, now, user }, ct);
        }
        foreach (var t in request.Types ?? [])
        {
            await session.ExecAsync(
                """
                insert into content.content_type (tenant_id, key, requires_owner, review_months, show_history_to_readers, under_review_label, updated_at)
                values (platform.current_tenant_id(), @key, @owner, @months, @history, @label, @now)
                on conflict (tenant_id, key) do update set requires_owner = excluded.requires_owner, review_months = excluded.review_months,
                  show_history_to_readers = excluded.show_history_to_readers, under_review_label = excluded.under_review_label, updated_at = excluded.updated_at
                """, new { key = t.Key, owner = t.RequiresOwner, months = t.ReviewMonths, history = t.ShowHistoryToReaders, label = t.UnderReviewLabel, now }, ct);
        }
        var after = await SettingsAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentSettingsChanged, "content_settings", null, Changes: new { before, after }), ct);
        return after;
    }

    /// <summary>A bare host name: letters, digits and hyphens in dot-separated labels, at least two labels, no port or path.</summary>
    internal static bool IsHostName(string host) =>
        host.Length is > 3 and <= 253 && Uri.CheckHostName(host) == UriHostNameType.Dns && host.Contains('.')
        && host.Split('.').All(l => l.Length is > 0 and <= 63 && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && l[0] != '-' && l[^1] != '-');
}

/// <summary>The site contract for other modules: active sites only, in the current tenant.</summary>
public sealed class SiteDirectory(Sites sites) : Contracts.ISiteDirectory
{
    public async Task<Contracts.SiteScope?> FindAsync(Guid siteId, CancellationToken ct = default) =>
        await sites.RowAsync(siteId, false, ct) is { Status: "active" } row ? new Contracts.SiteScope(row.Id, row.OrgUnitId) : null;
}
