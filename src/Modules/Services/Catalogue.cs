using System.Text.Json;
using Npgsql;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

public sealed record CategoryView(Guid Id, IReadOnlyDictionary<string, string> Names, int Position);

/// <summary>A catalogue card. Linked apps carry their URL and whether sign-in is single sign-on (FR-002).</summary>
public sealed record ServiceCard(
    Guid Id,
    string Kind,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    Guid? CategoryId,
    string? Url,
    bool Sso,
    string? LaunchRoute,
    bool Favourite);

public sealed record CatalogueView(IReadOnlyList<CategoryView> Categories, IReadOnlyList<ServiceCard> Services, IReadOnlyList<Guid> Recent);

/// <summary>A service a person may use, with the published form they fill in.</summary>
public sealed record ServiceDetail(ServiceCard Service, int? Version, FormDefinition Form, bool AllowOnBehalf, int MaxAttachments, bool HasApproval, string Classification);

/// <summary>What the catalogue stores for one service; also the shape of the owner's view.</summary>
public sealed record ServiceRow(
    Guid Id,
    string Kind,
    string? ModuleKey,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Descriptions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Synonyms,
    Guid? CategoryId,
    ServiceAudience Audience,
    Guid? OwnerOrgUnitId,
    string Classification,
    string? Url,
    bool Sso,
    string? LaunchRoute,
    string Status,
    int? CurrentVersion,
    DateTimeOffset UpdatedAt);

/// <summary>The facts an audience is checked against (FR-001): units with their parents, groups and membership type.</summary>
public sealed record AudienceFacts(IReadOnlySet<Guid> Units, IReadOnlySet<Guid> Groups, string? MembershipType)
{
    public bool Matches(ServiceAudience a) =>
        (a.OrgUnits is not { Count: > 0 } || a.OrgUnits.Any(Units.Contains))
        && (a.Groups is not { Count: > 0 } || a.Groups.Any(Groups.Contains))
        && (a.MembershipTypes is not { Count: > 0 } || (MembershipType is { } t && a.MembershipTypes.Contains(t)));
}

/// <summary>
/// The service catalogue of spec 016 US1: published services whose audience includes the viewer, by category, with
/// search, favourites and recent use. Module services (FR-010) join the catalogue from their registrations.
/// </summary>
public sealed class Catalogue(
    IDbSession session,
    ITenantContext context,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IAuthorizationProvisioning groups,
    IEntitlementService entitlements,
    IEnumerable<IModuleServiceSource> moduleSources,
    IClock clock)
{
    public const int RecentCount = 8;
    private AudienceFacts? viewer;

    internal const string Columns =
        """
        id, kind, module_key, names::text, descriptions::text, synonyms::text, category_id, audience::text, owner_org_unit_id, classification,
        url, sso, launch_route, status, current_version, updated_at
        """;

    internal static ServiceRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.Get<string?>("module_key"),
        Json<Dictionary<string, string>>(r.GetString(3)), Json<Dictionary<string, string>>(r.GetString(4)),
        Json<Dictionary<string, IReadOnlyList<string>>>(r.GetString(5)), r.Get<Guid?>("category_id"), Json<ServiceAudience>(r.GetString(7)),
        r.Get<Guid?>("owner_org_unit_id"), r.GetString(9), r.Get<string?>("url"), r.GetBoolean(11), r.Get<string?>("launch_route"),
        r.GetString(13), r.Get<int?>("current_version"), r.GetFieldValue<DateTimeOffset>(15));

    internal static T Json<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options)!;

    public async Task<AudienceFacts> ViewerAsync(CancellationToken ct)
    {
        if (viewer is not null)
        {
            return viewer;
        }
        return viewer = await FactsAsync(context.RequireUser(), ct);
    }

    /// <summary>The audience facts of any member of the tenant, for example the person a request is filed for.</summary>
    public async Task<AudienceFacts> FactsAsync(Guid user, CancellationToken ct)
    {
        var units = new HashSet<Guid>();
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(user, ct))
        {
            units.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(m.OrgUnitId, ct));
        }
        return new AudienceFacts(units, await groups.GroupIdsOfAsync(user, ct), (await memberships.GetAsync(user, ct))?.MembershipType);
    }

    /// <summary>Adds module services that are not in the catalogue yet and keeps their names in step with the module.</summary>
    internal async Task SyncModuleServicesAsync(CancellationToken ct)
    {
        foreach (var registration in moduleSources.SelectMany(s => s.Services))
        {
            await session.ExecAsync(
                """
                insert into services.service (tenant_id, id, kind, module_key, names, descriptions, launch_route, status, created_at, updated_at)
                values (platform.current_tenant_id(), @id, 'module', @key, @names, @descriptions, @route, 'published', @now, @now)
                on conflict (tenant_id, module_key) where module_key is not null do update
                  set names = excluded.names, descriptions = excluded.descriptions, launch_route = excluded.launch_route
                  where services.service.names <> excluded.names or services.service.descriptions <> excluded.descriptions
                     or services.service.launch_route is distinct from excluded.launch_route
                """, new
                {
                    id = Ids.New(), key = registration.Key, names = Jsonb.From(registration.Names),
                    descriptions = Jsonb.From(registration.Descriptions ?? new Dictionary<string, string>()), route = registration.LaunchRoute, now = clock.UtcNow,
                }, ct);
        }
    }

    public async Task<IReadOnlyList<CategoryView>> CategoriesAsync(CancellationToken ct) =>
        await session.QueryAsync("select id, names::text, position from services.category order by position, id", null,
            r => new CategoryView(r.GetGuid(0), Json<Dictionary<string, string>>(r.GetString(1)), r.GetInt32(2)), ct);

    private static ServiceCard Card(ServiceRow s, bool favourite) =>
        new(s.Id, s.Kind, s.Names, s.Descriptions, s.CategoryId, s.Kind == ServiceKinds.Linked ? s.Url : null, s.Kind == ServiceKinds.Linked && s.Sso,
            s.Kind == ServiceKinds.Module ? s.LaunchRoute : null, favourite);

    public async Task<CatalogueView> ListAsync(string? query, Guid? categoryId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        await SyncModuleServicesAsync(ct);
        var facts = await ViewerAsync(ct);
        var favourites = (await session.QueryAsync("select service_id from services.favourite where user_id = @user", new { user }, r => r.GetGuid(0), ct)).ToHashSet();
        var visible = (await session.QueryAsync($"select {Columns} from services.service where status = 'published'", null, Read, ct))
            .Where(s => facts.Matches(s.Audience)).ToList();
        var terms = Search.Terms(query);
        var shown = visible
            .Where(s => categoryId is null || s.CategoryId == categoryId)
            .Where(s => terms.Count == 0 || Search.Matches(terms, s))
            .OrderBy(s => s.Names.Values.FirstOrDefault(), StringComparer.CurrentCulture)
            .Select(s => Card(s, favourites.Contains(s.Id))).ToList();
        var ids = visible.Select(s => s.Id).ToHashSet();
        var recent = (await session.QueryAsync("select service_id from services.recent_use where user_id = @user order by used_at desc limit 50", new { user },
            r => r.GetGuid(0), ct)).Where(ids.Contains).Take(RecentCount).ToList();
        return new CatalogueView(await CategoriesAsync(ct), shown, recent);
    }

    /// <summary>
    /// One service for the viewer. A service that exists but is not offered to them is refused with "not available to
    /// you" (US1 scenario 4); a draft, a retired service or one of another tenant is not found.
    /// </summary>
    public async Task<(ServiceRow Row, ServiceCard Card)> VisibleAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        await SyncModuleServicesAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync($"select {Columns} from services.service where id = @id and status = 'published'", new { id }, Read, ct)
            ?? throw AppException.NotFound();
        if (!(await ViewerAsync(ct)).Matches(row.Audience))
        {
            throw AppException.Denied("services.not_available");
        }
        var favourite = await session.ScalarAsync<bool>("select exists (select 1 from services.favourite where user_id = @user and service_id = @id)", new { user, id }, ct);
        return (row, Card(row, favourite));
    }

    public async Task<ServiceDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var (row, card) = await VisibleAsync(id, ct);
        if (row.CurrentVersion is not { } version)
        {
            return new ServiceDetail(card, null, FormDefinition.Empty, false, 0, false, row.Classification);
        }
        var v = await Versions.GetAsync(session, id, version, ct) ?? throw AppException.NotFound();
        return new ServiceDetail(card, version, v.Form, v.AllowOnBehalf, v.MaxAttachments, v.Route.Approval is not null, row.Classification);
    }

    public async Task SetFavouriteAsync(Guid id, bool favourite, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (favourite)
        {
            await VisibleAsync(id, ct);
            await session.ExecAsync(
                "insert into services.favourite (tenant_id, user_id, service_id, created_at) values (platform.current_tenant_id(), @user, @id, @now) on conflict do nothing",
                new { user, id, now = clock.UtcNow }, ct);
            return;
        }
        if (!await session.ScalarAsync<bool>("select exists (select 1 from services.service where id = @id)", new { id }, ct))
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync("delete from services.favourite where user_id = @user and service_id = @id", new { user, id }, ct);
    }

    /// <summary>Records that the viewer used a service (opened a linked app, submitted a request) for the recent list.</summary>
    public async Task MarkUsedAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        await VisibleAsync(id, ct);
        await session.ExecAsync(
            """
            insert into services.recent_use (tenant_id, user_id, service_id, used_at) values (platform.current_tenant_id(), @user, @id, @now)
            on conflict (tenant_id, user_id, service_id) do update set used_at = excluded.used_at
            """, new { user, id, now = clock.UtcNow }, ct);
    }
}

/// <summary>Plain text search over names, descriptions and synonyms in every locale (US1). Spec 012 adds global search.</summary>
public static class Search
{
    public static IReadOnlyList<string> Terms(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToList();

    public static bool Matches(IReadOnlyList<string> terms, ServiceRow s)
    {
        var text = Normalize(string.Join(' ', s.Names.Values.Concat(s.Descriptions.Values).Concat(s.Synonyms.Values.SelectMany(v => v))));
        return terms.All(t => text.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>Lower case, Arabic diacritics and tatweel removed, and alef, yeh and teh marbuta forms folded.</summary>
    public static string Normalize(string text)
    {
        var chars = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
            {
                continue;
            }
            chars.Append(c switch
            {
                'آ' or 'أ' or 'إ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => c,
            });
        }
        return chars.ToString();
    }
}
