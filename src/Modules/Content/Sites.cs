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
    int Version);

public sealed record SiteRequest(
    IReadOnlyDictionary<string, string>? Names,
    Guid? OrgUnitId,
    string? TimeZone,
    ContentAudience? DefaultAudience,
    string? CommentsMode,
    bool? Moderation,
    int? Version);

public sealed record ContentSettingsView(IReadOnlyList<string> EmbedHosts, IReadOnlyList<ContentTypeSettings> Types);

public sealed record ContentSettingsRequest(IReadOnlyList<string>? EmbedHosts, IReadOnlyList<ContentTypeSettings>? Types);

internal sealed record SiteRow(Guid Id, IReadOnlyDictionary<string, string> Names, Guid? OrgUnitId, string TimeZone, ContentAudience DefaultAudience,
    string RoutesJson, string CommentsMode, bool Moderation, bool IsDefault, string Status, int Version)
{
    public AuthzTarget Target => OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    public SiteView View() => new(Id, Names, OrgUnitId, TimeZone, DefaultAudience, CommentsMode, Moderation, IsDefault, Status, Version);
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
    ILocalizer localizer,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxEmbedHosts = 50;

    private const string Columns = "id, names::text, org_unit_id, time_zone, default_audience::text, routes::text, comments_mode, moderation, is_default, status, row_version";

    private static SiteRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), ContentJson.Read<Dictionary<string, string>>(r.GetString(1)), r.Get<Guid?>("org_unit_id"),
        r.GetString(3), ContentJson.Read<ContentAudience>(r.GetString(4)), r.GetString(5), r.GetString(6), r.GetBoolean(7), r.GetBoolean(8), r.GetString(9), r.GetInt32(10));

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

    /// <summary>Sites where the caller writes, edits, publishes or administers content, the default site first.</summary>
    public async Task<IReadOnlyList<SiteView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        await DefaultAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from content.site where status = 'active' order by is_default desc, created_at", null, Read, ct);
        var admin = (await authz.CheckAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct)).Allowed;
        var mine = new List<SiteView>();
        foreach (var row in rows)
        {
            if (admin || await WorksInAsync(row, ct))
            {
                mine.Add(row.View());
            }
        }
        return mine;
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
        if (!(await authz.CheckAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct)).Allowed && !await WorksInAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        return row.View();
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
    }

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

    public async Task<SiteView> CreateAsync(SiteRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await authz.DemandAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct);
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        await ValidateAsync(request, create: true, errors, ct);
        errors.ThrowIfAny();
        await DefaultAsync(ct);

        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':content.sites', 0))", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from content.site where status = 'active'", null, ct);
        if (await entitlements.GetLimitAsync(Capabilities.ContentMaxSites, ct) is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.content.site_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.ContentMaxSites, ["limit"] = max });
        }
        var id = Ids.New();
        var tz = request.TimeZone ?? (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
        await session.ExecAsync(
            """
            insert into content.site (tenant_id, id, names, org_unit_id, time_zone, default_audience, comments_mode, moderation, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @names, @unit, @tz, @audience, @mode, @moderation, @user, @now, @now)
            """, new
            {
                id, names = new Jsonb(ContentJson.Write(Clean(request.Names!))), unit = request.OrgUnitId, tz,
                audience = new Jsonb(ContentJson.Write(request.DefaultAudience ?? ContentAudience.Everyone)), mode = request.CommentsMode ?? "on",
                moderation = request.Moderation ?? false, user, now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SiteCreated, "content_site", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { orgUnitId = request.OrgUnitId, tz }), ct);
        return (await RowAsync(id, false, ct))!.View();
    }

    public async Task<SiteView> UpdateAsync(Guid id, SiteRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        await authz.DemandAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
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
        var names = request.Names is null ? row.Names : Clean(request.Names);
        var unit = row.IsDefault ? null : request.OrgUnitId ?? row.OrgUnitId;
        await session.ExecAsync(
            """
            update content.site set names = @names, org_unit_id = @unit, time_zone = @tz, default_audience = @audience, comments_mode = @mode,
              moderation = @moderation, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, names = new Jsonb(ContentJson.Write(names)), unit, tz = request.TimeZone ?? row.TimeZone,
                audience = new Jsonb(ContentJson.Write(request.DefaultAudience ?? row.DefaultAudience)), mode = request.CommentsMode ?? row.CommentsMode,
                moderation = request.Moderation ?? row.Moderation, now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SiteChanged, "content_site", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { before = new { row.OrgUnitId, row.TimeZone }, after = new { orgUnitId = unit, timeZone = request.TimeZone ?? row.TimeZone } }), ct);
        return (await RowAsync(id, false, ct))!.View();
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
